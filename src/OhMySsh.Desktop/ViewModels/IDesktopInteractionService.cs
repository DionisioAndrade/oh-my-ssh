using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Keeps native dialogs and clipboard access outside the view model.</summary>
public interface IDesktopInteractionService
{
    ConnectionProfile? EditConnection(ConnectionProfile? existing);
    bool ConfirmDelete(ConnectionProfile profile);
    string? PickLocalFolder(string initialPath);
    void ShowInformation(string title, string message);
    void CopyToClipboard(string text);
    string? PickUploadFile(string initialDirectory) => null;
    IReadOnlyList<string> PickUploadFiles(string initialDirectory)
        => PickUploadFile(initialDirectory) is { } path ? [path] : [];
    string? PickDownloadFile(string suggestedFileName, string initialDirectory) => null;
    void ShowTransferDetails(TransferItemViewModel job) { }
    void OpenLocalFolder(string directory) { }
    void RevealLocalFile(string file) { }
    Task OpenLocalFolderAsync(string directory)
    {
        OpenLocalFolder(directory);
        return Task.CompletedTask;
    }
    Task RevealLocalFileAsync(string file)
    {
        RevealLocalFile(file);
        return Task.CompletedTask;
    }
    Task<bool> ConfirmOverwriteAsync(string destination, bool isRemote, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
