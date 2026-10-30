namespace OhMySsh.Core.Abstractions;

/// <summary>A session-owned, byte-oriented interactive PTY channel.</summary>
/// <remarks>
/// Canceling an input, output, or resize operation closes this channel. Await disposal to finish its
/// cleanup. The parent SSH connection remains usable; a replacement shell requires an explicit open.
/// </remarks>
public interface IRemoteShell : IAsyncDisposable
{
    bool IsOpen { get; }
    /// <summary>Completes when the shell closes; transport failures use a sanitized RemoteTerminalException.</summary>
    Task Completion { get; }
    /// <summary>Reads output in order, returning zero after buffered output is exhausted on a normal exit.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
}
