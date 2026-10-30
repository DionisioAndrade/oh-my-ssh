using OhMySsh.Core.Localization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.Services;

namespace OhMySsh.Desktop.Views;

/// <summary>Collects temporary credentials and gates explicit disclosure on current-user Windows authentication.</summary>
public partial class SshCredentialsWindow : Window
{
    private readonly ConnectionProfile _profile;
    private readonly RemoteAuthenticationMethod _method;
    private readonly bool _allowRemember;
    private readonly IWindowsUserVerificationService _verification;
    private readonly ICredentialClipboardService _clipboard;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _revealTimer;
    private CancellationTokenSource? _disclosureCancellation;
    private bool _initializing = true;
    private bool _verifying;
    private bool _closed;
    private long _disclosureVersion;
    private string? _validationKey;

    public SshCredentialsWindow(ConnectionProfile profile, bool allowRemember,
        IWindowsUserVerificationService? verification = null, ICredentialClipboardService? clipboard = null)
    {
        _profile = profile with { };
        _verification = verification ?? new WindowsUserVerificationService();
        _clipboard = clipboard ?? CredentialClipboardService.Shared;
        _allowRemember = allowRemember;
        _method = string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
            ? RemoteAuthenticationMethod.Password : RemoteAuthenticationMethod.PrivateKey;
        InitializeComponent();
        _revealTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(15) };
        _revealTimer.Tick += (_, _) => HideRevealedSecret();

        var host = profile.Host.Contains(':') ? $"[{profile.Host.Trim('[', ']')}]": profile.Host;
        EndpointText.Text = $"{profile.Username}@{host}:{profile.Port}";
        // A profile path is nonsecret metadata, but may contain an accidentally entered password.
        // Never echo it into this authentication surface, including tooltips or automation properties.
        KeyPathText.ToolTip = null;
        KeyPathText.Visibility = _method == RemoteAuthenticationMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        RememberCheckBox.IsEnabled = allowRemember;
        RefreshLocalizedText();

