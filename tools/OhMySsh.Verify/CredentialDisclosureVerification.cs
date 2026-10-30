using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyCredentialDisclosureBoundaryAsync()
    {
        const string misplacedPath = "QA_MISPLACED_KEY_FIELD_MUST_NOT_RENDER";
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            ThemeService.Current.Apply(theme);
            var verifier = new QaWindowsUserVerifier();
            var clipboard = new QaCredentialClipboard();
            var window = new SshCredentialsWindow(CreateProfile() with { PrivateKeyPath = misplacedPath }, true, verifier, clipboard);
            PrepareHiddenWindow(window);
            window.Show();
            try
            {
                await SettleLayoutAsync(window);
                Require(!CredentialSurfaceText(window).Contains(misplacedPath, StringComparison.Ordinal),
                    "The authentication dialog rendered arbitrary private-key metadata as text or a tooltip.");
                Require(CredentialPlainText(window).Text.Length == 0 && !CredentialPlainText(window).IsVisible,
                    "The authentication dialog exposed a secret before Windows approval.");
                Require(!CredentialPlainText(window).Focusable && !CredentialPlainText(window).IsTabStop
                    && !CredentialPlainText(window).IsHitTestVisible && CredentialPlainText(window).ContextMenu is null,
                    "The reveal-only control enabled unguarded keyboard, selection, drag, or context-menu copying.");
                Require(!((Button)window.FindName("CopySecretButton")).IsEnabled
                    && !((Button)window.FindName("RevealSecretButton")).IsEnabled,
                    "An empty private-key passphrase exposed a disclosure action.");
                await InvokeCredentialActionAsync(window, "CopySecretButton");
                Require(verifier.Requests == 0 && clipboard.Secrets.Count == 0,
                    "An empty passphrase started verification or copied private-key metadata as a password.");
                Capture(window, theme == AppTheme.Light ? "ssh-key-credentials-protected-light.png" : "ssh-key-credentials-protected-dark.png");
            }
            finally { window.Close(); }
        }

        foreach (var address in new[] { "12.123.123.123", "qa.example.invalid", "2001:db8::17" })
        {
            var verifier = new QaWindowsUserVerifier { OwnerActive = false };
            var clipboard = new QaCredentialClipboard();
            var window = new SshCredentialsWindow(CreateProfile() with { Host = address, PrivateKeyPath = null }, true, verifier, clipboard);
            PrepareHiddenWindow(window);
            window.Show();
            try
            {
                await InvokeCredentialActionAsync(window, "CopyIpButton");
                Require(clipboard.Addresses.SequenceEqual([address]) && clipboard.Secrets.Count == 0 && verifier.Requests == 0,
                    "Copy address included an account/port, resolved a hostname, or required access to a secret.");
            }
            finally { window.Close(); }
        }

        const string secret = "QA_SECRET_APPROVAL_REQUIRED";
        foreach (var outcome in new[] { WindowsUserVerificationResult.Rejected, WindowsUserVerificationResult.Canceled,
            WindowsUserVerificationResult.Unavailable })
        {
            var verifier = new QaWindowsUserVerifier { Result = outcome };
            var clipboard = new QaCredentialClipboard();
            var window = new SshCredentialsWindow(CreateProfile() with { PrivateKeyPath = null }, true, verifier, clipboard);
            PrepareHiddenWindow(window);
            window.Show();
            try
            {
                ((PasswordBox)window.FindName("SecretBox")).Password = secret;
                await InvokeCredentialActionAsync(window, "RevealSecretButton");
                await InvokeCredentialActionAsync(window, "CopySecretButton");
                Require(verifier.Requests == 2 && verifier.Owners.All(owner => owner != 0)
                    && clipboard.Secrets.Count == 0 && CredentialPlainText(window).Text.Length == 0
                    && !CredentialSurfaceText(window).Contains(secret, StringComparison.Ordinal),
                    "Rejected, canceled, or unavailable Windows verification disclosed a secret.");
            }
            finally { window.Close(); }
        }

        var failingVerifier = new QaWindowsUserVerifier { Handler = (_, _, _) =>
            Task.FromException<WindowsUserVerificationResult>(new InvalidOperationException(secret)) };
        var failingClipboard = new QaCredentialClipboard();
        var failedWindow = new SshCredentialsWindow(CreateProfile() with { PrivateKeyPath = null }, false, failingVerifier, failingClipboard);
        PrepareHiddenWindow(failedWindow);
        failedWindow.Show();
        try
        {
            ((PasswordBox)failedWindow.FindName("SecretBox")).Password = secret;
            await InvokeCredentialActionAsync(failedWindow, "RevealSecretButton");
            Require(failingClipboard.Secrets.Count == 0 && CredentialPlainText(failedWindow).Text.Length == 0
                && !CredentialSurfaceText(failedWindow).Contains(secret, StringComparison.Ordinal),
                "A verification exception exposed its raw details or the pending secret.");
        }
        finally { failedWindow.Close(); ThemeService.Current.Apply(AppTheme.Light); }

        var copyVerifier = new QaWindowsUserVerifier { Result = WindowsUserVerificationResult.Verified };
        var copyFailure = new QaCredentialClipboard { CopyFailure = new ExternalException(secret) };
        var copyWindow = new SshCredentialsWindow(CreateProfile() with { PrivateKeyPath = null }, true, copyVerifier, copyFailure);
        PrepareHiddenWindow(copyWindow);
        copyWindow.Show();
        try
        {
            ((PasswordBox)copyWindow.FindName("SecretBox")).Password = secret;
            await InvokeCredentialActionAsync(copyWindow, "CopySecretButton");
            Require(copyVerifier.Requests == 1 && copyFailure.Secrets.Count == 0
                && CredentialPlainText(copyWindow).Text.Length == 0
                && !CredentialSurfaceText(copyWindow).Contains(secret, StringComparison.Ordinal),
                "A clipboard failure exposed the secret or raw exception text.");
        }
        finally { copyWindow.Close(); }

        var unencryptedVerifier = new QaWindowsUserVerifier();
        var unencrypted = new SshCredentialsWindow(CreateProfile(), true, unencryptedVerifier, new QaCredentialClipboard());
        PrepareHiddenWindow(unencrypted);
        unencrypted.Loaded += (_, _) => application.Dispatcher.BeginInvoke(() =>
            ((Button)unencrypted.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        Require(unencrypted.ShowDialog() == true, "An unencrypted private key incorrectly required a passphrase or Windows disclosure approval.");
        using var keyCredentials = unencrypted.Credentials;
        Require(keyCredentials is { Method: RemoteAuthenticationMethod.PrivateKey } && keyCredentials.Secret.Length == 0
            && unencryptedVerifier.Requests == 0,
            "Private-key authentication changed its optional-passphrase or connection-only behavior.");
    }

    private async Task VerifyCredentialDisclosureLifecycleAsync()
    {
        const string secret = "QA_PRIVATE_PASSPHRASE";
        var verifier = new QaWindowsUserVerifier { Result = WindowsUserVerificationResult.Verified };
        var clipboard = new QaCredentialClipboard();
        var window = new SshCredentialsWindow(CreateProfile(), true, verifier, clipboard);
        PrepareHiddenWindow(window);
        window.Show();
        try
        {
            ((PasswordBox)window.FindName("SecretBox")).Password = secret;
            await InvokeCredentialActionAsync(window, "RevealSecretButton");
            Require(verifier.Requests == 1 && CredentialPlainText(window).IsVisible && CredentialPlainText(window).Text == secret,
                "Explicit Windows approval did not reveal the currently entered passphrase.");
            await InvokeCredentialActionAsync(window, "RevealSecretButton");
            Require(verifier.Requests == 1 && CredentialPlainText(window).Text.Length == 0 && !CredentialPlainText(window).IsVisible,
                "Hiding a revealed secret required authentication or retained plaintext.");
            Require(!CredentialPlainText(window).IsUndoEnabled && !CredentialPlainText(window).CanUndo,
                "A hidden revealed-secret control retained undo history containing plaintext.");
            await InvokeCredentialActionAsync(window, "CopySecretButton");
            Require(verifier.Requests == 2 && clipboard.Secrets.SequenceEqual([secret]) && CredentialPlainText(window).Text.Length == 0,
                "Copy reused a previous reveal approval, copied a key path, or exposed plaintext in the window.");

            await InvokeCredentialActionAsync(window, "RevealSecretButton");
            RaiseCredentialDeactivated(window);
            Require(CredentialPlainText(window).Text.Length == 0 && !CredentialPlainText(window).IsVisible,
                "Leaving the credential dialog retained a visible secret.");

            await InvokeCredentialActionAsync(window, "RevealSecretButton");
            var timer = typeof(SshCredentialsWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(field => field.FieldType == typeof(DispatcherTimer)).Select(field => field.GetValue(window))
                .OfType<DispatcherTimer>().Single();
            timer.Interval = TimeSpan.FromMilliseconds(25);
            await WaitUntilAsync(() => !CredentialPlainText(window).IsVisible);
            Require(CredentialPlainText(window).Text.Length == 0, "Reveal timeout hid the control but retained its plaintext value.");

            var pending = new TaskCompletionSource<WindowsUserVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            verifier.Handler = (_, _, _) => pending.Task;
            var before = clipboard.Secrets.Count;
            ((Button)window.FindName("CopySecretButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var operation = window.PendingDisclosure;
            ((PasswordBox)window.FindName("SecretBox")).Password = "QA_CHANGED_DURING_APPROVAL";
            pending.SetResult(WindowsUserVerificationResult.Verified);
            await operation;
            Require(clipboard.Secrets.Count == before && CredentialPlainText(window).Text.Length == 0,
                "An approval for an earlier secret was used after the value changed.");

            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            verifier.Handler = (_, _, _) => pending.Task;
            ((Button)window.FindName("RevealSecretButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            operation = window.PendingDisclosure;
            verifier.OwnerActive = false;
            RaiseCredentialDeactivated(window);
            pending.SetResult(WindowsUserVerificationResult.Verified);
            await operation;
            Require(CredentialPlainText(window).Text.Length == 0 && !CredentialPlainText(window).IsVisible,
                "A late verified result revealed a secret into an inactive dialog.");
        }
        finally { window.Close(); }
        Require(((PasswordBox)window.FindName("SecretBox")).SecurePassword.Length == 0 && CredentialPlainText(window).Text.Length == 0,
            "Closing the dialog retained a masked or revealed credential value.");

        var lateVerifier = new QaWindowsUserVerifier();
        var lateClipboard = new QaCredentialClipboard();
        var lateResult = new TaskCompletionSource<WindowsUserVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lateVerifier.Handler = (_, _, _) => lateResult.Task;
        var closed = new SshCredentialsWindow(CreateProfile() with { PrivateKeyPath = null }, true, lateVerifier, lateClipboard);
        PrepareHiddenWindow(closed);
        closed.Show();
        ((PasswordBox)closed.FindName("SecretBox")).Password = secret;
        ((Button)closed.FindName("CopySecretButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var lateOperation = closed.PendingDisclosure;
        closed.Close();
        lateResult.SetResult(WindowsUserVerificationResult.Verified);
        await lateOperation;
        Require(lateClipboard.Secrets.Count == 0 && CredentialPlainText(closed).Text.Length == 0,
            "A late Windows result copied a secret after its dialog closed.");
    }

    private async Task VerifyWindowsCredentialProtectionAsync()
    {
        var service = new WindowsUserVerificationService();
        Require(!service.IsOwnerActive(0)
            && await service.VerifyAsync(0, "QA invalid owner") == WindowsUserVerificationResult.Unavailable,
            "A missing owner window bypassed Windows verification or launched an unowned prompt.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Require(await service.VerifyAsync(0, "QA canceled request", canceled.Token) == WindowsUserVerificationResult.Canceled,
            "An already canceled request did not stay canceled before opening native verification.");

        var identityCheck = typeof(WindowsUserVerificationService).GetMethod("IsSameUser", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The native verifier's exact-user check could not be verified.");
        using var current = WindowsIdentity.GetCurrent();
        var sid = current.User ?? throw new InvalidOperationException("The QA process has no Windows user identity.");
        var other = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        Require((bool)identityCheck.Invoke(null, [sid, new SecurityIdentifier(sid.Value)])!
            && !(bool)identityCheck.Invoke(null, [sid, other])!
            && !(bool)identityCheck.Invoke(null, [null, null])!
            && !(bool)identityCheck.Invoke(null, [sid, null])!,
            "The Windows verifier accepted a different account or a missing identity as the current user.");
        foreach (var (library, exports) in new[] {
            ("credui.dll", new[] { "CredUIPromptForWindowsCredentialsW", "CredPackAuthenticationBufferW", "CredUnPackAuthenticationBufferW" }),
            ("advapi32.dll", new[] { "LogonUserW" }),
            ("user32.dll", new[] { "IsWindow", "GetForegroundWindow" }) })
        {
            Require(NativeLibrary.TryLoad(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), library), out var handle),
                "A required Windows credential library could not be loaded.");
            try { foreach (var export in exports) Require(NativeLibrary.TryGetExport(handle, export, out _), "A required native credential export is unavailable."); }
            finally { NativeLibrary.Free(handle); }
        }
        QaNativeCredentialBuffers.VerifySyntheticRoundtrip();

        var factory = typeof(CredentialClipboardService).GetMethod("CreateSecretData", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Protected clipboard data construction could not be verified.");
        var marker = Guid.NewGuid();
        const string clipboardSecret = "QA_PROTECTED_COPY_FIXTURE";
        var data = (DataObject)factory.Invoke(null, [clipboardSecret, marker])!;
        Require(data.GetData(DataFormats.UnicodeText, autoConvert: false) is string value && value == clipboardSecret,
            "Protected copy did not retain the exact secret text.");
        foreach (var format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
        {
            Require(data.GetData(format, autoConvert: false) is MemoryStream bytes && bytes.ToArray().Length == sizeof(uint)
                && BitConverter.ToUInt32(bytes.ToArray()) == 0,
                "Protected copy did not explicitly exclude clipboard history and cloud synchronization.");
        }
        Require(data.GetData("ExcludeClipboardContentFromMonitorProcessing", autoConvert: false) is MemoryStream exclusion && exclusion.Length > 0
            && data.GetData("OhMySsh.CredentialCopyMarker", autoConvert: false) is MemoryStream entry
            && entry.ToArray().SequenceEqual(marker.ToByteArray()),
            "Protected copy lacked monitor-exclusion metadata or its own cleanup marker.");
    }

    private static TextBox CredentialPlainText(SshCredentialsWindow window) => (TextBox)window.FindName("RevealedSecretBox");

    private Task VerifyCredentialClipboardPublicationAsync()
    {
        var time = new QaMonotonicTimeProvider();
        var sink = new QaCredentialClipboardSink { ThrowAfterPublish = true };
        var service = CreateControlledCredentialClipboard(sink, time);
        ExpectCredentialPublicationFailure(service);
        var firstMarker = sink.CurrentMarker ?? throw new InvalidOperationException("The partial-publication fixture did not publish clipboard data before failing.");
        time.Advance(TimeSpan.FromSeconds(29));
        ExpireCredentialClipboard(service);
        Require(sink.CurrentMarker == firstMarker && sink.Cleared.Count == 0,
            "A failed publication cleared its owned entry before the disclosure lifetime expired.");
        time.Advance(TimeSpan.FromSeconds(1));
        ExpireCredentialClipboard(service);
        Require(sink.CurrentMarker is null && sink.Cleared.SequenceEqual([firstMarker]),
            "A secret published before a clipboard exception lacked its bounded cleanup.");

        time = new QaMonotonicTimeProvider();
        sink = new QaCredentialClipboardSink();
        service = CreateControlledCredentialClipboard(sink, time);
        service.CopySecret("QA_OLDER_OWNED_ENTRY");
        var older = sink.CurrentMarker!.Value;
        time.Advance(TimeSpan.FromSeconds(10));
        sink.ThrowBeforePublish = true;
        ExpectCredentialPublicationFailure(service);
        Require(sink.CurrentMarker == older, "The failed-before-publication fixture unexpectedly replaced the older entry.");
        time.Advance(TimeSpan.FromSeconds(20));
        ExpireCredentialClipboard(service);
        Require(sink.CurrentMarker is null && sink.Cleared.SequenceEqual([older]),
            "A failed new copy discarded or extended cleanup of a previously owned secret.");
        var unrelated = Guid.NewGuid();
        sink.CurrentMarker = unrelated;
        time.Advance(TimeSpan.FromSeconds(10));
        ExpireCredentialClipboard(service);
        Require(sink.CurrentMarker == unrelated && sink.Cleared.SequenceEqual([older]),
            "Cleanup of a failed copy erased a later unrelated clipboard entry.");

        time = new QaMonotonicTimeProvider();
        sink = new QaCredentialClipboardSink();
        service = CreateControlledCredentialClipboard(sink, time);
        service.CopySecret("QA_FIRST_ENTRY");
        time.Advance(TimeSpan.FromSeconds(10));
        sink.ThrowAfterPublish = true;
        ExpectCredentialPublicationFailure(service);
        var newer = sink.CurrentMarker!.Value;
        time.Advance(TimeSpan.FromSeconds(20));
        ExpireCredentialClipboard(service);
        Require(sink.CurrentMarker == newer && sink.Cleared.Count == 0,
            "An older expiration erased a newer owned entry before its own deadline.");
        time.Advance(TimeSpan.FromSeconds(10));
        ExpireCredentialClipboard(service);
        Require(sink.CurrentMarker is null && sink.Cleared.SequenceEqual([newer]),
            "A partially published replacement secret was not cleaned at its own deadline.");
        return Task.CompletedTask;
    }

    private static CredentialClipboardService CreateControlledCredentialClipboard(QaCredentialClipboardSink sink, TimeProvider time)
        => (CredentialClipboardService)Activator.CreateInstance(typeof(CredentialClipboardService), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [new Action<DataObject>(sink.Publish), new Func<Guid, bool>(sink.ClearOwnedEntry), time], culture: null)!;

    private static void ExpireCredentialClipboard(CredentialClipboardService service)
        => typeof(CredentialClipboardService).GetMethod("CleanupExpiredEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);

    private static void ExpectCredentialPublicationFailure(CredentialClipboardService service)
    {
        var failed = false;
        try { service.CopySecret("QA_FAILED_COPY_FIXTURE"); }
        catch (ExternalException) { failed = true; }
        Require(failed, "The clipboard publication fixture did not exercise the failure boundary.");
    }

    private static async Task InvokeCredentialActionAsync(SshCredentialsWindow window, string buttonName)
    {
        ((Button)window.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await window.PendingDisclosure;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void RaiseCredentialDeactivated(Window window)
        => typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [EventArgs.Empty]);

    private static string CredentialSurfaceText(DependencyObject parent)
    {
        var pieces = new List<string>();
        if (parent is TextBlock block) pieces.Add(block.Text);
        if (parent is TextBox box) pieces.Add(box.Text);
        if (parent is Window window) pieces.Add(window.Title);
        if (parent is FrameworkElement { ToolTip: string tooltip }) pieces.Add(tooltip);
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
            pieces.Add(CredentialSurfaceText(System.Windows.Media.VisualTreeHelper.GetChild(parent, index)));
        return string.Join(' ', pieces);
    }
}

internal sealed class QaWindowsUserVerifier : IWindowsUserVerificationService
{
    public int Requests { get; private set; }
    public List<nint> Owners { get; } = [];
    public bool OwnerActive { get; set; } = true;
    public WindowsUserVerificationResult Result { get; set; } = WindowsUserVerificationResult.Rejected;
    public Func<nint, string, CancellationToken, Task<WindowsUserVerificationResult>>? Handler { get; set; }
    public bool IsOwnerActive(nint ownerWindow) => OwnerActive;
    public Task<WindowsUserVerificationResult> VerifyAsync(nint ownerWindow, string purpose, CancellationToken cancellationToken = default)
    {
        Requests++;
        Owners.Add(ownerWindow);
        return Handler?.Invoke(ownerWindow, purpose, cancellationToken) ?? Task.FromResult(Result);
    }
}

internal sealed class QaCredentialClipboard : ICredentialClipboardService
{
    public List<string> Addresses { get; } = [];
    public List<string> Secrets { get; } = [];
    public Exception? CopyFailure { get; set; }
    public void CopyAddress(string address) => Addresses.Add(address);
    public void CopySecret(string secret)
    {
        if (CopyFailure is { } failure) throw failure;
        Secrets.Add(secret);
    }
}

internal sealed class QaCredentialClipboardSink
{
    public Guid? CurrentMarker { get; set; }
    public bool ThrowBeforePublish { get; set; }
    public bool ThrowAfterPublish { get; set; }
    public List<Guid> Cleared { get; } = [];
    public void Publish(DataObject data)
    {
        if (ThrowBeforePublish) throw new ExternalException("QA publication failed before replacing the clipboard.");
        var marker = data.GetData("OhMySsh.CredentialCopyMarker", autoConvert: false) as MemoryStream
            ?? throw new InvalidOperationException("The clipboard fixture did not receive an ownership marker.");
        CurrentMarker = new Guid(marker.ToArray());
        if (ThrowAfterPublish) throw new ExternalException("QA publication failed after replacing the clipboard.");
    }
    public bool ClearOwnedEntry(Guid marker)
    {
        if (CurrentMarker == marker)
        {
            Cleared.Add(marker);
            CurrentMarker = null;
        }
        return true;
    }
}

internal static class QaNativeCredentialBuffers
{
    public static void VerifySyntheticRoundtrip()
    {
        const string account = @"QA\credential-fixture";
        const string secret = "QA_SYNTHETIC_NATIVE_BUFFER";
        nint packed = 0, username = 0, domain = 0, password = 0;
        uint packedBytes = 0, usernameBytes = 0, domainBytes = 0, passwordBytes = 0;
        try
        {
            uint required = 0;
            RequireNative(!CredPackAuthenticationBuffer(0x4, account, secret, 0, ref required)
                && Marshal.GetLastWin32Error() == 122 && required is > 0 and < 65536,
                "Native generic-credential packing did not report a bounded buffer size.");
            packedBytes = required;
            packed = Allocate(packedBytes);
            RequireNative(CredPackAuthenticationBuffer(0x4, account, secret, packed, ref required),
                "Native generic-credential packing failed for a synthetic fixture.");
            uint userChars = 0, domainChars = 0, passwordChars = 0;
            RequireNative(!CredUnPackAuthenticationBuffer(0, packed, required, 0, ref userChars, 0, ref domainChars, 0, ref passwordChars)
                && Marshal.GetLastWin32Error() == 122 && userChars is > 0 and < 4096
                && domainChars < 4096 && passwordChars is > 0 and < 4096,
                "Native credential unpacking did not report bounded identity and password buffers.");
            usernameBytes = checked(userChars * sizeof(char));
            domainChars = Math.Max(1u, domainChars);
            domainBytes = checked(domainChars * sizeof(char));
            passwordBytes = checked(passwordChars * sizeof(char));
            username = Allocate(usernameBytes);
            domain = Allocate(domainBytes);
            password = Allocate(passwordBytes);
            RequireNative(CredUnPackAuthenticationBuffer(0, packed, required, username, ref userChars,
                domain, ref domainChars, password, ref passwordChars),
                "Native credential unpacking failed for a synthetic fixture.");
            var userText = Marshal.PtrToStringUni(username) ?? string.Empty;
            var domainText = Marshal.PtrToStringUni(domain) ?? string.Empty;
            var principal = domainText.Length == 0 ? userText : domainText + "\\" + userText;
            RequireNative(principal == account && Marshal.PtrToStringUni(password) == secret,
                "Native credential packing/unpacking changed the synthetic account or secret.");
        }
        finally
        {
            ClearAndFree(password, passwordBytes);
            ClearAndFree(domain, domainBytes);
            ClearAndFree(username, usernameBytes);
            ClearAndFree(packed, packedBytes);
        }
    }

    private static nint Allocate(uint bytes)
    {
        var memory = Marshal.AllocHGlobal(checked((int)bytes));
        Marshal.Copy(new byte[checked((int)bytes)], 0, memory, checked((int)bytes));
        return memory;
    }

    private static void ClearAndFree(nint memory, uint bytes)
    {
        if (memory == 0) return;
        try { Marshal.Copy(new byte[checked((int)bytes)], 0, memory, checked((int)bytes)); }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void RequireNative(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("credui.dll", EntryPoint = "CredPackAuthenticationBufferW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredPackAuthenticationBuffer(uint flags, string username, string password, nint buffer, ref uint bytes);

    [DllImport("credui.dll", EntryPoint = "CredUnPackAuthenticationBufferW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBuffer(uint flags, nint buffer, uint bytes,
        nint username, ref uint userChars, nint domain, ref uint domainChars, nint password, ref uint passwordChars);
}
