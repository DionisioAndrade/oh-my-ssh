using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using Renci.SshNet;

namespace OhMySsh.Infrastructure.Connections;

/// <summary>Owns at most one interactive channel on the existing authenticated SSH client.</summary>
internal sealed class SshRemoteShellService(SshClient client, Func<bool> parentConnected) : IRemoteShellService, IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task<IRemoteShell>? _opening;
    private SshRemoteShell? _active;
    private Task? _disposeTask;
    private bool _disposed;

    public Task<IRemoteShell> OpenAsync(RemoteTerminalOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleGate)
        {
            if (_disposed || _lifetime.IsCancellationRequested || !parentConnected()) throw Unavailable();
            if (_opening is { IsCompleted: false } || _active is { IsOpen: true })
            {
                throw new RemoteTerminalException(RemoteTerminalErrorCode.AlreadyOpen, "An interactive shell is already open.");
            }

            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            // SSH.NET's PTY/shell handshake is synchronous. Keep it off the dispatcher and track its
            // eventual cleanup even when a caller stops waiting before the handshake completes.
            _opening = Task.Run(() => OpenCoreAsync(options, cancellation));
            _ = ObserveAsync(_opening);
            return AwaitOpeningAsync(_opening, cancellationToken);
        }
    }

    private static async Task<IRemoteShell> AwaitOpeningAsync(Task<IRemoteShell> opening, CancellationToken cancellationToken)
    {
        try { return await opening.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A completed handshake can race the caller's canceled wait. That channel has no owner
            // at the call site, so explicitly close it even if OpenCore already handed it to us.
            _ = CloseCanceledOpeningAsync(opening);
            throw;
        }
    }

    private static async Task CloseCanceledOpeningAsync(Task<IRemoteShell> opening)
    {
        try { await (await opening.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private async Task<IRemoteShell> OpenCoreAsync(RemoteTerminalOptions options, CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            SshRemoteShell? created = null;
            try
            {
                var token = cancellation.Token;
                token.ThrowIfCancellationRequested();
                if (_active is { } previous) await previous.DisposeAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!parentConnected()) throw Unavailable();
                var stream = client.CreateShellStream(options.TerminalType, (uint)options.Columns, (uint)options.Rows,
                    width: 0, height: 0, bufferSize: SshRemoteShell.BlockSize);
                try { created = new SshRemoteShell(stream, parentConnected); }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                lock (_lifecycleGate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_disposed || !parentConnected()) throw Unavailable();
                    _active = created;
                }

                return created;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (created is not null) await created.DisposeAsync().ConfigureAwait(false);
                if (cancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Opening the interactive shell was canceled.", cancellation.Token);
                }

                throw exception as RemoteTerminalException ?? SshRemoteShell.MapFailure(parentConnected());
            }
        }
    }

    public void CancelPendingOperations()
    {
        SshRemoteShell? active;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            // Cancellation precedes the snapshot so an opening channel cannot be handed off after it.
            _lifetime.Cancel();
            active = _active;
        }
        active?.ConnectionLost();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _lifetime.Cancel();
                var pending = _opening;
                var active = _active;
                // Close immediately to unblock reads/writes; wait for any pending handshake before
                // allowing the parent session to dispose its SSH client and authentication resources.
                var close = active?.DisposeAsync().AsTask() ?? Task.CompletedTask;
                _disposeTask = Task.Run(async () =>
                {
                    try
                    {
                        if (pending is not null) await ObserveAsync(pending).ConfigureAwait(false);
                        await close.ConfigureAwait(false);
                        if (_active is { } shell && !ReferenceEquals(shell, active))
                            await shell.DisposeAsync().ConfigureAwait(false);
                    }
                    finally { _lifetime.Dispose(); }
                });
            }

            return new ValueTask(_disposeTask);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private static RemoteTerminalException Unavailable() => new(RemoteTerminalErrorCode.Unavailable,
        "An authenticated SSH connection is required to open an interactive shell.");
}
