using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using Renci.SshNet;

namespace OhMySsh.Infrastructure.Connections;

/// <summary>Owns independent interactive channels on one authenticated SSH connection.</summary>
internal sealed class SshRemoteShellService(SshClient client, Func<bool> parentConnected) : IRemoteShellService, IAsyncDisposable
{
    private const int MaximumChannels = 8;
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _creationGate = new(1, 1);
    private readonly HashSet<ChannelEntry> _channels = [];
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
            if (_channels.Count >= MaximumChannels)
                throw new RemoteTerminalException(RemoteTerminalErrorCode.AlreadyOpen, "The interactive shell limit has been reached.");

            var entry = new ChannelEntry(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token));
            _channels.Add(entry);
            // Serialize only creation. Each opened PTY has independent input, output,
            // dimensions, cancellation and lifetime on the authenticated SSH transport.
            entry.Opening = Task.Run(() => OpenCoreAsync(entry, options));
            _ = ObserveOpeningAsync(entry);
            return AwaitOpeningAsync(entry.Opening, cancellationToken);
        }
    }

    private static async Task<IRemoteShell> AwaitOpeningAsync(Task<IRemoteShell> opening, CancellationToken cancellationToken)
    {
        try { return await opening.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = CloseCanceledOpeningAsync(opening);
            throw;
        }
    }

    private static async Task CloseCanceledOpeningAsync(Task<IRemoteShell> opening)
    {
        try { await (await opening.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private async Task<IRemoteShell> OpenCoreAsync(ChannelEntry entry, RemoteTerminalOptions options)
    {
        using (entry.Cancellation)
        {
            SshRemoteShell? created = null;
            var acquired = false;
            try
            {
                var token = entry.Cancellation.Token;
                await _creationGate.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                token.ThrowIfCancellationRequested();
                if (!parentConnected()) throw Unavailable();
                var stream = client.CreateShellStream(options.TerminalType, (uint)options.Columns, (uint)options.Rows,
                    width: 0, height: 0, bufferSize: SshRemoteShell.BlockSize);
                try { created = new SshRemoteShell(stream, parentConnected, () => ReleaseChannel(entry)); }
                catch { stream.Dispose(); throw; }
                lock (_lifecycleGate)
                {
                    token.ThrowIfCancellationRequested();
                    if (_disposed || !parentConnected()) throw Unavailable();
                    entry.Shell = created;
                }
                return created;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (created is not null) await created.DisposeAsync().ConfigureAwait(false);
                if (entry.Cancellation.IsCancellationRequested)
                    throw new OperationCanceledException("Opening the interactive shell was canceled.", entry.Cancellation.Token);
                throw exception as RemoteTerminalException ?? SshRemoteShell.MapFailure(parentConnected());
            }
            finally { if (acquired) _creationGate.Release(); }
        }
    }

    private async Task ObserveOpeningAsync(ChannelEntry entry)
    {
        try { await entry.Opening.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        finally
        {
            lock (_lifecycleGate)
                if (!entry.Opening.IsCompletedSuccessfully || entry.Released) _channels.Remove(entry);
        }
    }

    private void ReleaseChannel(ChannelEntry entry)
    {
        lock (_lifecycleGate)
        {
            entry.Released = true;
            // Keep an opening worker tracked until its final cleanup has actually unwound.
            if (entry.Opening.IsCompleted) _channels.Remove(entry);
        }
    }

    public void CancelPendingOperations()
    {
        SshRemoteShell[] active;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _lifetime.Cancel();
            active = _channels.Select(entry => entry.Shell).OfType<SshRemoteShell>().ToArray();
        }
        foreach (var shell in active) shell.ConnectionLost();
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _lifetime.Cancel();
                var channels = _channels.ToArray();
                var closing = channels.Select(entry => entry.Shell?.DisposeAsync().AsTask() ?? Task.CompletedTask).ToArray();
                _disposeTask = Task.Run(async () =>
                {
                    try
                    {
                        await Task.WhenAll(channels.Select(RetireAsync)).ConfigureAwait(false);
                        await Task.WhenAll(closing).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_lifecycleGate) _channels.Clear();
                        _creationGate.Dispose();
                        _lifetime.Dispose();
                    }
                });
            }
            return new ValueTask(_disposeTask);
        }
    }

    private static async Task RetireAsync(ChannelEntry entry)
    {
        try { await (await entry.Opening.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    private static RemoteTerminalException Unavailable() => new(RemoteTerminalErrorCode.Unavailable,
        "An authenticated SSH connection is required to open an interactive shell.");

    private sealed class ChannelEntry(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<IRemoteShell> Opening { get; set; } = null!;
        public SshRemoteShell? Shell { get; set; }
        public bool Released { get; set; }
    }
}
