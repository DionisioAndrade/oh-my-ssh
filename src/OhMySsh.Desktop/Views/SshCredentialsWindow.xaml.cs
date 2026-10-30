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
        MethodText.Text = _method == RemoteAuthenticationMethod.Password ? "Password authentication" : "Private-key authentication";
        // A profile path is nonsecret metadata, but may contain an accidentally entered password.
        // Never echo it into this authentication surface, including tooltips or automation properties.
        KeyPathText.ToolTip = null;
        KeyPathText.Visibility = _method == RemoteAuthenticationMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Content = _method == RemoteAuthenticationMethod.Password ? "_Password" : "_Passphrase (optional for an unencrypted key)";
        CopySecretButton.Content = _method == RemoteAuthenticationMethod.Password ? "Copy password" : "Copy passphrase";
        RememberCheckBox.IsEnabled = allowRemember;
        if (!allowRemember)
            StorageHintText.Text = "Manual connection credentials are temporary. Save a VPS profile to remember credentials.";

        _initializing = false;
        UpdateDisclosureButtons();
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
            _closed = true;
            InvalidateDisclosure();
            _lifetime.Cancel();
            SecretBox.Clear();
            _lifetime.Dispose();
        };
    }

    public RemoteConnectionCredentials? Credentials { get; private set; }
    public Task PendingDisclosure { get; private set; } = Task.CompletedTask;

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
        ValidationText.Text = string.Empty;
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
        RevealSecretButton.Content = "Show";
    }

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (_closed) return;
        try
        {
            _clipboard.CopyAddress(_profile.Host);
            ValidationText.Text = "Server address copied.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ValidationText.Text = "The clipboard is unavailable. Try copying again.";
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
        ValidationText.Text = "Confirm your Windows account password in the system dialog.";
        var copyAttempted = false;
        try
        {
            var owner = new WindowInteropHelper(this).EnsureHandle();
            var result = await _verification.VerifyAsync(owner,
                copy ? "Confirm your Windows password to copy the SSH secret." : "Confirm your Windows password to reveal the SSH secret.",
                cancellation.Token).WaitAsync(cancellation.Token);
            // Let the native credential dialog return foreground ownership before the final check.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (_closed || !IsVisible || cancellation.IsCancellationRequested || version != _disclosureVersion) return;
            if (result != WindowsUserVerificationResult.Verified)
            {
                ValidationText.Text = result switch
                {
                    WindowsUserVerificationResult.Canceled => "Windows authentication was canceled. The secret stays hidden.",
                    WindowsUserVerificationResult.Rejected => "Windows authentication failed. Use the password of the current Windows account.",
                    _ => "Windows password verification is unavailable. The secret stays hidden."
                };
                return;
            }
            if (!_verification.IsOwnerActive(owner))
            {
                ValidationText.Text = "Return to this window and authenticate again. The secret stays hidden.";
                return;
            }
            if (!HasSecret()) return;

            // Read the SSH secret only after the successful, still-current disclosure decision.
            if (copy)
            {
                copyAttempted = true;
                _clipboard.CopySecret(SecretBox.Password);
                ValidationText.Text = "Secret copied. This app's clipboard entry will be cleared after 30 seconds.";
            }
            else
            {
                RevealedSecretBox.Text = SecretBox.Password;
                SecretBox.Visibility = Visibility.Collapsed;
                RevealedSecretBox.Visibility = Visibility.Visible;
                RevealSecretButton.Content = "Hide";
                _revealTimer.Start();
                ValidationText.Text = "Secret shown temporarily. It hides after 15 seconds or when this window loses focus.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            HideRevealedSecret();
            if (!_closed) ValidationText.Text = copyAttempted
                ? "The copy could not be confirmed. Any clipboard entry created by this app is still scheduled for cleanup."
                : "The protected action could not be completed. The secret stays hidden.";
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
            ValidationText.Text = "Enter the SSH password.";
            SecretBox.Focus();
            return;
        }
        Credentials = new RemoteConnectionCredentials(_method, SecretBox.Password,
            _allowRemember && RememberCheckBox.IsChecked == true);
        SecretBox.Clear();
        DialogResult = true;
    }
}
