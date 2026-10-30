using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Infrastructure.Connections;
using OhMySsh.Infrastructure.Persistence;

namespace OhMySsh.SshVerify;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var root = FindRepositoryRoot();
            var output = Path.Combine(root, ".local", $"ssh-verify-{Guid.NewGuid():N}");
            Directory.CreateDirectory(output);
            var python = args.Length == 0
                ? Path.Combine(root, ".local", "ssh-integration-venv", "Scripts", "python.exe")
                : Path.GetFullPath(args[0]);
            await using var fixture = await SshFixture.StartAsync(python, output);
            var suite = new SshVerificationSuite(fixture, output);
            await suite.RunAsync(args.Contains("--terminal-only", StringComparer.Ordinal));
            return suite.Failures == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SSH verification could not start: {exception.GetType().Name}. Check the isolated Python fixture setup.");
            return 2;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) return current.FullName;
        }
        throw new DirectoryNotFoundException("The repository root could not be located.");
    }
}

internal sealed partial class SshVerificationSuite(SshFixture fixture, string output)
{
    private readonly List<CheckResult> _checks = [];
    private readonly List<SshRemoteConnectionService> _services = [];
    private ConnectionProfile Profile => new()
    {
        Name = "Loopback SSH verification", Host = "127.0.0.1", Port = fixture.Info.Port,
        Username = "qa", Group = "QA", ConnectionTimeoutSeconds = 5
    };
    public int Failures => _checks.Count(x => !x.Passed);

