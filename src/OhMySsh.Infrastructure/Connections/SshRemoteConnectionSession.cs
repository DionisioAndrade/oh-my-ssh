using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OhMySsh.Infrastructure.Connections;

internal sealed class SshRemoteConnectionSession : IRemoteConnectionSession
{
    private readonly SshClient _client;
    private readonly AuthenticationMethod _authentication;
    private readonly PrivateKeyFile? _privateKey;
    private readonly EventHandler<HostKeyEventArgs> _hostKeyHandler;
    private readonly CancellationTokenSource _monitorCancellation = new();
    private readonly object _disposeGate = new();
    private readonly Task _monitorTask;
    private Task? _disposeTask;
    private int _disposed;
    private int _connectionLost;

    public SshRemoteConnectionSession(ConnectionProfile profile, SshClient client, AuthenticationMethod authentication,
        PrivateKeyFile? privateKey, EventHandler<HostKeyEventArgs> hostKeyHandler)
    {
        Profile = profile;
        _client = client;
        _authentication = authentication;
        _privateKey = privateKey;
        _hostKeyHandler = hostKeyHandler;
        _client.ErrorOccurred += OnError;
        _monitorTask = MonitorAsync();
    }

    public ConnectionProfile Profile { get; }
    public event EventHandler? Disconnected;

    public bool IsConnected
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _connectionLost) != 0)
            {
                return false;
            }

            try { return _client.IsConnected; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposed, 1);
                _client.ErrorOccurred -= OnError;
                _client.HostKeyReceived -= _hostKeyHandler;
                _monitorCancellation.Cancel();
                _disposeTask = Task.Run(async () =>
                {
                    await _monitorTask.ConfigureAwait(false);
                    SshRemoteConnectionService.DisposeResources(_client, _authentication, _privateKey);
                    _monitorCancellation.Dispose();
                });
            }

            return new ValueTask(_disposeTask);
        }
    }

    private void OnError(object? sender, ExceptionEventArgs args) => NotifyConnectionLost();

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_monitorCancellation.Token).ConfigureAwait(false))
            {
                if (!IsConnected)
                {
                    NotifyConnectionLost();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_monitorCancellation.IsCancellationRequested) { }
    }

    private void NotifyConnectionLost()
    {
        if (Volatile.Read(ref _disposed) == 0 && Interlocked.Exchange(ref _connectionLost, 1) == 0)
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }
}
