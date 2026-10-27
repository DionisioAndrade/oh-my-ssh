using Microsoft.Win32;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Desktop.Services;

public sealed class DesktopInteractionService : IDesktopInteractionService
{
    public ConnectionProfile? EditConnection(ConnectionProfile? existing)
    {
        var window = new ConnectionEditorWindow(existing)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        return window.ShowDialog() == true ? window.Profile : null;
    }

    public bool ConfirmDelete(ConnectionProfile profile)
        => System.Windows.MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            $"Remover a conexão “{profile.Name}”?\n\nOs arquivos do servidor não serão alterados.",
            "Remover conexão",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    public string? PickLocalFolder(string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolher pasta local",
            InitialDirectory = initialPath,
            Multiselect = false
        };

        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true
            ? dialog.FolderName
            : null;
    }

    public void ShowInformation(string title, string message)
        => System.Windows.MessageBox.Show(
            System.Windows.Application.Current.MainWindow,
            message,
            title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);

    public void CopyToClipboard(string text) => System.Windows.Clipboard.SetText(text);
}