    public async Task RunAsync(bool terminalOnly = false)
    {
        if (terminalOnly)
        {
            await RunTerminalChecksAsync();
            foreach (var service in _services) await ((IRemoteConnectionService)service).ShutdownAsync();
            await WriteReportAsync();
            return;
        }
        await CheckAsync("Password authentication, trust once, immutable session, and disposal", async () =>
        {
            var profile = Profile;
            var credentials = Password();
            var prompt = new HostPrompt(HostKeyDecision.TrustOnce);
            var hosts = Hosts("trust-once");
            var service = CreateService(credentials, prompt, hosts);
            var session = await service.ConnectAsync(profile);
            Require(session.IsConnected && session.Profile == profile, "An authenticated session did not retain its profile snapshot.");
            profile = profile with { Host = "changed.example.invalid", Username = "other" };
            Require(session.Profile.Host == "127.0.0.1" && session.Profile.Username == "qa", "Editing a profile redirected an active session.");
            Require(prompt.Identities.Single().Sha256Fingerprint == fixture.Info.Fingerprint, "The prompt fingerprint does not match the SSH server.");
            Require(await hosts.GetAsync("127.0.0.1", fixture.Info.Port) is null, "Trust once persisted a host identity.");
            Require(credentials.SaveCalls == 0, "Credentials were saved without opt-in.");
            await session.DisposeAsync();
            await session.DisposeAsync();
            Require(!session.IsConnected, "Disposal left a connected transport.");
        });
        await CheckAsync("Remembered host identities bypass the prompt on reconnect", async () =>
        {
            var hosts = Hosts("remembered");
            var prompt = new HostPrompt(HostKeyDecision.TrustAndRemember);
            var service = CreateService(Password(), prompt, hosts);
            await using (var first = await service.ConnectAsync(Profile)) Require(first.IsConnected, "The initial connection failed.");
            var reloaded = new JsonKnownHostStore(Path.Combine(output, "remembered", "known-hosts.json"));
            var stored = await reloaded.GetAsync("127.0.0.1", fixture.Info.Port);
            Require(stored?.Sha256Fingerprint == fixture.Info.Fingerprint, "The remembered host did not survive reloading.");
            var rejectPrompt = new HostPrompt(HostKeyDecision.Reject);
            await using var second = await CreateService(Password(), rejectPrompt, reloaded).ConnectAsync(Profile);
            Require(second.IsConnected && rejectPrompt.Identities.Count == 0, "A known host prompted again or failed to connect.");
        });
        await CheckAsync("Unknown rejected hosts never authenticate or persist trust", async () =>
        {
            var hosts = Hosts("rejected");
            var credentials = Password(remember: true);
            await ExpectCodeAsync(() => CreateService(credentials, new HostPrompt(HostKeyDecision.Reject), hosts).ConnectAsync(Profile),
                RemoteConnectionErrorCode.HostKeyRejected);
            Require(await hosts.GetAsync("127.0.0.1", fixture.Info.Port) is null && credentials.SaveCalls == 0,
                "Rejecting the host persisted trust or credentials.");
        });
        await CheckAsync("Password failure is sanitized, invalidates credentials, and allows retry", async () =>
        {
            var credentials = new CredentialProvider(RemoteAuthenticationMethod.Password, "incorrect-fixture-password", remember: true);
            var service = CreateService(credentials, new HostPrompt(HostKeyDecision.TrustOnce), Hosts("auth-failure"));
            await ExpectCodeAsync(() => service.ConnectAsync(Profile), RemoteConnectionErrorCode.AuthenticationFailed);
            Require(credentials.SaveCalls == 0 && credentials.Invalidations > 0, "Failed authentication persisted or retained invalid credentials.");
            credentials.Secret = fixture.Info.Password;
            await using var retried = await service.ConnectAsync(Profile);
            Require(retried.IsConnected && credentials.SaveCalls == 1, "A retry with valid credentials did not connect and honor opt-in.");
        });
        await CheckAsync("Unencrypted Ed25519 private-key authentication", async () =>
        {
            var credentials = new CredentialProvider(RemoteAuthenticationMethod.PrivateKey, string.Empty);
            await using var session = await CreateService(credentials, new HostPrompt(HostKeyDecision.TrustOnce), Hosts("plain-key"))
                .ConnectAsync(Profile with { PrivateKeyPath = fixture.Info.PlainKeyPath });
            Require(session.IsConnected, "Unencrypted key authentication failed.");
        });
        await CheckAsync("Encrypted Ed25519 private-key authentication and wrong passphrase", async () =>
        {
            var profile = Profile with { PrivateKeyPath = fixture.Info.EncryptedKeyPath };
            var credentials = new CredentialProvider(RemoteAuthenticationMethod.PrivateKey, "incorrect-fixture-passphrase");
            var service = CreateService(credentials, new HostPrompt(HostKeyDecision.TrustOnce), Hosts("encrypted-key"));
            await ExpectCodeAsync(() => service.ConnectAsync(profile), RemoteConnectionErrorCode.PrivateKeyInvalid);
            credentials.Secret = fixture.Info.Passphrase;
            await using var session = await service.ConnectAsync(profile);
            Require(session.IsConnected, "Encrypted key authentication failed.");
        });
        await CheckAsync("Unencrypted RSA PEM private-key authentication", async () =>
        {
            var credentials = new CredentialProvider(RemoteAuthenticationMethod.PrivateKey, string.Empty);
            await using var session = await CreateService(credentials, new HostPrompt(HostKeyDecision.TrustOnce), Hosts("rsa-key"))
                .ConnectAsync(Profile with { PrivateKeyPath = fixture.Info.RsaKeyPath });
            Require(session.IsConnected, "RSA PEM key authentication failed.");
        });
        await CheckAsync("Missing private keys produce a useful error without a connection", async () =>
        {
            var profile = Profile with { PrivateKeyPath = Path.Combine(output, "does-not-exist") };
            await ExpectCodeAsync(() => CreateService(new CredentialProvider(RemoteAuthenticationMethod.PrivateKey, string.Empty),
                new HostPrompt(HostKeyDecision.TrustOnce), Hosts("missing-key")).ConnectAsync(profile), RemoteConnectionErrorCode.PrivateKeyInvalid);
        });
        await CheckAsync("FTP and FTPS remain explicitly unsupported", async () =>
        {
            foreach (var protocol in new[] { ConnectionProtocol.Ftp, ConnectionProtocol.Ftps })
            {
                var credentials = Password();
                await ExpectCodeAsync(() => CreateService(credentials, new HostPrompt(HostKeyDecision.TrustOnce), Hosts("unsupported"))
                    .ConnectAsync(Profile with { Protocol = protocol }), RemoteConnectionErrorCode.UnsupportedProtocol);
                Require(credentials.Requests == 0, "An unsupported protocol requested SSH credentials.");
            }
        });
        await CheckAsync("SFTP profiles establish SSH transport without file operations", async () =>
        {
            await using var session = await Service("sftp").ConnectAsync(Profile with { Protocol = ConnectionProtocol.Sftp });
            Require(session.IsConnected, "An SFTP profile could not establish SSH transport.");
        });
        await CheckAsync("Connection refusal is distinguished from authentication failure", async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            await ExpectCodeAsync(() => Service("refused").ConnectAsync(Profile with { Port = port }), RemoteConnectionErrorCode.ConnectionRefused);
        });
        await CheckAsync("A stalled SSH handshake respects the configured timeout", async () =>
        {
            var watch = Stopwatch.StartNew();
            await ExpectCodeAsync(() => Service("timeout").ConnectAsync(Profile with { Port = fixture.Info.StallPort, ConnectionTimeoutSeconds = 1 }),
                RemoteConnectionErrorCode.Timeout);
            Require(watch.Elapsed < TimeSpan.FromSeconds(8), "The configured timeout did not bound a stalled handshake.");
        });
        await CheckAsync("Cancellation interrupts connection, credential, host, and authentication waits", async () =>
        {
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                await ExpectCanceledAsync(() => Service("cancel-connect").ConnectAsync(Profile with { Port = fixture.Info.StallPort }, cancellation.Token));
            var pendingCredentials = Password();
            pendingCredentials.WaitForCancellation = true;
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                await ExpectCanceledAsync(() => CreateService(pendingCredentials, new HostPrompt(HostKeyDecision.TrustOnce), Hosts("cancel-credentials"))
                    .ConnectAsync(Profile, cancellation.Token));
            var pendingPrompt = new HostPrompt(HostKeyDecision.TrustOnce) { WaitForCancellation = true };
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
                await ExpectCanceledAsync(() => CreateService(Password(), pendingPrompt, Hosts("cancel-host"))
                    .ConnectAsync(Profile, cancellation.Token));
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
                await ExpectCanceledAsync(() => Service("cancel-auth").ConnectAsync(Profile with { Username = "slow" }, cancellation.Token));
        });
        await CheckAsync("Unexpected transport loss notifies the owner and allows reconnect", async () =>
        {
            var service = Service("dropped");
            await using var session = await service.ConnectAsync(Profile);
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Disconnected += (_, _) => lost.TrySetResult();
            await fixture.CommandAsync("drop");
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Require(!session.IsConnected, "The dropped transport still reports an active session.");
            await using var retry = await service.ConnectAsync(Profile);
            Require(retry.IsConnected, "The transport could not reconnect after unexpected loss.");
        });
        await CheckAsync("Malformed host trust storage fails closed", async () =>
        {
            var path = Path.Combine(output, "corrupt-known-hosts.json");
            await File.WriteAllTextAsync(path, "{ not-json }");
            var prompt = new HostPrompt(HostKeyDecision.TrustAndRemember);
            await ExpectCodeAsync(() => CreateService(Password(), prompt, new JsonKnownHostStore(path)).ConnectAsync(Profile),
                RemoteConnectionErrorCode.HostTrustStorageFailed);
            Require(await File.ReadAllTextAsync(path) == "{ not-json }", "Corrupt trust data was overwritten.");
        });
        await CheckAsync("Windows credentials are isolated, reloadable, and bound to profile settings", VerifyWindowsCredentialsAsync);
        await CheckAsync("Profile JSON contains settings only and preserves the timeout", async () =>
        {
            var profile = Profile with { PrivateKeyPath = fixture.Info.EncryptedKeyPath, ConnectionTimeoutSeconds = 17 };
            var store = new JsonConnectionProfileStore(Path.Combine(output, "profiles.json"));
            await store.SaveAsync([profile]);
            Require((await store.LoadAsync()).Single() == profile, "Profile settings or timeout did not survive serialization.");
            var json = await File.ReadAllTextAsync(store.StoragePath);
            Require(!json.Contains(fixture.Info.Password, StringComparison.Ordinal) && !json.Contains(fixture.Info.Passphrase, StringComparison.Ordinal)
                && !json.Contains("PRIVATE KEY", StringComparison.Ordinal), "Profile JSON contains authentication material.");
        });
        await RunSftpChecksAsync();
        await RunTerminalChecksAsync();

