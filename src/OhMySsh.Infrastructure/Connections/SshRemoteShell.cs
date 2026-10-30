using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OhMySsh.Infrastructure.Connections;

/// <summary>Ordered byte transport with bounded output and explicit channel ownership.</summary>
internal sealed class SshRemoteShell : IRemoteShell
{
    internal const int BlockSize = 16 * 1024;
    private const int OutputBufferSize = 512 * 1024;
    private readonly ShellStream _stream;
    private readonly Func<bool> _parentConnected;
    private readonly byte[] _output = new byte[OutputBufferSize];
    private readonly byte[] _receiveBuffer = new byte[BlockSize];
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _receiveGate = new();
    private readonly object _bufferGate = new();
    private readonly object _disposeGate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _dataAvailable = NewSignal();
    private int _outputOffset;
    private int _outputCount;
    private Task? _disposeTask;
    private RemoteTerminalException? _failure;
    private int _closed;

    internal SshRemoteShell(ShellStream stream, Func<bool> parentConnected)
    {
        _stream = stream;
        _parentConnected = parentConnected;
        lock (_receiveGate)
        {
            _stream.DataReceived += OnDataReceived;
            _stream.ErrorOccurred += OnError;
            _stream.Closed += OnClosed;
            // Output may arrive while SSH.NET is finishing the synchronous shell handshake. The
            // receive lock also prevents cleanup from racing the initial event subscriptions.
            DrainOutput();
            if (!_stream.CanWrite) RequestClose(parentConnected() ? null : MapFailure(false));
        }
        _ = ObserveCompletionAsync();
    }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    public Task Completion => _completion.Task;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        using var canceled = cancellationToken.Register(() => RequestClose(null));
        await WaitForGateAsync(_readGate, cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                // Cancellation closes the channel and wakes this reader. Its wake-up may beat
                // WaitAsync's own cancellation callback, so do not report that close as normal EOF.
                cancellationToken.ThrowIfCancellationRequested();
                Task available;
                lock (_bufferGate)
                {
                    if (_outputCount > 0)
                    {
                        var count = Math.Min(buffer.Length, _outputCount);
                        var first = Math.Min(count, OutputBufferSize - _outputOffset);
                        _output.AsMemory(_outputOffset, first).CopyTo(buffer);
                        if (first < count) _output.AsMemory(0, count - first).CopyTo(buffer[first..]);
                        _outputOffset = (_outputOffset + count) % OutputBufferSize;
                        _outputCount -= count;
                        if (_outputCount == 0 && IsOpen) _dataAvailable = NewSignal();
                        return count;
                    }
                    if (!IsOpen)
                    {
                        if (_failure is not null) throw _failure;
                        return 0;
                    }
                    available = _dataAvailable.Task;
                }
                await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = RequestClose(null);
            throw;
        }
        finally { _readGate.Release(); }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return;
        using var canceled = cancellationToken.Register(() => RequestClose(null));
        await WaitForGateAsync(_operations, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            // Flush after every bounded input block: keystrokes must reach the PTY immediately.
            // FlushAsync contains a synchronous SSH.NET send, so every send runs off the dispatcher.
            await Task.Run(async () =>
            {
                var remaining = buffer;
                while (!remaining.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfClosed();
                    var count = Math.Min(BlockSize, remaining.Length);
                    await _stream.WriteAsync(remaining[..count], cancellationToken)
                        .ConfigureAwait(false);
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    remaining = remaining[count..];
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _ = RequestClose(null);
                throw new OperationCanceledException("Writing to the interactive shell was canceled.", cancellationToken);
            }
            var failure = exception as RemoteTerminalException ?? MapFailure(_parentConnected());
            _ = RequestClose(failure);
            throw failure;
        }
        finally { _operations.Release(); }
    }

    public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        RemoteTerminalOptions.ValidateSize(columns, rows);
        using var canceled = cancellationToken.Register(() => RequestClose(null));
        await WaitForGateAsync(_operations, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfClosed();
                _stream.ChangeWindowSize((uint)columns, (uint)rows, width: 0, height: 0);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _ = RequestClose(null);
                throw new OperationCanceledException("Resizing the interactive shell was canceled.", cancellationToken);
            }
            var failure = exception as RemoteTerminalException ?? MapFailure(_parentConnected());
            _ = RequestClose(failure);
            throw failure;
        }
        finally { _operations.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await RequestClose(null).ConfigureAwait(false);
        // A normal remote exit leaves its final output available to readers. Explicit disposal is
        // the ownership boundary after which retained terminal contents must be released.
        lock (_bufferGate)
        {
            Array.Clear(_output);
            _outputOffset = 0;
            _outputCount = 0;
        }
        lock (_receiveGate) { Array.Clear(_receiveBuffer); }
    }

