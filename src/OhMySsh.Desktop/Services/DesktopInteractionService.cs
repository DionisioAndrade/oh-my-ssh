using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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

    public string? PickUploadFile(string initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Enviar arquivo para a VPS",
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickDownloadFile(string suggestedFileName, string initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Baixar arquivo da VPS",
            FileName = suggestedFileName,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            AddExtension = false,
            CheckPathExists = true,
            OverwritePrompt = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public async Task<bool> ConfirmOverwriteAsync(string destination, bool isRemote, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dispatcher = Application.Current.Dispatcher;
        return await dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new OverwriteConfirmationWindow(destination, isRemote);
            if (Application.Current.MainWindow is { IsVisible: true } owner) window.Owner = owner;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            using var cancellation = cancellationToken.Register(() =>
            {
                if (!dispatcher.HasShutdownStarted)
                    dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        if (window.IsVisible) window.Close();
                    }));
            });
            var confirmed = window.ShowDialog() == true;
            cancellationToken.ThrowIfCancellationRequested();
            return confirmed;
        });
    }
}