        await CheckAsync("Changed host keys are blocked without a replacement prompt", async () =>
        {
            var hosts = Hosts("changed-key");
            var firstPrompt = new HostPrompt(HostKeyDecision.TrustAndRemember);
            await using (var initial = await CreateService(Password(), firstPrompt, hosts).ConnectAsync(Profile))
                Require(initial.IsConnected, "Initial host enrollment failed.");
            var original = await hosts.GetAsync("127.0.0.1", fixture.Info.Port);
            await fixture.CommandAsync("rotate");
            var prompt = new HostPrompt(HostKeyDecision.TrustAndRemember);
            await ExpectCodeAsync(() => CreateService(Password(), prompt, hosts).ConnectAsync(Profile), RemoteConnectionErrorCode.HostKeyChanged);
            Require(prompt.Identities.Count == 0 && await hosts.GetAsync("127.0.0.1", fixture.Info.Port) == original,
                "A changed host key was prompted for or replaced remembered trust.");
        });

        await CheckAsync("Shutdown retires canceled workers and releases all fixture sockets", async () =>
        {
            foreach (var service in _services)
                await ((IRemoteConnectionService)service).ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var stats = await fixture.CommandAsync("stats");
                if (stats.GetProperty("Connections").GetInt32() == 0 && stats.GetProperty("Stalled").GetInt32() == 0) return;
                await Task.Delay(50);
            }
            throw new InvalidOperationException("SSH verification left a transport socket open after shutdown.");
        });
        await WriteReportAsync();
    }

    private async Task WriteReportAsync()
    {
        var report = new { FinishedAt = DateTimeOffset.UtcNow, Passed = _checks.Count - Failures, Failed = Failures, Checks = _checks,
            Fixture = "Ephemeral AsyncSSH server on 127.0.0.1 only; generated keys are removed at fixture shutdown." };
        await File.WriteAllTextAsync(Path.Combine(output, "ssh-verification.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SSH verification: {report.Passed} passed, {Failures} failed.");
        Console.WriteLine($"Report: {Path.Combine(output, "ssh-verification.json")}");
    }

    private async Task VerifyWindowsCredentialsAsync()
    {
        var prefix = $"OhMySsh.Verify.{Guid.NewGuid():N}";
        var store = new WindowsCredentialStore(prefix);
        var profile = Profile;
        using var credentials = new RemoteConnectionCredentials(RemoteAuthenticationMethod.Password, fixture.Info.Password, remember: true);
        try
        {
            Require(await store.LoadAsync(profile) is null, "An isolated credential target already exists.");
            await store.SaveAsync(profile, credentials);
            using var loaded = await new WindowsCredentialStore(prefix).LoadAsync(profile);
            Require(loaded?.Secret == fixture.Info.Password && loaded.Method == RemoteAuthenticationMethod.Password,
                "The Windows credential did not survive reloading.");
            foreach (var changed in new[] { profile with { Host = "other.example.invalid" }, profile with { Username = "other" },
                profile with { Port = profile.Port + 1 }, profile with { PrivateKeyPath = fixture.Info.PlainKeyPath }, profile with { Id = Guid.NewGuid() } })
            {
                using var unexpected = await store.LoadAsync(changed);
                Require(unexpected is null, "An edited profile reused credentials for a different connection identity.");
            }
            Require(!credentials.ToString().Contains(fixture.Info.Password, StringComparison.Ordinal), "Credential formatting exposed the secret.");
            Require(!JsonSerializer.Serialize(credentials).Contains(fixture.Info.Password, StringComparison.Ordinal),
                "Accidental credential serialization exposed the secret.");
        }
        finally
        {
            await store.DeleteAsync(profile);
        }
        Require(await store.LoadAsync(profile) is null, "Test credentials were not removed from Windows Credential Manager.");
    }

    private CredentialProvider Password(bool remember = false) => new(RemoteAuthenticationMethod.Password, fixture.Info.Password, remember);
    private JsonKnownHostStore Hosts(string name) => new(Path.Combine(output, name, "known-hosts.json"));
    private SshRemoteConnectionService Service(string name) => CreateService(Password(), new HostPrompt(HostKeyDecision.TrustOnce), Hosts(name));
    private SshRemoteConnectionService CreateService(IRemoteCredentialProvider credentials, IHostKeyPrompt prompt, IKnownHostStore hosts)
    {
        var service = new SshRemoteConnectionService(credentials, prompt, hosts);
        _services.Add(service);
        return service;
    }
    private async Task CheckAsync(string name, Func<Task> test)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await test().WaitAsync(TimeSpan.FromSeconds(45));
            _checks.Add(new(name, true, watch.ElapsedMilliseconds, null));
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            var error = $"{exception.GetType().Name}: {exception.Message}".Replace(fixture.Info.Password, "[redacted]", StringComparison.Ordinal)
                .Replace(fixture.Info.Passphrase, "[redacted]", StringComparison.Ordinal);
            _checks.Add(new(name, false, watch.ElapsedMilliseconds, error));
            Console.WriteLine($"FAIL {name}: {error}");
        }
    }
    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }
    private static async Task ExpectCodeAsync(Func<Task<IRemoteConnectionSession>> action, RemoteConnectionErrorCode code)
    {
        try { await using var unexpected = await action(); }
        catch (RemoteConnectionException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException($"Expected a sanitized {code} error.");
    }
    private static async Task ExpectCanceledAsync(Func<Task<IRemoteConnectionSession>> action)
    {
        var watch = Stopwatch.StartNew();
        try { await using var unexpected = await action(); }
        catch (OperationCanceledException)
        {
            Require(watch.Elapsed < TimeSpan.FromSeconds(5), "Cancellation did not complete promptly.");
            return;
        }
        throw new InvalidOperationException("Expected cancellation without an active session.");
    }
}

