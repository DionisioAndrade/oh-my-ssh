using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

public interface IHostKeyPrompt
{
    Task<HostKeyDecision> ConfirmAsync(SshHostIdentity identity, CancellationToken cancellationToken = default);
}
