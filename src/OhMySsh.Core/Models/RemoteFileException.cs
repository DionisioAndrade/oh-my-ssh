namespace OhMySsh.Core.Models;

public enum RemoteFileErrorCode
{
    Unavailable,
    PermissionDenied,
    NotFound,
    AlreadyExists,
    ConnectionLost,
    UnsafePath,
    UnsupportedReplacement,
    Failed
}

/// <summary>A safe English error; upstream transport messages are never retained.</summary>
public sealed class RemoteFileException(RemoteFileErrorCode code, string message, bool requiresReconnect = false) : Exception(message)
{
    public RemoteFileErrorCode Code { get; } = code;
    public bool RequiresReconnect { get; } = requiresReconnect;
}