        _initializing = false;
        UpdateDisclosureButtons();
        UiText.Current.LanguageChanged += LanguageChanged;
        Loaded += (_, _) => SecretBox.Focus();
        Deactivated += (_, _) =>
        {
            HideRevealedSecret();
            // Native credential UI deactivates its owner while authentication is pending.
            // The verifier checks foreground ownership again before any disclosure.
            if (!_verifying) InvalidateDisclosure();
        };
        Closed += (_, _) =>
        {
            UiText.Current.LanguageChanged -= LanguageChanged;
            _closed = true;
            InvalidateDisclosure();
            _lifetime.Cancel();
            SecretBox.Clear();
            _lifetime.Dispose();
        };
    }

    public RemoteConnectionCredentials? Credentials { get; private set; }
    public Task PendingDisclosure { get; private set; } = Task.CompletedTask;

    private void LanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        MethodText.Text = UiText.Get(_method == RemoteAuthenticationMethod.Password ? "Dialog.PasswordMethod" : "Dialog.KeyMethod");
        SecretLabel.Content = UiText.Get(_method == RemoteAuthenticationMethod.Password ? "Dialog.PasswordLabel" : "Dialog.PassphraseLabel");
        CopySecretButton.Content = UiText.Get(_method == RemoteAuthenticationMethod.Password ? "Dialog.CopyPassword" : "Dialog.CopyPassphrase");
        StorageHintText.Text = UiText.Get(_allowRemember ? "Dialog.StorageHint" : "Dialog.TemporaryCredentials");
        RevealSecretButton.Content = UiText.Get(RevealedSecretBox.Visibility == Visibility.Visible ? "Dialog.Hide" : "Dialog.Show");
        if (_validationKey is not null) ValidationText.Text = UiText.Get(_validationKey);
    }

    private void ShowStatus(string? key)
    {
        _validationKey = key;
        ValidationText.Text = key is null ? string.Empty : UiText.Get(key);
    }

    private bool HasSecret()
    {
        using var secret = SecretBox.SecurePassword;
        return secret.Length > 0;
    }

    private void UpdateDisclosureButtons()
    {
        if (_initializing) return;
        var available = !_closed && !_verifying && HasSecret();
        RevealSecretButton.IsEnabled = available;
        CopySecretButton.IsEnabled = available;
        ConnectButton.IsEnabled = !_closed && !_verifying;
    }

    private void SecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        InvalidateDisclosure();
        ShowStatus(null);
        UpdateDisclosureButtons();
    }

    private void InvalidateDisclosure()
    {
        _disclosureVersion++;
        _disclosureCancellation?.Cancel();
        HideRevealedSecret();
    }

    private void HideRevealedSecret()
    {
        _revealTimer.Stop();
        RevealedSecretBox.Clear();
        RevealedSecretBox.Visibility = Visibility.Collapsed;
        SecretBox.Visibility = Visibility.Visible;
        RevealSecretButton.Content = UiText.Get("Dialog.Show");
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        try
        {
            _clipboard.CopyAddress(_profile.Host);
            ShowStatus("Dialog.AddressCopied");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowStatus("Dialog.ClipboardUnavailable");
        }
    }

    private void RevealSecret_Click(object sender, RoutedEventArgs e)
    {
        if (RevealedSecretBox.Visibility == Visibility.Visible)
        {
            InvalidateDisclosure();
            return;
        }
        PendingDisclosure = DiscloseAsync(copy: false);
    }

    private void CopySecret_Click(object sender, RoutedEventArgs e)
        => PendingDisclosure = DiscloseAsync(copy: true);

    private async Task DiscloseAsync(bool copy)
    {
        if (_closed || _verifying || !IsVisible || !HasSecret()) return;
        HideRevealedSecret();
        _verifying = true;
        var version = ++_disclosureVersion;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _disclosureCancellation = cancellation;
        UpdateDisclosureButtons();
        ShowStatus("Dialog.ConfirmWindows");
        var copyAttempted = false;
        try
        {
            var owner = new WindowInteropHelper(this).EnsureHandle();
            var result = await _verification.VerifyAsync(owner,
                copy ? UiText.Get("Dialog.ConfirmCopy") : UiText.Get("Dialog.ConfirmReveal"),
                cancellation.Token).WaitAsync(cancellation.Token);
            // Let the native credential dialog return foreground ownership before the final check.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_closed || !IsVisible || cancellation.IsCancellationRequested || version != _disclosureVersion) return;
            if (result != WindowsUserVerificationResult.Verified)
            {
                ShowStatus(result switch
                {
                    WindowsUserVerificationResult.Canceled => "Dialog.VerificationCanceled",
                    WindowsUserVerificationResult.Rejected => "Dialog.VerificationRejected",
                    _ => "Dialog.VerificationUnavailable"
                });
                return;
            }
            if (!_verification.IsOwnerActive(owner))
            {
                ShowStatus("Dialog.VerificationInactive");
                return;
            }
            if (!HasSecret()) return;

            // Read the SSH secret only after the successful, still-current disclosure decision.
            if (copy)
            {
                copyAttempted = true;
                _clipboard.CopySecret(SecretBox.Password);
                ShowStatus("Dialog.SecretCopied");
            }
            else
            {
                RevealedSecretBox.Text = SecretBox.Password;
                SecretBox.Visibility = Visibility.Collapsed;
                RevealedSecretBox.Visibility = Visibility.Visible;
                RevealSecretButton.Content = UiText.Get("Dialog.Hide");
                _revealTimer.Start();
                ShowStatus("Dialog.SecretRevealed");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            HideRevealedSecret();
            if (!_closed) ShowStatus(copyAttempted
                ? "Dialog.CopyUnconfirmed"
                : "Dialog.ProtectedActionFailed");
        }
        finally
        {
            if (ReferenceEquals(_disclosureCancellation, cancellation)) _disclosureCancellation = null;
            _verifying = false;
            UpdateDisclosureButtons();
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _verifying) return;
        InvalidateDisclosure();
        using var secureValue = SecretBox.SecurePassword;
        if (_method == RemoteAuthenticationMethod.Password && secureValue.Length == 0)
        {
            ShowStatus("Dialog.PasswordRequired");
            SecretBox.Focus();
            return;
        }
        Credentials = new RemoteConnectionCredentials(_method, SecretBox.Password,
            _allowRemember && RememberCheckBox.IsChecked == true);
        SecretBox.Clear();
        DialogResult = true;
    }
}
