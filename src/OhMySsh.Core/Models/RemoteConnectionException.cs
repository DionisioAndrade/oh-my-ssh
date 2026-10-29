namespace OhMySsh.Core.Models;

public enum RemoteConnectionErrorCode
{
    UnsupportedProtocol,
    InvalidSettings,
    AuthenticationFailed,
    PrivateKeyInvalid,
    HostKeyRejected,
    HostKeyChanged,
    HostTrustStorageFailed,
    CredentialStorageFailed,
    NameResolutionFailed,
    ConnectionRefused,
    Timeout,
    ConnectionLost,
    ConnectionFailed
}

/// <summary>A public, English error that never retains an upstream exception or secret.</summary>
public sealed class RemoteConnectionException(RemoteConnectionErrorCode code, string message) : Exception(message)
{
    public RemoteConnectionErrorCode Code { get; } = code;
}
