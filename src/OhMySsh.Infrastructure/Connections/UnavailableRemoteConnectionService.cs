using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;

namespace OhMySsh.Infrastructure.Connections;

/// <summary>Explicitly keeps the shell offline until a real protocol adapter is installed.</summary>
public sealed class UnavailableRemoteConnectionService : IRemoteConnectionService
{
    public Task<IRemoteConnectionSession> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IRemoteConnectionSession>(new NotSupportedException(
            "Remote connections are unavailable because no connection adapter was configured. No connection was started."));
    }
}
