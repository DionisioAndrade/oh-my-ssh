namespace OhMySsh.Core.Models;

public enum RemoteTerminalErrorCode
{
    Unavailable,
    AlreadyOpen,
    InvalidSettings,
    ConnectionLost,
    Closed,
    BufferLimitExceeded,
    Failed
}

/// <summary>A safe English diagnostic. Upstream messages and terminal contents are never retained.</summary>
public sealed class RemoteTerminalException(RemoteTerminalErrorCode code, string message) : Exception(message)
{
    public RemoteTerminalErrorCode Code { get; } = code;
}
