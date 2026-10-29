namespace OhMySsh.Core.Models;

public sealed record SshHostIdentity(string Host, int Port, string KeyAlgorithm, string Sha256Fingerprint);

public enum HostKeyDecision
{
    Reject,
    TrustOnce,
    TrustAndRemember
}
