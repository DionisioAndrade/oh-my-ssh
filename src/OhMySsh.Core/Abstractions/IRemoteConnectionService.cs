using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

/// <summary>Opens a remote session and reports success only after the transport connects.</summary>
public interface IRemoteConnectionService
{
    Task<IRemoteConnectionSession> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
}
