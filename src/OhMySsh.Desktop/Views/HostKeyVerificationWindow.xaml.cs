using System.Windows;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.Views;

public partial class HostKeyVerificationWindow : Window
{
    public HostKeyVerificationWindow(SshHostIdentity identity)
    {
        InitializeComponent();
        var host = identity.Host.Contains(':') ? $"[{identity.Host}]" : identity.Host;
        ServerText.Text = $"{host}:{identity.Port}";
        AlgorithmText.Text = $"Host key: {identity.KeyAlgorithm}";
        FingerprintTextBox.Text = identity.Sha256Fingerprint;
    }

    public HostKeyDecision Decision { get; private set; } = HostKeyDecision.Reject;

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
