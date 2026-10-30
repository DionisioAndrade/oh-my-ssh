using OhMySsh.Core.Localization;
using System.Windows;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.Views;

public partial class HostKeyVerificationWindow : Window
{
    private readonly string _keyAlgorithm;

    public HostKeyVerificationWindow(SshHostIdentity identity)
    {
        _keyAlgorithm = identity.KeyAlgorithm;
        InitializeComponent();
        var host = identity.Host.Contains(':') ? $"[{identity.Host}]" : identity.Host;
        ServerText.Text = $"{host}:{identity.Port}";
        RefreshLocalizedText();
        FingerprintTextBox.Text = identity.Sha256Fingerprint;
        UiText.Current.LanguageChanged += LanguageChanged;
        Closed += (_, _) => UiText.Current.LanguageChanged -= LanguageChanged;
    }

    public HostKeyDecision Decision { get; private set; } = HostKeyDecision.Reject;

    private void LanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText() => AlgorithmText.Text = UiText.Format("Dialog.HostKey", _keyAlgorithm);

    private void TrustOnce_Click(object sender, RoutedEventArgs e)
    {
        Decision = HostKeyDecision.TrustOnce;
        DialogResult = true;
    }

    private void TrustAndRemember_Click(object sender, RoutedEventArgs e)
    {
        Decision = HostKeyDecision.TrustAndRemember;
        DialogResult = true;
    }
}
