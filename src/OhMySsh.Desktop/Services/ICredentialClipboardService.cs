namespace OhMySsh.Desktop.Services;

/// <summary>Separates ordinary address copying from short-lived, history-excluded secret copying.</summary>
public interface ICredentialClipboardService
{
    void CopyAddress(string address);
    void CopySecret(string secret);
}
