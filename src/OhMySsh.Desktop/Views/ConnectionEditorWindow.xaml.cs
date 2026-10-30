using OhMySsh.Core.Localization;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.Views;

/// <summary>Edits the non-secret details of a saved connection profile.</summary>
public partial class ConnectionEditorWindow : Window
{
    private readonly ConnectionProfile? _existing;
    private ConnectionProtocol _previousProtocol = ConnectionProtocol.Ssh;
    private bool _isInitializing = true;
    private string? _validationKey;

    public ConnectionEditorWindow(ConnectionProfile? existing = null)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            NameTextBox.Text = existing.Name;
            HostTextBox.Text = existing.Host;
            PortTextBox.Text = existing.Port.ToString(CultureInfo.InvariantCulture);
            TimeoutTextBox.Text = existing.ConnectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            UsernameTextBox.Text = existing.Username;
            GroupTextBox.Text = existing.Group;
            PrivateKeyTextBox.Text = existing.PrivateKeyPath ?? string.Empty;
            FavoriteCheckBox.IsChecked = existing.IsFavorite;
            _previousProtocol = existing.Protocol;
        }

        ProtocolComboBox.SelectedIndex = (int)(existing?.Protocol ?? ConnectionProtocol.Ssh);
        RefreshLocalizedText();
        _isInitializing = false;
        UiText.Current.LanguageChanged += LanguageChanged;
        Closed += (_, _) => UiText.Current.LanguageChanged -= LanguageChanged;
    }

    public ConnectionProfile? Profile { get; private set; }

    private void LanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        Title = UiText.Get(_existing is null ? "Dialog.SaveVps" : "Dialog.EditVps");
        HeadingText.Text = Title;
        SaveButton.Content = UiText.Get(_existing is null ? "Dialog.SaveVps" : "Dialog.SaveChanges");
        UpdateProtocolDescription(GetProtocol());
        if (_validationKey is not null) ValidationText.Text = UiText.Get(_validationKey);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        NameTextBox.Focus();
        NameTextBox.SelectAll();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            SaveProfile();
        }
    }

    private void ProtocolComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire while the XAML is still being initialized.
        if (PortTextBox is null || ProtocolDescriptionText is null || PrivateKeyPanel is null)
        {
            return;
        }

        var protocol = GetProtocol();
        if (!_isInitializing && int.TryParse(PortTextBox.Text, out var port) &&
            port == GetDefaultPort(_previousProtocol))
        {
            PortTextBox.Text = GetDefaultPort(protocol).ToString(CultureInfo.InvariantCulture);
        }

        _previousProtocol = protocol;
        UpdateProtocolDescription(protocol);
    }

    private void UpdateProtocolDescription(ConnectionProtocol protocol)
    {
        ProtocolDescriptionText.Text = protocol switch
        {
            ConnectionProtocol.Ssh => UiText.Get("Dialog.SshDescription"),
            ConnectionProtocol.Sftp => UiText.Get("Dialog.SftpDescription"),
            ConnectionProtocol.Ftp => UiText.Get("Dialog.FtpDescription"),
            ConnectionProtocol.Ftps => UiText.Get("Dialog.FtpsDescription"),
            _ => string.Empty
        };
        PrivateKeyPanel.Visibility = protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp
            ? Visibility.Visible
            : Visibility.Collapsed;
        var usernameLabel = protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp
            ? UiText.Get("Dialog.SshUsername")
            : UiText.Get("Dialog.FtpUsername");
        UsernameLabel.Content = UiText.Get(protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp
            ? "Dialog.SshUsernameLabel" : "Dialog.FtpUsernameLabel");
        System.Windows.Automation.AutomationProperties.SetName(UsernameTextBox, usernameLabel);
    }

    private ConnectionProtocol GetProtocol() =>
        ProtocolComboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
        Enum.TryParse<ConnectionProtocol>(tag, out var protocol)
            ? protocol
            : ConnectionProtocol.Ssh;

    private static int GetDefaultPort(ConnectionProtocol protocol) =>
        protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp ? 22 : 21;

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Dialog.ChoosePrivateKey"),
            Filter = UiText.Get("Dialog.AllFiles"),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyTextBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProfile();

    private void SaveProfile()
    {
        ValidationPanel.Visibility = Visibility.Collapsed;
        _validationKey = null;
        var name = NameTextBox.Text.Trim();
        var host = HostTextBox.Text.Trim();
        var username = UsernameTextBox.Text.Trim();
        var group = GroupTextBox.Text.Trim();
        var keyPath = PrivateKeyTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation("Dialog.ValidationNameRequired", NameTextBox);
            return;
        }

        if (name.Length > 100 || name.Any(char.IsControl))
        {
            ShowValidation("Dialog.ValidationName", NameTextBox);
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            ShowValidation("Dialog.ValidationHostRequired", HostTextBox);
            return;
        }

        if (host.Length > 255 || host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            host.Contains("://", StringComparison.Ordinal) ||
            host.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
        {
            ShowValidation("Dialog.ValidationHost", HostTextBox);
            return;
        }

        var bracketedHost = host.StartsWith('[') || host.EndsWith(']');
        var normalizedHost = host;
        if (bracketedHost)
        {
            if (!host.StartsWith('[') || !host.EndsWith(']') ||
                !IPAddress.TryParse(host[1..^1], out var bracketedAddress) ||
                bracketedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                ShowValidation("Dialog.ValidationIpv6", HostTextBox);
                return;
            }

            normalizedHost = host[1..^1];
        }

        // IPv6 may contain colons; a hostname followed by a port may not.
        if (normalizedHost.Contains(':') && !IPAddress.TryParse(normalizedHost, out _))
        {
            ShowValidation("Dialog.ValidationSeparatePort", HostTextBox);
            return;
        }

        if (!IPAddress.TryParse(normalizedHost, out _) &&
            Uri.CheckHostName(normalizedHost) != UriHostNameType.Dns)
        {
            ShowValidation("Dialog.ValidationDomain", HostTextBox);
            return;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            ShowValidation("Dialog.ValidationPort", PortTextBox);
            return;
        }

        if (!int.TryParse(TimeoutTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var timeout)
            || timeout is < 1 or > 300)
        {
            ShowValidation("Dialog.ValidationTimeout", TimeoutTextBox);
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowValidation("Dialog.ValidationUsernameRequired", UsernameTextBox);
            return;
        }

        if (username.Length > 128 || username.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            ShowValidation("Dialog.ValidationUsername", UsernameTextBox);
            return;
        }

        if (group.Length > 80 || group.Any(char.IsControl))
        {
            ShowValidation("Dialog.ValidationGroup", GroupTextBox);
            return;
        }

        if (keyPath.Any(char.IsControl))
        {
            ShowValidation("Dialog.ValidationKeyPath", PrivateKeyTextBox);
            return;
        }

        var protocol = GetProtocol();
        Profile = (_existing ?? new ConnectionProfile()) with
        {
            Name = name,
            Host = normalizedHost,
            Port = port,
            ConnectionTimeoutSeconds = timeout,
            Username = username,
            Group = group.Length > 0 ? group : _existing?.Group ?? "VPS",
            Protocol = protocol,
            PrivateKeyPath = protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp && keyPath.Length > 0
                ? keyPath
                : null,
            IsFavorite = FavoriteCheckBox.IsChecked == true
        };

        DialogResult = true;
    }

    private void ShowValidation(string key, TextBox field)
    {
        _validationKey = key;
        ValidationText.Text = UiText.Get(key);
        ValidationPanel.Visibility = Visibility.Visible;
        field.Focus();
        field.SelectAll();
        field.BringIntoView();
    }
}