    internal void ConnectionLost() => RequestClose(MapFailure(false));

    private void OnDataReceived(object? sender, ShellDataEventArgs args) => DrainOutput();
    private void OnError(object? sender, ExceptionEventArgs args) => RequestClose(MapFailure(_parentConnected()));
    private void OnClosed(object? sender, EventArgs args)
    {
        DrainOutput();
        RequestClose(_parentConnected() ? null : MapFailure(false));
    }

    private void DrainOutput()
    {
        lock (_receiveGate)
        {
            if (!IsOpen) return;
            try
            {
                while (_stream.DataAvailable)
                {
                    var count = _stream.Read(_receiveBuffer, 0, _receiveBuffer.Length);
                    if (count == 0) break;
                    if (!TryAppendOutput(_receiveBuffer.AsSpan(0, count)))
                    {
                        // Closing is explicit and visible. Never discard output and continue with a
                        // corrupt terminal state, or block SSH.NET's shared transport receive thread.
                        RequestClose(new RemoteTerminalException(RemoteTerminalErrorCode.BufferLimitExceeded,
                            "The interactive shell closed because terminal output exceeded its buffer limit."));
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                RequestClose(MapFailure(_parentConnected()));
            }
        }
    }

    private Task RequestClose(RemoteTerminalException? failure)
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            lock (_bufferGate)
            {
                _failure = failure;
                Volatile.Write(ref _closed, 1);
                _dataAvailable.TrySetResult();
            }
            if (failure is null) _completion.TrySetResult();
            else _completion.TrySetException(failure);
            _disposeTask = Task.Run(async () =>
            {
                // Forced channel closure is allowed to interrupt an in-flight SSH.NET send. Normal
                // writes and resizes share a gate; cleanup waits until that worker has unwound before
                // the parent SSH client can be released. ShellStream never owns the parent session.
                lock (_receiveGate)
                {
                    _stream.DataReceived -= OnDataReceived;
                    _stream.ErrorOccurred -= OnError;
                    _stream.Closed -= OnClosed;
                }
                try { _stream.Dispose(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                await _operations.WaitAsync().ConfigureAwait(false);
                _operations.Release();
            });
            return _disposeTask;
        }
    }

    private bool TryAppendOutput(ReadOnlySpan<byte> bytes)
    {
        lock (_bufferGate)
        {
            if (!IsOpen) return true;
            if (bytes.Length > OutputBufferSize - _outputCount) return false;
            var offset = (_outputOffset + _outputCount) % OutputBufferSize;
            var first = Math.Min(bytes.Length, OutputBufferSize - offset);
            bytes[..first].CopyTo(_output.AsSpan(offset));
            bytes[first..].CopyTo(_output);
            _outputCount += bytes.Length;
            _dataAvailable.TrySetResult();
            return true;
        }
    }

    private void ThrowIfClosed()
    {
        if (!IsOpen) throw _failure ?? new RemoteTerminalException(RemoteTerminalErrorCode.Closed, "The interactive shell is closed.");
    }

    private async Task WaitForGateAsync(SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A canceled await can dispose its token registration before that callback gets its
            // turn. Cancellation cleanup must therefore also be guaranteed by the await itself.
            _ = RequestClose(null);
            throw;
        }
    }

    private async Task ObserveCompletionAsync()
    {
        try { await Completion.ConfigureAwait(false); }
        catch (RemoteTerminalException) { }
    }

    internal static RemoteTerminalException MapFailure(bool parentConnected) => parentConnected
        ? new(RemoteTerminalErrorCode.Failed, "The interactive shell could not complete the operation.")
        : new(RemoteTerminalErrorCode.ConnectionLost, "The SSH connection was lost. Connect again to open a new interactive shell.");

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
