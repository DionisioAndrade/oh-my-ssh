namespace OhMySsh.Desktop.Services;

public enum WindowsUserVerificationResult { Verified, Rejected, Canceled, Unavailable }

/// <summary>Requires a fresh proof of the current Windows user's identity before disclosing a secret.</summary>
public interface IWindowsUserVerificationService
{
    Task<WindowsUserVerificationResult> VerifyAsync(nint ownerWindow, string purpose,
        CancellationToken cancellationToken = default);
    bool IsOwnerActive(nint ownerWindow);
}
