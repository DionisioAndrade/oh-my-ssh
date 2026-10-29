using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

public interface IKnownHostStore
{
    Task<SshHostIdentity?> GetAsync(string host, int port, CancellationToken cancellationToken = default);
    Task SaveAsync(SshHostIdentity identity, CancellationToken cancellationToken = default);
}
