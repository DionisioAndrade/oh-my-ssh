using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

/// <summary>Opens an explicitly requested PTY on the authenticated parent connection.</summary>
public interface IRemoteShellService
{
    /// <remarks>Only one shell may be opening or open. An exited shell is never restarted automatically.</remarks>
    Task<IRemoteShell> OpenAsync(RemoteTerminalOptions options, CancellationToken cancellationToken = default);
}
