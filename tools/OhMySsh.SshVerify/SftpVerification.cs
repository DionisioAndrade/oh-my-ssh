using System.Security.Cryptography;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;

namespace OhMySsh.SshVerify;

internal sealed partial class SshVerificationSuite
{
    private async Task RunSftpChecksAsync()
    {
        var local = Path.Combine(output, "sftp-local");
        Directory.CreateDirectory(local);
        await CheckAsync("SFTP: home, navigation, metadata, Unicode names, and POSIX paths", async () =>
        {
            await using var session = await Service("files-browse").ConnectAsync(Profile);
            var files = RequireFiles(session);
            Require(await files.GetHomeDirectoryAsync() == "/", "The SFTP initial working directory was incorrect.");
            var entries = await files.GetEntriesAsync("/");
            Require(entries.Any(x => x.Name == "açúcar 空 白.txt" && !x.IsDirectory && x.Size > 0 && x.LastModified != default),
                "Remote file metadata or Unicode names were not preserved.");
            Require(entries.Any(x => x.Name == "Unicode folder" && x.IsDirectory), "A remote directory was missing.");
            Require(entries.All(x => x.Name is not ("." or "..")), "Synthetic dot entries leaked into the listing.");
            Require((await files.GetEntriesAsync("/empty")).Count == 0, "An empty remote directory was not empty.");
            Require(RemotePath.Normalize("/Unicode folder//../empty/./") == "/empty"
                && RemotePath.GetParent("/Unicode folder") == "/" && RemotePath.GetParent("/") == "/",
                "Remote paths were normalized using incorrect path rules.");
        });
        await CheckAsync("SFTP: missing paths and permission errors preserve a usable session", async () =>
        {
            await using var session = await Service("files-errors").ConnectAsync(Profile);
            var files = RequireFiles(session);
            await ExpectFileCodeAsync(() => files.GetEntriesAsync("/missing"), RemoteFileErrorCode.NotFound);
            await ExpectFileCodeAsync(() => files.GetEntriesAsync("/denied"), RemoteFileErrorCode.PermissionDenied);
            Require((await files.GetEntriesAsync("/")).Count > 0 && session.IsConnected,
                "A browsing error made the authenticated SSH session unusable.");
        });
        foreach (var (name, keyPath, secret) in new[] {
            ("unencrypted Ed25519", fixture.Info.PlainKeyPath, string.Empty),
            ("encrypted Ed25519", fixture.Info.EncryptedKeyPath, fixture.Info.Passphrase),
            ("RSA PEM", fixture.Info.RsaKeyPath, string.Empty) })
        {
            await CheckAsync($"SFTP: child transport reuses {name} authentication", async () =>
            {
                var credentials = new CredentialProvider(RemoteAuthenticationMethod.PrivateKey, secret);
                var prompt = new HostPrompt(HostKeyDecision.TrustOnce);
                await using var session = await CreateService(credentials, prompt, Hosts($"files-key-{name.Replace(' ', '-') }"))
                    .ConnectAsync(Profile with { PrivateKeyPath = keyPath });
                Require((await RequireFiles(session).GetEntriesAsync("/")).Count > 0 && credentials.Requests == 1 && prompt.Identities.Count == 1,
                    "The SFTP transport did not reuse the confirmed authentication and server identity.");
            });
        }
        await CheckAsync("SFTP: successful SSH attempt cancellation does not cancel its active child transport", async () =>
        {
            using var originalAttempt = new CancellationTokenSource();
            await using var session = await Service("files-independent-attempt-token").ConnectAsync(Profile, originalAttempt.Token);
            originalAttempt.Cancel();
            Require(await RequireFiles(session).GetHomeDirectoryAsync() == "/" && session.IsConnected,
                "A completed SSH attempt retained control of SFTP authentication.");
        });
        await CheckAsync("SFTP: cancellation interrupts child authentication and permits retry", async () =>
        {
            await using var session = await Service("files-cancel-child-auth").ConnectAsync(Profile);
            var files = RequireFiles(session);
            await fixture.CommandAsync("slow-auth-on");
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                await ExpectFileCanceledAsync(() => files.GetHomeDirectoryAsync(cancellation.Token));
            }
            finally { await fixture.CommandAsync("slow-auth-off"); }
            await VerifyFilesUsableOrExplicitReconnectAsync(files);
            Require(session.IsConnected, "Canceling SFTP authentication ended the main SSH session.");
        });
        await CheckAsync("SFTP: binary upload/download preserve contents, names, and progress", async () =>
        {
            await using var session = await Service("files-roundtrip").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var source = Path.Combine(local, "upload café 空.bin");
            await File.WriteAllBytesAsync(source, Enumerable.Range(0, 256 * 1024).Select(x => (byte)(x % 251)).ToArray());
            var progress = new RecordedFileProgress();
            const string remote = "/Unicode folder/upload café 空.bin";
            await files.UploadAsync(source, remote, false, progress);
            var destination = Path.Combine(local, "download café 空.bin");
            await files.DownloadAsync(remote, destination, false, progress);
            Require(await SameContentsAsync(source, destination), "A round trip changed binary contents.");
            Require(await SameContentsAsync(source, Path.Combine(fixture.Info.SftpRoot, "Unicode folder", "upload café 空.bin")),
                "The remote destination did not contain the uploaded bytes.");
            Require(progress.Samples.Any(x => x.BytesTransferred > 0) && progress.Samples.All(x => x.BytesTransferred <= x.TotalBytes)
                && progress.Samples.Last().BytesTransferred == new FileInfo(source).Length,
                "Transfer progress did not report actual byte counts.");
            Require(progress.Samples.Count(x => x.IsFinalizing) == 2
                && progress.Samples.Where(x => x.IsFinalizing).All(x => x.BytesTransferred == x.TotalBytes),
                "Transfers did not report their actual destination finalization boundary.");
            RequireNoRemotePartials();
        });
        await CheckAsync("SFTP: empty files finalize as successful zero-byte transfers", async () =>
        {
            await using var session = await Service("files-empty").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var source = Path.Combine(local, "empty-upload.bin");
            await File.WriteAllBytesAsync(source, []);
            var progress = new RecordedFileProgress();
            await files.UploadAsync(source, "/empty-upload.bin", false, progress);
            var target = Path.Combine(local, "empty-download.bin");
            await files.DownloadAsync("/empty-upload.bin", target, false, progress);
            Require(new FileInfo(target).Length == 0 && new FileInfo(Path.Combine(fixture.Info.SftpRoot, "empty-upload.bin")).Length == 0,
                "Empty-file finalization failed.");
            Require(progress.Samples.Count(x => x.IsFinalizing && x.TotalBytes == 0 && x.BytesTransferred == 0) == 2,
                "Empty-file finalization did not identify its atomic commit phase.");
            RequireNoRemotePartials();
        });
        await CheckAsync("SFTP: no-overwrite conflicts preserve both existing destinations", async () =>
        {
            await using var session = await Service("files-overwrite-no").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var source = Path.Combine(local, "replacement.txt");
            var target = Path.Combine(local, "existing-target.txt");
            await File.WriteAllTextAsync(source, "replacement contents");
            await File.WriteAllTextAsync(target, "keep local contents");
            var originalRemote = await File.ReadAllBytesAsync(Path.Combine(fixture.Info.SftpRoot, "hello.txt"));
            await ExpectFileCodeAsync(() => files.UploadAsync(source, "/hello.txt", false), RemoteFileErrorCode.AlreadyExists);
            await ExpectFileCodeAsync(() => files.DownloadAsync("/hello.txt", target, false), RemoteFileErrorCode.AlreadyExists);
            Require(await File.ReadAllTextAsync(target) == "keep local contents", "A rejected overwrite changed the local target.");
            Require((await File.ReadAllBytesAsync(Path.Combine(fixture.Info.SftpRoot, "hello.txt"))).SequenceEqual(originalRemote),
                "A rejected overwrite changed the remote target.");
        });
        await CheckAsync("SFTP: confirmed overwrite uses staged finalization", async () =>
        {
            await using var session = await Service("files-overwrite-yes").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var source = Path.Combine(local, "overwrite-source.txt");
            await File.WriteAllTextAsync(source, "new finalized contents");
            await files.UploadAsync(source, "/overwrite.txt", false);
            await File.WriteAllTextAsync(source, "replacement finalized contents");
            await files.UploadAsync(source, "/overwrite.txt", true);
            var target = Path.Combine(local, "overwrite-target.txt");
            await File.WriteAllTextAsync(target, "old local contents");
            await files.DownloadAsync("/overwrite.txt", target, true);
            Require(await SameContentsAsync(source, target), "A confirmed overwrite did not finalize the new contents.");
            RequireNoRemotePartials();
        });
        await CheckAsync("SFTP: destination races never overwrite unconfirmed files", async () =>
        {
            await using var session = await Service("files-destination-race").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var remoteTarget = Path.Combine(fixture.Info.SftpRoot, "raced-upload.bin");
            var uploadProgress = new RecordedFileProgress(value =>
            {
                if (value.BytesTransferred > 0 && !File.Exists(remoteTarget))
                    File.WriteAllText(remoteTarget, "remote destination created during upload");
            });
            await ExpectFileCodeAsync(() => files.UploadAsync(Path.Combine(fixture.Info.SftpRoot, "large.bin"),
                "/raced-upload.bin", false, uploadProgress), RemoteFileErrorCode.AlreadyExists);
            Require(await File.ReadAllTextAsync(remoteTarget) == "remote destination created during upload",
                "Upload finalization replaced a destination created after the initial existence check.");
            var localTarget = Path.Combine(local, "raced-download.bin");
            var downloadProgress = new RecordedFileProgress(value =>
            {
                if (value.BytesTransferred > 0 && !File.Exists(localTarget))
                    File.WriteAllText(localTarget, "local destination created during download");
            });
            await ExpectFileCodeAsync(() => files.DownloadAsync("/large.bin", localTarget, false, downloadProgress),
                RemoteFileErrorCode.AlreadyExists);
            Require(await File.ReadAllTextAsync(localTarget) == "local destination created during download",
                "Download finalization replaced a destination created after the initial existence check.");
            RequireNoRemotePartials();
            Require(!Directory.EnumerateFiles(local, ".oh-my-ssh-*.part").Any(), "A destination conflict left local staging files.");
        });
        await CheckAsync("SFTP: unsupported atomic replacement preserves the previous remote file", async () =>
        {
            await using var session = await Service("files-no-posix-rename").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var source = Path.Combine(local, "unsupported-replacement.txt");
            await File.WriteAllTextAsync(source, "must not replace old contents");
            await ExpectFileCodeAsync(() => files.UploadAsync(source, "/no-replace/existing.txt", true), RemoteFileErrorCode.UnsupportedReplacement);
            Require(await File.ReadAllTextAsync(Path.Combine(fixture.Info.SftpRoot, "no-replace", "existing.txt")) == "preserve this destination",
                "Unsupported atomic replacement destroyed the old target.");
            RequireNoRemotePartials();
        });
        await CheckAsync("SFTP: cancellation during upload preserves the target and removes staging", async () =>
        {
            await using var session = await Service("files-cancel-upload").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var remoteTarget = Path.Combine(fixture.Info.SftpRoot, "canceled-upload.bin");
            await File.WriteAllTextAsync(remoteTarget, "original remote destination");
            using var cancellation = new CancellationTokenSource();
            var progress = new RecordedFileProgress(x => { if (x.BytesTransferred > 0) cancellation.Cancel(); });
            await fixture.CommandAsync("slow-on");
            try { await ExpectFileCanceledAsync(() => files.UploadAsync(Path.Combine(fixture.Info.SftpRoot, "large.bin"),
                "/canceled-upload.bin", true, progress, cancellation.Token)); }
            finally { await fixture.CommandAsync("slow-off"); }
            Require(await File.ReadAllTextAsync(remoteTarget) == "original remote destination", "Canceled upload changed the existing target.");
            RequireNoRemotePartials();
            await VerifyFilesUsableOrExplicitReconnectAsync(files);
        });
        await CheckAsync("SFTP: cancellation during download preserves the target and removes staging", async () =>
        {
            await using var session = await Service("files-cancel-download").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var target = Path.Combine(local, "canceled-download.bin");
            await File.WriteAllTextAsync(target, "original local destination");
            using var cancellation = new CancellationTokenSource();
            var progress = new RecordedFileProgress(x => { if (x.BytesTransferred > 0) cancellation.Cancel(); });
            await fixture.CommandAsync("slow-on");
            try { await ExpectFileCanceledAsync(() => files.DownloadAsync("/large.bin", target, true, progress, cancellation.Token)); }
            finally { await fixture.CommandAsync("slow-off"); }
            Require(await File.ReadAllTextAsync(target) == "original local destination", "Canceled download changed the existing target.");
            Require(!Directory.EnumerateFiles(local, ".oh-my-ssh-*.part").Any(), "Canceled download left a staging file.");
            await VerifyFilesUsableOrExplicitReconnectAsync(files);
        });
        await CheckAsync("SFTP: large streaming transfers retain bounded managed memory", async () =>
        {
            await using var session = await Service("files-large").ConnectAsync(Profile);
            var files = RequireFiles(session);
            await files.GetHomeDirectoryAsync();
            var baseline = GC.GetTotalMemory(true);
            long peak = baseline, lastMeasured = 0;
            var progress = new RecordedFileProgress(x =>
            {
                if (x.BytesTransferred - lastMeasured >= 4 * 1024 * 1024)
                {
                    peak = Math.Max(peak, GC.GetTotalMemory(true));
                    lastMeasured = x.BytesTransferred;
                }
            });
            var source = Path.Combine(fixture.Info.SftpRoot, "large.bin");
            await files.UploadAsync(source, "/large-upload.bin", false, progress);
            var target = Path.Combine(local, "large-download.bin");
            lastMeasured = 0;
            await files.DownloadAsync("/large-upload.bin", target, false, progress);
            Require(await SameContentsAsync(source, target), "A large transfer changed file contents.");
            Require(peak - baseline < 24 * 1024 * 1024, "The 32 MiB transfer retained an entire file-sized managed buffer.");
            RequireNoRemotePartials();
        });
        await CheckAsync("SFTP: unexpected disconnection preserves an existing local destination", async () =>
        {
            await using var session = await Service("files-dropped").ConnectAsync(Profile);
            var files = RequireFiles(session);
            var target = Path.Combine(local, "dropped-download.bin");
            await File.WriteAllTextAsync(target, "original before connection loss");
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await fixture.CommandAsync("slow-on");
            try
            {
                var operation = files.DownloadAsync("/large.bin", target, true, new RecordedFileProgress(x =>
                { if (x.BytesTransferred > 0) started.TrySetResult(); }));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await fixture.CommandAsync("drop");
                try { await operation; throw new InvalidOperationException("A dropped download reported success."); }
                catch (Exception exception) when (exception is RemoteFileException or OperationCanceledException) { }
            }
            finally { await fixture.CommandAsync("slow-off"); }
            Require(await File.ReadAllTextAsync(target) == "original before connection loss", "Connection loss changed the old destination.");
            Require(!Directory.EnumerateFiles(local, ".oh-my-ssh-*.part").Any(), "Connection loss left a local staging file.");
        });
        await CheckAsync("SFTP: unavailable subsystem leaves the authenticated SSH session active", async () =>
        {
            await using var session = await Service("files-unavailable").ConnectAsync(Profile with { Port = fixture.Info.NoSftpPort });
            var failure = await ExpectFileCodeAsync(() => RequireFiles(session).GetHomeDirectoryAsync(), RemoteFileErrorCode.Unavailable);
            Require(!failure.RequiresReconnect && session.IsConnected, "An unavailable SFTP subsystem was treated as SSH connection loss.");
        });
        await CheckAsync("SFTP: unsafe remote filenames cannot escape a local destination", () =>
        {
            foreach (var name in new[] { "../escape.txt", "..\\escape.txt", "C:\\escape.txt", ".", "..", "NUL", "CON.txt", "file.txt:stream", "trailing." })
            {
                try { RemotePath.GetSafeLocalFileName(name); throw new InvalidOperationException("An unsafe local filename was accepted."); }
                catch (RemoteFileException exception) when (exception.Code == RemoteFileErrorCode.UnsafePath) { }
            }
            Require(RemotePath.GetSafeLocalFileName("açúcar 空 白.txt") == "açúcar 空 白.txt", "A valid Unicode filename was rejected.");
            return Task.CompletedTask;
        });
        await CheckAsync("SFTP: child connection pins the active SSH host identity without a new prompt", async () =>
        {
            var prompt = new HostPrompt(HostKeyDecision.TrustOnce);
            await using var session = await CreateService(Password(), prompt, Hosts("files-pinned-key")).ConnectAsync(Profile);
            await fixture.CommandAsync("rotate-listener");
            try
            {
                var failure = await ExpectFileCodeAsync(() => RequireFiles(session).GetHomeDirectoryAsync(), RemoteFileErrorCode.Unavailable);
                Require(failure.RequiresReconnect && prompt.Identities.Count == 1, "A mismatched SFTP endpoint was silently re-trusted.");
            }
            finally { await fixture.CommandAsync("restore-listener"); }
        });
    }

    private static IRemoteFileService RequireFiles(IRemoteConnectionSession session)
        => session.Files ?? throw new InvalidOperationException("The real SSH session did not expose SFTP resources.");

    private void RequireNoRemotePartials()
        => Require(!Directory.EnumerateFiles(fixture.Info.SftpRoot, ".oh-my-ssh-*.part", SearchOption.AllDirectories).Any(),
            "A completed or canceled operation left a remote staging file.");

    private static async Task<bool> SameContentsAsync(string first, string second)
    {
        await using var left = File.OpenRead(first);
        await using var right = File.OpenRead(second);
        var leftHash = await SHA256.HashDataAsync(left);
        var rightHash = await SHA256.HashDataAsync(right);
        return leftHash.SequenceEqual(rightHash);
    }

    private static async Task<RemoteFileException> ExpectFileCodeAsync(Func<Task> action, RemoteFileErrorCode code)
    {
        try { await action(); }
        catch (RemoteFileException exception) when (exception.Code == code) { return exception; }
        throw new InvalidOperationException($"Expected a sanitized SFTP {code} error.");
    }

    private static async Task ExpectFileCanceledAsync(Func<Task> action)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try { await action(); }
        catch (OperationCanceledException)
        {
            Require(watch.Elapsed < TimeSpan.FromSeconds(8), "SFTP cancellation did not stop the underlying operation promptly.");
            return;
        }
        throw new InvalidOperationException("Canceled SFTP work reported success.");
    }

    private static async Task VerifyFilesUsableOrExplicitReconnectAsync(IRemoteFileService files)
    {
        try { await files.GetEntriesAsync("/"); }
        catch (RemoteFileException exception) when (exception.RequiresReconnect) { }
    }
}

internal sealed class RecordedFileProgress(Action<TransferProgress>? onProgress = null) : IProgress<TransferProgress>
{
    private readonly List<TransferProgress> _samples = [];
    public IReadOnlyList<TransferProgress> Samples { get { lock (_samples) return _samples.ToArray(); } }
    public void Report(TransferProgress value)
    {
        lock (_samples) _samples.Add(value);
        onProgress?.Invoke(value);
    }
}
