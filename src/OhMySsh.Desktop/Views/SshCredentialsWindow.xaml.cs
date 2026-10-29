using System.Windows;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.Views;

/// <summary>Collects a temporary secret without placing it in a profile or view-model property.</summary>
public partial class SshCredentialsWindow : Window
{
    private readonly RemoteAuthenticationMethod _method;
    private readonly bool _allowRemember;

    public SshCredentialsWindow(ConnectionProfile profile, bool allowRemember)
    {
        InitializeComponent();
        _allowRemember = allowRemember;
        _method = string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
            ? RemoteAuthenticationMethod.Password : RemoteAuthenticationMethod.PrivateKey;
        var host = profile.Host.Contains(':') ? $"[{profile.Host}]" : profile.Host;
        EndpointText.Text = $"{profile.Username}@{host}:{profile.Port}";
        MethodText.Text = _method == RemoteAuthenticationMethod.Password ? "Password authentication" : "Private-key authentication";
        KeyPathText.Text = profile.PrivateKeyPath ?? string.Empty;
        KeyPathText.ToolTip = profile.PrivateKeyPath;
        KeyPathText.Visibility = _method == RemoteAuthenticationMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        SecretLabel.Content = _method == RemoteAuthenticationMethod.Password ? "_Password" : "_Passphrase (optional for an unencrypted key)";
        RememberCheckBox.IsEnabled = allowRemember;
        if (!allowRemember)
        {
            StorageHintText.Text = "Manual connection credentials are temporary. Save a VPS profile to remember credentials.";
        }

        Loaded += (_, _) => SecretBox.Focus();
        Closed += (_, _) => SecretBox.Clear();
    }

    public RemoteConnectionCredentials? Credentials { get; private set; }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
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
