using OhMySsh.Core.Models;

namespace OhMySsh.Core.Abstractions;

/// <summary>The protocol adapter owns the actual connection state and cleanup.</summary>
public interface IRemoteConnectionSession : IAsyncDisposable
{
    ConnectionProfile Profile { get; }
    bool IsConnected { get; }
    IRemoteFileService? Files => null;
    IRemoteShellService? Shells => null;
    event EventHandler? Disconnected;
}
