namespace OhMySsh.Core.Models;

/// <summary>
/// Non-secret connection settings. Passwords and private-key contents must be
/// supplied by a credential provider rather than stored in this model.
/// </summary>
public sealed record ConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    public string Group { get; init; } = "Geral";
    public ConnectionProtocol Protocol { get; init; } = ConnectionProtocol.Ssh;
    public string? PrivateKeyPath { get; init; }
    public bool IsFavorite { get; init; }
}
