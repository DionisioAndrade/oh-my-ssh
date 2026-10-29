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

    public ConnectionEditorWindow(ConnectionProfile? existing = null)
    {
        InitializeComponent();
        _existing = existing;

        if (existing is not null)
        {
            Title = "Editar VPS";
            HeadingText.Text = Title;
            SaveButton.Content = "Salvar alterações";
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
        UpdateProtocolDescription(GetProtocol());
        _isInitializing = false;
    }

    public ConnectionProfile? Profile { get; private set; }

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
            ConnectionProtocol.Ssh => "Terminal e arquivos pela mesma conexão SSH.",
            ConnectionProtocol.Sftp => "Transferência de arquivos pela conexão SSH.",
            ConnectionProtocol.Ftp => "Transferência pelo protocolo FTP.",
            ConnectionProtocol.Ftps => "FTP com TLS explícito. A porta padrão é 21.",
            _ => string.Empty
        };
        PrivateKeyPanel.Visibility = protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp
            ? Visibility.Visible
            : Visibility.Collapsed;
        var usernameLabel = protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp
            ? "Usuário SSH"
            : "Usuário FTP";
        UsernameLabel.Content = $"_{usernameLabel}";
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
            Title = "Selecionar chave privada",
            Filter = "Todos os arquivos (*.*)|*.*",
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
        var name = NameTextBox.Text.Trim();
        var host = HostTextBox.Text.Trim();
        var username = UsernameTextBox.Text.Trim();
        var group = GroupTextBox.Text.Trim();
        var keyPath = PrivateKeyTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation("Informe um nome para identificar a VPS ou servidor.", NameTextBox);
            return;
        }

        if (name.Length > 100 || name.Any(char.IsControl))
        {
            ShowValidation("Use um nome com até 100 caracteres, sem quebras de linha ou caracteres de controle.", NameTextBox);
            return;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            ShowValidation("Informe o nome ou endereço IP do servidor.", HostTextBox);
            return;
        }

        if (host.Length > 255 || host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            host.Contains("://", StringComparison.Ordinal) ||
            host.IndexOfAny(['/', '\\', '?', '#', '@']) >= 0)
        {
            ShowValidation("Use apenas o nome do servidor ou endereço IP, sem protocolo, usuário ou caminho.", HostTextBox);
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
                ShowValidation("Use colchetes apenas ao redor de um endereço IPv6 válido, sem a porta.", HostTextBox);
                return;
            }

            normalizedHost = host[1..^1];
        }

        // IPv6 may contain colons; a hostname followed by a port may not.
        if (normalizedHost.Contains(':') && !IPAddress.TryParse(normalizedHost, out _))
        {
            ShowValidation("Informe a porta no campo Porta, separada do endereço do servidor.", HostTextBox);
            return;
        }

        if (!IPAddress.TryParse(normalizedHost, out _) &&
            Uri.CheckHostName(normalizedHost) != UriHostNameType.Dns)
        {
            ShowValidation("Informe um endereço IP ou domínio válido, como servidor.exemplo.com.", HostTextBox);
            return;
        }

        if (!int.TryParse(PortTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            ShowValidation("Informe uma porta entre 1 e 65535.", PortTextBox);
            return;
        }

        if (!int.TryParse(TimeoutTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var timeout)
            || timeout is < 1 or > 300)
        {
            ShowValidation("Enter a connection timeout between 1 and 300 seconds.", TimeoutTextBox);
            return;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            ShowValidation("Informe o usuário da conexão.", UsernameTextBox);
            return;
        }

        if (username.Length > 128 || username.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            ShowValidation("Use um usuário com até 128 caracteres, sem espaços, quebras de linha ou caracteres de controle.", UsernameTextBox);
            return;
        }

        if (group.Length > 80 || group.Any(char.IsControl))
        {
            ShowValidation("Use um grupo com até 80 caracteres, sem quebras de linha ou caracteres de controle.", GroupTextBox);
            return;
        }

        if (keyPath.Any(char.IsControl))
        {
            ShowValidation("Informe o caminho da chave em uma única linha, sem caracteres de controle.", PrivateKeyTextBox);
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

    private void ShowValidation(string message, TextBox field)
    {
        ValidationText.Text = message;
        ValidationPanel.Visibility = Visibility.Visible;
        field.Focus();
        field.SelectAll();
        field.BringIntoView();
    }
}