internal sealed record CheckResult(string Name, bool Passed, long DurationMs, string? Error);
internal sealed record FixtureInfo(int Port, int StallPort, string Password, string Passphrase, string PlainKeyPath, string EncryptedKeyPath, string RsaKeyPath, string Fingerprint, int NoSftpPort, string SftpRoot, int RealTerminalPort);

internal sealed class SshFixture(Process process, FixtureInfo info, Task<string> diagnosticDrain) : IAsyncDisposable
{
    public FixtureInfo Info { get; } = info;
    public static async Task<SshFixture> StartAsync(string python, string output)
    {
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ssh_fixture.py"));
        start.ArgumentList.Add(Path.Combine(output, "fixture"));
        start.ArgumentList.Add(Path.Combine(FindRuntimeRoot(output), "terminal-runtime", "msys"));
        var process = Process.Start(start) ?? throw new InvalidOperationException("The local SSH fixture could not start.");
        var diagnosticDrain = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var info = JsonSerializer.Deserialize<FixtureInfo>(ready ?? "") ?? throw new InvalidOperationException("The SSH fixture did not become ready.");
            return new SshFixture(process, info, diagnosticDrain);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await diagnosticDrain;
            process.Dispose();
            throw;
        }
    }
    private static string FindRuntimeRoot(string output) => Directory.GetParent(output)?.FullName
        ?? throw new DirectoryNotFoundException("The local runtime directory could not be located.");
    public async Task<JsonElement> CommandAsync(string command)
    {
        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.FlushAsync();
        var result = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (result is null) throw new InvalidOperationException("The local SSH fixture stopped unexpectedly.");
        using var document = JsonDocument.Parse(result);
        return document.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                await CommandAsync("stop");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            await diagnosticDrain;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }
}

internal sealed class CredentialProvider(RemoteAuthenticationMethod method, string secret, bool remember = false) : IRemoteCredentialProvider
{
    public string Secret { get; set; } = secret;
    public int Requests { get; private set; }
    public int SaveCalls { get; private set; }
    public int Invalidations { get; private set; }
    public bool WaitForCancellation { get; set; }
    public async Task<RemoteConnectionCredentials?> GetCredentialsAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        Requests++;
        if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new RemoteConnectionCredentials(method, Secret, remember);
    }
    public Task SaveCredentialsAsync(ConnectionProfile profile, RemoteConnectionCredentials credentials, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCalls++;
        return Task.CompletedTask;
    }
    public Task InvalidateAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        Invalidations++;
        return Task.CompletedTask;
    }
}

internal sealed class HostPrompt(HostKeyDecision decision) : IHostKeyPrompt
{
    public List<SshHostIdentity> Identities { get; } = [];
    public bool WaitForCancellation { get; set; }
    public async Task<HostKeyDecision> ConfirmAsync(SshHostIdentity identity, CancellationToken cancellationToken = default)
    {
        Identities.Add(identity);
        if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return decision;
    }
}
