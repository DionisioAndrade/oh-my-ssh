using System.Net.Sockets;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OhMySsh.Infrastructure.Files;

/// <summary>Lazy, session-owned SFTP transport with staged, single-file transfers.</summary>
internal sealed class SftpRemoteFileService(
    ConnectionProfile profile, Func<CancellationToken, ConnectionInfo> connectionFactory, string fingerprint, Func<bool> parentConnected)
    : IRemoteFileService, IAsyncDisposable
{
    private const int CopyBufferSize = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private SftpClient? _client;
    private Task _initializationCleanup = Task.CompletedTask;
    private Task? _disposeTask;
    private int _disposed;

    public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken = default)
        => RunAsync((client, _) => Task.FromResult(RemotePath.Normalize(client.WorkingDirectory)), cancellationToken);

    public Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var path = RemotePath.Normalize(remotePath);
        return RunAsync((client, token) => client.ExistsAsync(path, token), cancellationToken);
    }

    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var path = RemotePath.Normalize(remotePath);
        return RunAsync<IReadOnlyList<FileEntry>>(async (client, token) =>
        {
            var entries = new List<FileEntry>();
            await foreach (var entry in client.ListDirectoryAsync(path, token).ConfigureAwait(false))
            {
                if (entry.Name is "." or "..") continue;
                // Construct the path from the requested directory rather than trusting a server-provided FullName.
                entries.Add(new FileEntry
                {
                    Name = entry.Name, FullPath = RemotePath.Combine(path, entry.Name),
                    IsDirectory = entry.IsDirectory, Size = entry.IsDirectory ? 0 : entry.Length,
                    LastModified = entry.LastWriteTime
                });
            }
            return entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }, cancellationToken);
    }

    public Task UploadAsync(string localPath, string remoteDestination, bool overwrite,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var destination = RemotePath.Normalize(remoteDestination);
        if (destination == "/") throw UnsafePath();
        var source = Path.GetFullPath(localPath);
        return RunAsync(async (client, token) =>
        {
            if (!overwrite && await client.ExistsAsync(destination, token).ConfigureAwait(false)) throw AlreadyExists();
            var temporary = RemotePath.Combine(RemotePath.GetParent(destination), $".oh-my-ssh-{Guid.NewGuid():N}.part");
            var staged = false;
            try
            {
                long total;
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    total = input.Length;
                    // OpenAsync may create the file before cancellation interrupts its following fstat request.
                    // This unpredictable, operation-owned name must therefore be cleaned even when open fails.
                    staged = true;
                    await using (var output = await client.OpenAsync(temporary, FileMode.CreateNew, FileAccess.Write, token).ConfigureAwait(false))
                    {
                        await CopyAsync(input, output, total, progress, token).ConfigureAwait(false);
                        await output.FlushAsync(token).ConfigureAwait(false);
                    }
                }
                token.ThrowIfCancellationRequested();
                if (overwrite)
                {
                    // This extension atomically replaces the destination; never delete the original first.
                    // Once commit begins its outcome is authoritative even if a cancellation races the rename.
                    progress?.Report(new TransferProgress(total, total, IsFinalizing: true));
                    try { client.RenameFile(temporary, destination, isPosix: true); }
                    catch (NotSupportedException) { throw UnsupportedReplacement(); }
                    catch (SftpPermissionDeniedException) { throw; }
                    catch (SshConnectionException) { throw; }
                    catch (SshException) { throw UnsupportedReplacement(); }
                }
                else
                {
                    if (await client.ExistsAsync(destination, token).ConfigureAwait(false)) throw AlreadyExists();
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new TransferProgress(total, total, IsFinalizing: true));
                    try { await client.RenameFileAsync(temporary, destination, CancellationToken.None).ConfigureAwait(false); }
                    catch (SshException)
                    {
                        if (await ExistsQuietlyAsync(client, destination).ConfigureAwait(false)) throw AlreadyExists();
                        throw;
                    }
                }
                staged = false;
            }
            finally
            {
                if (staged) await RemoveTemporaryAsync(client, temporary).ConfigureAwait(false);
            }
            return true;
        }, cancellationToken);
    }

    public Task DownloadAsync(string remoteFile, string localDestination, bool overwrite,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var source = RemotePath.Normalize(remoteFile);
        // Remote names cannot turn into drive paths, device names, or nested local destinations.
        RemotePath.GetSafeLocalFileName(RemotePath.GetFileName(source));
        var destination = Path.GetFullPath(localDestination);
        RemotePath.GetSafeLocalFileName(Path.GetFileName(destination));
        var directory = Path.GetDirectoryName(destination)!;
        return RunAsync(async (client, token) =>
        {
            if (Directory.Exists(destination)) throw AlreadyExists();
            if (!overwrite && File.Exists(destination)) throw AlreadyExists();
            var attributes = await client.GetAttributesAsync(source, token).ConfigureAwait(false);
            if (attributes.IsDirectory) throw new RemoteFileException(RemoteFileErrorCode.Failed, "Select an individual remote file to download.");
            var temporary = Path.Combine(directory, $".oh-my-ssh-{Guid.NewGuid():N}.part");
            try
            {
                await using (var input = await client.OpenAsync(source, FileMode.Open, FileAccess.Read, token).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyAsync(input, output, attributes.Size, progress, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                if (!overwrite && File.Exists(destination)) throw AlreadyExists();
                token.ThrowIfCancellationRequested();
                // Staging is in the destination directory, so this stays on the same filesystem.
                progress?.Report(new TransferProgress(attributes.Size, attributes.Size, IsFinalizing: true));
                try { File.Move(temporary, destination, overwrite); }
                catch (IOException) when (!overwrite && File.Exists(destination)) { throw AlreadyExists(); }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return true;
        }, cancellationToken);
    }

    private async Task<T> RunAsync<T>(Func<SftpClient, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        CancellationTokenSource cancellation;
        lock (_disposeGate)
        {
            if (Volatile.Read(ref _disposed) != 0) throw Lost();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using var operationCancellation = cancellation;
        var token = cancellation.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (!parentConnected()) throw Lost();
                var client = await GetClientAsync(token).ConfigureAwait(false);
                return await operation(client, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException("The remote file operation was canceled.", cancellationToken);
        }
        catch (RemoteFileException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw MapError(exception);
        }
        finally { _gate.Release(); }
    }

    private async Task<SftpClient> GetClientAsync(CancellationToken token)
    {
        if (_client is not null)
        {
            if (!_client.IsConnected) throw Lost();
            return _client;
        }
        // Observe any canceled initialization before allocating its replacement.
        await _initializationCleanup.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var candidate = new SftpClient(connectionFactory(linked.Token))
        {
            OperationTimeout = TimeSpan.FromSeconds(Math.Min(profile.ConnectionTimeoutSeconds, 30)),
            BufferSize = CopyBufferSize, KeepAliveInterval = TimeSpan.FromSeconds(10)
        };
        var hostChanged = false;
        void VerifyHost(object? sender, HostKeyEventArgs args)
        {
            args.CanTrust = string.Equals("SHA256:" + args.FingerPrintSHA256, fingerprint, StringComparison.Ordinal);
            if (!args.CanTrust) hostChanged = true;
        }
        candidate.HostKeyReceived += VerifyHost;
        var work = Task.Run(() => candidate.ConnectAsync(linked.Token), linked.Token);
        try
        {
            await work.WaitAsync(linked.Token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _client = candidate;
            return candidate;
        }
        catch (Exception exception)
        {
            async Task CleanupAsync()
            {
                try { await work.ConfigureAwait(false); } catch (Exception) { }
                candidate.HostKeyReceived -= VerifyHost;
                await DisposeClientAsync(candidate).ConfigureAwait(false);
            }
            _initializationCleanup = CleanupAsync();
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            if (hostChanged) throw new RemoteFileException(RemoteFileErrorCode.Unavailable,
                "The SFTP server identity differs from the active SSH session. Disconnect and verify the server before reconnecting.", true);
            if (exception is SshAuthenticationException or SshConnectionException or SocketException)
                throw new RemoteFileException(RemoteFileErrorCode.ConnectionLost,
                    "The associated SFTP connection could not be established. Check the server and reconnect the VPS.", true);
            throw new RemoteFileException(RemoteFileErrorCode.Unavailable,
                "The SSH connection is active, but the server did not open an SFTP subsystem. Check that SFTP is enabled for this account.");
        }
    }

    private static async Task CopyAsync(Stream input, Stream output, long total, IProgress<TransferProgress>? progress, CancellationToken token)
    {
        var buffer = new byte[CopyBufferSize];
        long transferred = 0;
        progress?.Report(new TransferProgress(0, total));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            transferred += read;
            progress?.Report(new TransferProgress(transferred, total));
        }
        if (transferred != total) throw new RemoteFileException(RemoteFileErrorCode.Failed,
            "The source file changed during the transfer. The destination was not replaced; refresh and try again.");
    }

    private static async Task RemoveTemporaryAsync(SftpClient client, string path)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DeleteFileAsync(path, cleanup.Token).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException) { }
        catch (Exception) { /* The final destination is preserved; an unreachable server may retain the staging file. */ }
    }

    private static async Task<bool> ExistsQuietlyAsync(SftpClient client, string path)
    {
        try { return await client.ExistsAsync(path).ConfigureAwait(false); }
        catch (Exception) { return false; }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _lifetime.Cancel();
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    internal void CancelPendingOperations()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null) _lifetime.Cancel();
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _initializationCleanup.ConfigureAwait(false);
            if (_client is { } client) await DisposeClientAsync(client).ConfigureAwait(false);
            _client = null;
        }
        finally
        {
            _lifetime.Dispose();
            _gate.Release();
        }
    }

    private static Task DisposeClientAsync(SftpClient client) => Task.Run(() =>
    {
        var authentication = client.ConnectionInfo.AuthenticationMethods.ToArray();
        try { client.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed subsystem close must not leave the owning SSH socket open.
            try { client.Disconnect(); }
            catch (Exception retryException) when (retryException is not OutOfMemoryException) { }
            try { client.Dispose(); }
            catch (Exception retryException) when (retryException is not OutOfMemoryException) { }
        }
        finally
        {
            foreach (var method in authentication)
            {
                try { method.Dispose(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    });

    private static RemoteFileException MapError(Exception exception) => exception switch
    {
        SftpPathNotFoundException or FileNotFoundException or DirectoryNotFoundException => new(RemoteFileErrorCode.NotFound,
            "The file or directory was not found. Refresh the listing and check the path."),
        SftpPermissionDeniedException or UnauthorizedAccessException => new(RemoteFileErrorCode.PermissionDenied,
            "Permission was denied. Check the account and destination permissions."),
        SshConnectionException or SocketException or ObjectDisposedException => Lost(),
        SshOperationTimeoutException or TimeoutException => new(RemoteFileErrorCode.ConnectionLost,
            "The SFTP operation timed out. Reconnect the VPS before retrying; an unfinished staging file may remain on an unreachable server.", true),
        NotSupportedException => UnsupportedReplacement(),
        ArgumentException => UnsafePath(),
        _ => new(RemoteFileErrorCode.Failed,
            "The remote file operation failed. Existing destination files were preserved unless finalization had already completed. An unfinished staging file may remain if the server became unreachable.")
    };
    private static RemoteFileException Lost() => new(RemoteFileErrorCode.ConnectionLost,
        "The remote file connection was lost. Reconnect the VPS before continuing.", true);
    private static RemoteFileException AlreadyExists() => new(RemoteFileErrorCode.AlreadyExists,
        "The destination already exists. Confirm replacement before retrying the transfer.");
    private static RemoteFileException UnsafePath() => new(RemoteFileErrorCode.UnsafePath,
        "Choose a valid individual file path and local filename.");
    private static RemoteFileException UnsupportedReplacement() => new(RemoteFileErrorCode.UnsupportedReplacement,
        "This server cannot safely replace the destination atomically. The existing destination was preserved; choose another filename.");
}
