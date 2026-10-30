using OhMySsh.Core.Localization;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
            UiText.Format("Dialog.RemoveConnectionQuestion", profile.Name),
            UiText.Get("Dialog.RemoveConnection"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes;

    public string? PickLocalFolder(string initialPath)
    {
        var dialog = new OpenFolderDialog
        {
            Title = UiText.Get("Dialog.ChooseLocalFolder"),
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
            Title = UiText.Get("Dialog.UploadFile"),
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public IReadOnlyList<string> PickUploadFiles(string initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Dialog.UploadFiles"),
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            CheckFileExists = true,
            Multiselect = true
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileNames : [];
    }

    public void ShowTransferDetails(TransferItemViewModel job)
    {
        if (!job.HasFailureDetails) return;
        var window = new TransferDetailsWindow(job);
        if (Application.Current.MainWindow is { IsVisible: true } owner) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    public void OpenLocalFolder(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("The local transfer folder was not found.");
        // A directory is passed as a Shell item, never embedded into a command line.
        using var process = Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true, Verb = "explore" });
    }

    public void RevealLocalFile(string file)
    {
        var fullPath = Path.GetFullPath(file);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The downloaded file was not found.");
        nint item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(fullPath, 0, out item, 0, out _));
            // A zero child count treats this absolute PIDL as the item to select in its parent.
            Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, 0, 0));
        }
        finally
        {
            if (item != 0) Marshal.FreeCoTaskMem(item);
        }
    }

    public Task OpenLocalFolderAsync(string directory) => RunShellActionAsync(() => OpenLocalFolder(directory));
    public Task RevealLocalFileAsync(string file) => RunShellActionAsync(() => RevealLocalFile(file));

    private static Task RunShellActionAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
                initialized = true;
                action();
                completion.SetResult();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The caller classifies this error; native details must never be displayed or logged.
                completion.SetException(exception);
            }
            finally
            {
                if (initialized) CoUninitialize();
            }
        }) { IsBackground = true, Name = "Transfer file shortcut" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint bindContext, out nint item, uint attributes, out uint returnedAttributes);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint childCount, nint children, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    public string? PickDownloadFile(string suggestedFileName, string initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("Dialog.DownloadFile"),
            FileName = suggestedFileName,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            AddExtension = false,
            CheckPathExists = true,
            OverwritePrompt = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickDownloadFolder(string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = UiText.Get("Dialog.DownloadFolder"),
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : string.Empty,
            Multiselect = false
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
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
