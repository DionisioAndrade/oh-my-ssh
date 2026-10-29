using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Infrastructure.Persistence;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace OhMySsh.Infrastructure.Connections;

/// <summary>Opens authenticated SSH transport; SFTP profiles share the same SSH security policy.</summary>
public sealed class SshRemoteConnectionService(
    IRemoteCredentialProvider credentials,
    IHostKeyPrompt hostPrompt,
    IKnownHostStore knownHosts) : IRemoteConnectionService
{
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly object _lifecycleGate = new();
    private readonly HashSet<Task> _pendingCleanup = [];
    private bool _shuttingDown;

    public async Task ShutdownAsync()
    {
        Task[] pending;
        lock (_lifecycleGate)
        {
            _shuttingDown = true;
            pending = _pendingCleanup.ToArray();
        }

        _shutdownCancellation.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public async Task<IRemoteConnectionSession> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateProfile(profile);
        cancellationToken.ThrowIfCancellationRequested();
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCancellation.Token);
        var operationToken = operationCancellation.Token;
        var snapshot = profile with { Host = profile.Host.Trim(), Username = profile.Username.Trim() };
        using var authenticationCredentials = await credentials.GetCredentialsAsync(snapshot, operationToken).ConfigureAwait(false);
        if (authenticationCredentials is null)
        {
            throw new OperationCanceledException("Credential entry was canceled.", operationToken);
        }

        operationToken.ThrowIfCancellationRequested();
        // Users can take their time entering credentials; the deadline starts with the transport attempt.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(snapshot.ConnectionTimeoutSeconds));
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken, timeout.Token);
        var attemptToken = attemptCancellation.Token;
        var cleanupCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleGate)
        {
            if (_shuttingDown)
            {
                throw new OperationCanceledException("The SSH connection service is shutting down.", operationToken);
            }

            _pendingCleanup.Add(cleanupCompletion.Task);
        }

        PrivateKeyFile? privateKey = null;
        AuthenticationMethod? authentication = null;
        SshClient? client = null;
        HostValidation? hostValidation = null;
        Task? connectionWork = null;
        var handedOff = false;
        try
        {
            var rememberedIdentity = await knownHosts.GetAsync(snapshot.Host, snapshot.Port, attemptToken).ConfigureAwait(false);
            hostValidation = new HostValidation(snapshot, rememberedIdentity, hostPrompt, knownHosts, attemptToken);

            // Key parsing and the library's synchronous authentication stages never run on the WPF dispatcher.
            var method = authenticationCredentials.Method;
            var secret = authenticationCredentials.Secret;
            connectionWork = Task.Run(async () =>
            {
                attemptToken.ThrowIfCancellationRequested();
                if (method == RemoteAuthenticationMethod.PrivateKey)
                {
                    privateKey = ReadPrivateKey(snapshot, secret);
                    authentication = new CancelablePrivateKeyAuthentication(snapshot.Username, privateKey, attemptToken);
                }
                else
                {
                    authentication = new CancelablePasswordAuthentication(snapshot.Username, secret, attemptToken);
                }

                attemptToken.ThrowIfCancellationRequested();
                var connectionInfo = new ConnectionInfo(snapshot.Host, snapshot.Port, snapshot.Username, authentication)
                {
                    Timeout = TimeSpan.FromSeconds(snapshot.ConnectionTimeoutSeconds),
                    ChannelCloseTimeout = TimeSpan.FromSeconds(1)
                };
                client = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(10) };
                client.HostKeyReceived += hostValidation.Handle;
                await client.ConnectAsync(attemptToken).ConfigureAwait(false);
            }, attemptToken);
            // SSH.NET still contains synchronous waits during key exchange/authentication. A canceled
            // attempt returns promptly, while tracked cleanup owns any late library completion.
            await connectionWork.WaitAsync(attemptToken).ConfigureAwait(false);

            attemptToken.ThrowIfCancellationRequested();
            if (hostValidation.Failure is { } trustFailure)
            {
                throw trustFailure;
            }

            if (client is null || !client.IsConnected || !client.ConnectionInfo.IsAuthenticated)
            {
                throw new RemoteConnectionException(RemoteConnectionErrorCode.ConnectionFailed,
                    "The SSH server did not confirm an authenticated connection. Try connecting again.");
            }

            if (authenticationCredentials.Remember)
            {
                await credentials.SaveCredentialsAsync(snapshot, authenticationCredentials, attemptToken).ConfigureAwait(false);
            }

            attemptToken.ThrowIfCancellationRequested();
            var session = new SshRemoteConnectionSession(snapshot, client, authentication!, privateKey,
                hostValidation.Handle, hostValidation.AcceptedIdentity!.Sha256Fingerprint);
            handedOff = true;
            return session;
        }
        catch (Exception exception)
        {
            if (exception is SshAuthenticationException
                || exception is RemoteConnectionException { Code: RemoteConnectionErrorCode.PrivateKeyInvalid })
            {
                // A rotated or invalid saved secret must not trap every future retry in the same failure.
                try
                {
                    await credentials.InvalidateAsync(snapshot, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception invalidationException) when (invalidationException is not OutOfMemoryException)
                {
                    // Preserve the actionable authentication error. No upstream messages are logged.
                }
            }

            if (operationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The SSH connection attempt was canceled.", operationToken);
            }

            if (timeout.IsCancellationRequested || exception is SshOperationTimeoutException or TimeoutException)
            {
                throw new RemoteConnectionException(RemoteConnectionErrorCode.Timeout,
                    "The SSH connection timed out. Check the server address and network, or increase the connection timeout.");
            }

            if (hostValidation?.Failure is { } hostFailure)
            {
                throw hostFailure;
            }

            throw MapError(exception);
        }
        finally
        {
            void Cleanup()
            {
                if (client is not null && hostValidation is not null)
                {
                    client.HostKeyReceived -= hostValidation.Handle;
                }

                DisposeResources(client, authentication, privateKey);
            }

            void CleanupFinished()
            {
                cleanupCompletion.TrySetResult();
                lock (_lifecycleGate) { _pendingCleanup.Remove(cleanupCompletion.Task); }
            }

            if (!handedOff && connectionWork is { IsCompleted: false })
            {
                _ = CompleteLateCleanupAsync(connectionWork, Cleanup, CleanupFinished);
            }
            else
            {
                try
                {
                    if (!handedOff)
                    {
                        await Task.Run(Cleanup).ConfigureAwait(false);
                    }
                }
                finally { CleanupFinished(); }
            }
        }
    }

    private static async Task CompleteLateCleanupAsync(Task work, Action cleanup, Action completed)
    {
        try
        {
            try { await work.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            await Task.Run(cleanup).ConfigureAwait(false);
        }
        finally { completed(); }
    }

    private static PrivateKeyFile ReadPrivateKey(ConnectionProfile profile, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
        {
            throw InvalidPrivateKey();
        }

        try
        {
            using var stream = new FileStream(profile.PrivateKeyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024)
            {
                throw InvalidPrivateKey();
            }

            return passphrase.Length == 0 ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw InvalidPrivateKey();
        }
    }

    private static RemoteConnectionException InvalidPrivateKey() => new(RemoteConnectionErrorCode.PrivateKeyInvalid,
        "The private key could not be opened or decrypted. Check the key file, its format and permissions, and the passphrase.");

    private static void ValidateProfile(ConnectionProfile profile)
    {
        if (profile.Protocol is not (ConnectionProtocol.Ssh or ConnectionProtocol.Sftp))
        {
            throw new RemoteConnectionException(RemoteConnectionErrorCode.UnsupportedProtocol,
                "FTP and FTPS connections are not supported yet. Choose an SSH or SFTP profile.");
        }

        if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Username)
            || profile.Host.Any(char.IsControl) || profile.Username.Any(char.IsControl)
            || profile.Port is < 1 or > 65535 || profile.ConnectionTimeoutSeconds is < 1 or > 300)
        {
            throw new RemoteConnectionException(RemoteConnectionErrorCode.InvalidSettings,
                "Enter a valid server, username, port (1–65535), and connection timeout (1–300 seconds).");
        }
    }

    private static RemoteConnectionException MapError(Exception exception)
    {
        if (exception is RemoteConnectionException known)
        {
            return known;
        }

        return exception switch
        {
            SshAuthenticationException => new(RemoteConnectionErrorCode.AuthenticationFailed,
                "SSH authentication failed. Check the username and password or private key, then try again."),
            SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => new(
                RemoteConnectionErrorCode.NameResolutionFailed, "The server name could not be resolved. Check the hostname and your DNS connection."),
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => new(RemoteConnectionErrorCode.ConnectionRefused,
                "The server refused the SSH connection. Check the port, SSH service, and firewall rules."),
            SocketException { SocketErrorCode: SocketError.TimedOut } => new(RemoteConnectionErrorCode.Timeout,
                "The SSH connection timed out. Check the server and network, then try again."),
            _ => new(RemoteConnectionErrorCode.ConnectionFailed,
                "The SSH connection could not be established. Check the server, network, and authentication settings, then try again.")
        };
    }

    internal static void DisposeResources(SshClient? client, AuthenticationMethod? authentication, PrivateKeyFile? privateKey)
    {
        try { client?.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        try { authentication?.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        try { privateKey?.Dispose(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }

    internal static ConnectionInfo CreateFileConnectionInfo(ConnectionProfile profile,
        AuthenticationMethod sessionAuthentication, PrivateKeyFile? privateKey, CancellationToken cancellationToken)
    {
        // Each transport owns its authentication state and cancellation registration. Only the parsed
        // private key is shared, and the parent session keeps it alive until child cleanup finishes.
        AuthenticationMethod authentication = sessionAuthentication is CancelablePasswordAuthentication password
            ? password.CopyFor(cancellationToken)
            : new CancelablePrivateKeyAuthentication(profile.Username, privateKey!, cancellationToken);
        return new ConnectionInfo(profile.Host, profile.Port, profile.Username, authentication)
        {
            Timeout = TimeSpan.FromSeconds(profile.ConnectionTimeoutSeconds),
            ChannelCloseTimeout = TimeSpan.FromSeconds(1)
        };
    }

    private sealed class CancelablePasswordAuthentication : PasswordAuthenticationMethod
    {
        private readonly byte[] _password;
        private readonly CancellationToken _cancellationToken;

        public CancelablePasswordAuthentication(string username, string password, CancellationToken cancellationToken)
            : this(username, Encoding.UTF8.GetBytes(password), cancellationToken) { }

        private CancelablePasswordAuthentication(string username, byte[] password, CancellationToken cancellationToken)
            : base(username, password)
        {
            _password = password;
            _cancellationToken = cancellationToken;
        }

        public override AuthenticationResult Authenticate(Session session)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            using var cancellation = new AuthenticationCancellation(session, _cancellationToken);
            return base.Authenticate(session);
        }

        public AuthenticationMethod CopyFor(CancellationToken cancellationToken)
            => new CancelablePasswordAuthentication(Username, _password.ToArray(), cancellationToken);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { CryptographicOperations.ZeroMemory(_password); }
        }
    }

    private sealed class CancelablePrivateKeyAuthentication(string username, IPrivateKeySource key, CancellationToken cancellationToken)
        : PrivateKeyAuthenticationMethod(username, key)
    {
        public override AuthenticationResult Authenticate(Session session)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cancellation = new AuthenticationCancellation(session, cancellationToken);
            return base.Authenticate(session);
        }
    }

    private sealed class AuthenticationCancellation : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        private Task? _disconnectTask;

        public AuthenticationCancellation(Session session, CancellationToken cancellationToken)
        {
            _registration = cancellationToken.Register(() => _disconnectTask = Task.Run(() =>
            {
                try { session.Disconnect(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }));
        }

        public void Dispose()
        {
            _registration.Dispose();
            // Only the authentication worker waits; canceling from the WPF dispatcher never blocks it.
            _disconnectTask?.GetAwaiter().GetResult();
        }
    }

    private sealed class HostValidation(ConnectionProfile profile, SshHostIdentity? rememberedIdentity,
        IHostKeyPrompt prompt, IKnownHostStore store, CancellationToken cancellationToken)
    {
        private SshHostIdentity? _acceptedIdentity;
        public SshHostIdentity? AcceptedIdentity => _acceptedIdentity;
        public RemoteConnectionException? Failure { get; private set; }

        public void Handle(object? sender, HostKeyEventArgs args)
        {
            args.CanTrust = false;
            var identity = new SshHostIdentity(profile.Host, profile.Port, args.HostKeyName, "SHA256:" + args.FingerPrintSHA256);
            try
            {
                var accepted = _acceptedIdentity ?? rememberedIdentity;
                if (accepted is not null)
                {
                    if (!JsonKnownHostStore.SameKey(accepted, identity))
                    {
                        throw new RemoteConnectionException(RemoteConnectionErrorCode.HostKeyChanged,
                            "The server host key has changed. Verify its SHA-256 fingerprint with the server administrator before updating the trusted host file.");
                    }
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var decision = prompt.ConfirmAsync(identity, cancellationToken).WaitAsync(cancellationToken).GetAwaiter().GetResult();
                    if (decision == HostKeyDecision.Reject || !Enum.IsDefined(decision))
                    {
                        throw new RemoteConnectionException(RemoteConnectionErrorCode.HostKeyRejected,
                            "The server identity was not trusted. Verify its SHA-256 fingerprint before connecting again.");
                    }

                    if (decision == HostKeyDecision.TrustAndRemember)
                    {
                        store.SaveAsync(identity, cancellationToken).GetAwaiter().GetResult();
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                }

                _acceptedIdentity = identity;
                args.CanTrust = true;
            }
            catch (Exception exception)
            {
                Failure = exception as RemoteConnectionException ?? new RemoteConnectionException(
                    RemoteConnectionErrorCode.HostKeyRejected,
                    "Server identity verification did not complete. No SSH connection was accepted.");
            }
        }
    }
}
