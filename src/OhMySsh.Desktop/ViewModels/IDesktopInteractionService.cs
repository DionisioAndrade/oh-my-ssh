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
}
