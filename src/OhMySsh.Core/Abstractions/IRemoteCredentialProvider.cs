using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

public interface IRemoteCredentialProvider
{
    Task<RemoteConnectionCredentials?> GetCredentialsAsync(ConnectionProfile profile, CancellationToken cancellationToken = default);
    Task SaveCredentialsAsync(ConnectionProfile profile, RemoteConnectionCredentials credentials, CancellationToken cancellationToken = default);
    Task InvalidateAsync(ConnectionProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
