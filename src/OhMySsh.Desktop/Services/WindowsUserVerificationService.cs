using OhMySsh.Core.Localization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OhMySsh.Desktop.Services;

/// <summary>
/// Uses the native password provider, authenticates the supplied password, then compares token SIDs.
/// A successful credential prompt alone never grants permission to disclose a secret.
/// </summary>
public sealed class WindowsUserVerificationService : IWindowsUserVerificationService
{
    private const uint GenericPrompt = 0x1;
    private const uint GenericCredentialPack = 0x4;
    private const uint DoNotPersist = 0x2;
    private const uint AlwaysShowUi = 0x80;
    private const uint GenericCredentials = 0x40000;
    private const uint KeepUsername = 0x100000;
    private const uint LocalPasswordPromptFlags = DoNotPersist | AlwaysShowUi | GenericCredentials | KeepUsername;
    // WinCred limits include an additional character for the terminating NUL.
    private const uint LocalUsernameCharacters = 514;
    private const uint LocalPasswordCharacters = 257;
    private const int InsufficientBuffer = 122;
    private const uint UserCanceled = 1223;
    private const uint TokenQuery = 0x8;
    private const int NetworkLogon = 3;
    private const int DefaultProvider = 0;
    private const uint MaximumAuthenticationBytes = 1024 * 1024;
    private const uint MaximumIdentityCharacters = 1024;
    private const uint MaximumPasswordCharacters = 65536;
    private static readonly byte[] ZeroBlock = new byte[4096];

    public async Task<WindowsUserVerificationResult> VerifyAsync(nint ownerWindow, string purpose,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return WindowsUserVerificationResult.Canceled;
        if (!IsOwnerActive(ownerWindow)) return WindowsUserVerificationResult.Unavailable;

        SecurityIdentifier? expectedUser;
        string accountName;
        try
        {
            // Open the process token explicitly: a thread impersonation token must not redefine the owner.
            var tokenOpened = OpenProcessToken(GetCurrentProcess(), TokenQuery, out var processToken);
            using (processToken)
            {
                if (!tokenOpened || processToken.IsInvalid) return WindowsUserVerificationResult.Unavailable;
                using var identity = new WindowsIdentity(processToken.DangerousGetHandle());
                expectedUser = identity.User;
                accountName = identity.Name;
            }
            if (expectedUser is null || string.IsNullOrWhiteSpace(accountName))
                return WindowsUserVerificationResult.Unavailable;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return WindowsUserVerificationResult.Unavailable;
        }

        var completion = new TaskCompletionSource<WindowsUserVerificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var thread = new Thread(() =>
            {
                var initialized = false;
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetResult(WindowsUserVerificationResult.Canceled);
                        return;
                    }
                    if (!IsOwnerActive(ownerWindow))
                    {
                        completion.TrySetResult(WindowsUserVerificationResult.Unavailable);
                        return;
                    }
                    if (CoInitializeEx(0, 2) < 0)
                    {
                        completion.TrySetResult(WindowsUserVerificationResult.Unavailable);
                        return;
                    }
                    initialized = true;
                    completion.TrySetResult(VerifyPassword(ownerWindow, purpose, expectedUser, accountName, cancellationToken));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    completion.TrySetResult(WindowsUserVerificationResult.Unavailable);
                }
                finally
                {
                    if (initialized) CoUninitialize();
                }
            }) { IsBackground = true, Name = "Windows credential verification" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // CredUI has no cancellation-token API. A late native result is discarded after cancellation.
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WindowsUserVerificationResult.Canceled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return WindowsUserVerificationResult.Unavailable;
        }
    }

    public bool IsOwnerActive(nint ownerWindow)
    {
        if (!OperatingSystem.IsWindows() || ownerWindow == 0) return false;
        try { return IsWindow(ownerWindow) && GetForegroundWindow() == ownerWindow; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private static WindowsUserVerificationResult VerifyPassword(nint ownerWindow, string purpose,
        SecurityIdentifier expectedUser, string accountName, CancellationToken cancellationToken)
    {
        if (TryGetLocalAccount(accountName, Environment.MachineName, out var localUsername))
            return VerifyLocalPassword(ownerWindow, purpose, expectedUser, localUsername, cancellationToken);

        nint input = 0, output = 0, username = 0, domain = 0, password = 0;
        uint inputBytes = 0, inputCapacity = 0, outputBytes = 0, usernameCapacity = 0, domainCapacity = 0, passwordCapacity = 0;
        try
        {
            // Prefill only the account identity; no Windows password is cached or supplied by the app.
            var sized = CredPackAuthenticationBuffer(GenericCredentialPack, accountName, string.Empty, 0, ref inputBytes);
            if ((!sized && Marshal.GetLastWin32Error() != InsufficientBuffer)
                || inputBytes == 0 || inputBytes > MaximumAuthenticationBytes)
                return WindowsUserVerificationResult.Unavailable;
            inputCapacity = inputBytes;
            input = Marshal.AllocHGlobal(checked((int)inputCapacity));
            ZeroMemory(input, inputCapacity);
            if (!CredPackAuthenticationBuffer(GenericCredentialPack, accountName, string.Empty, input, ref inputBytes))
                return WindowsUserVerificationResult.Unavailable;
            if (cancellationToken.IsCancellationRequested) return WindowsUserVerificationResult.Canceled;

            var info = new CredentialUiInfo
            {
                Size = Marshal.SizeOf<CredentialUiInfo>(), Parent = ownerWindow,
                Caption = UiText.Get("Dialog.WindowsVerification"), Message = purpose, Banner = 0
            };
            uint package = 0;
            // Generic requests an actual password; it cannot be combined with SECURE_PROMPT.
            // No Save checkbox, administrator enumeration, PIN fallback, or persisted authorization is used.
            var result = CredUIPromptForWindowsCredentials(ref info, 0, ref package,
                input, inputBytes, out output, out outputBytes, 0, GenericPrompt);
            if (cancellationToken.IsCancellationRequested || result == UserCanceled)
                return WindowsUserVerificationResult.Canceled;
            if (result != 0 || output == 0 || outputBytes == 0 || outputBytes > MaximumAuthenticationBytes)
                return WindowsUserVerificationResult.Unavailable;

            uint usernameChars = 0, domainChars = 0, passwordChars = 0;
            if (CredUnPackAuthenticationBuffer(0, output, outputBytes,
                0, ref usernameChars, 0, ref domainChars, 0, ref passwordChars)
                || Marshal.GetLastWin32Error() != InsufficientBuffer
                || usernameChars is 0 or > MaximumIdentityCharacters || domainChars > MaximumIdentityCharacters
                || passwordChars is < 2 or > MaximumPasswordCharacters)
                return WindowsUserVerificationResult.Rejected;

            usernameCapacity = usernameChars;
            domainCapacity = Math.Max(1, domainChars);
            passwordCapacity = passwordChars;
            username = AllocateCharacters(usernameCapacity);
            domain = AllocateCharacters(domainCapacity);
            password = AllocateCharacters(passwordCapacity);
            domainChars = domainCapacity;
            if (!CredUnPackAuthenticationBuffer(0, output, outputBytes,
                username, ref usernameChars, domain, ref domainChars, password, ref passwordChars))
                return WindowsUserVerificationResult.Rejected;
            if (Marshal.ReadInt16(password) == 0) return WindowsUserVerificationResult.Rejected;

            // Password stays in unmanaged memory until it is cleared in finally.
            var usernameText = Marshal.PtrToStringUni(username) ?? string.Empty;
            var domainText = Marshal.PtrToStringUni(domain) ?? string.Empty;
            if (!TrySplitAccount(usernameText, domainText, out var loginName, out var loginDomain))
                return WindowsUserVerificationResult.Rejected;
            if (cancellationToken.IsCancellationRequested) return WindowsUserVerificationResult.Canceled;
            return AuthenticatePassword(expectedUser, loginName, loginDomain, password, cancellationToken);
        }
        finally
        {
            ClearAndFree(username, usernameCapacity * sizeof(char), coTaskMemory: false);
            ClearAndFree(domain, domainCapacity * sizeof(char), coTaskMemory: false);
            ClearAndFree(password, passwordCapacity * sizeof(char), coTaskMemory: false);
            ClearAndFree(input, inputCapacity, coTaskMemory: false);
            ClearAndFree(output, outputBytes, coTaskMemory: true);
        }
    }

    private static WindowsUserVerificationResult VerifyLocalPassword(nint ownerWindow, string purpose,
        SecurityIdentifier expectedUser, string localUsername, CancellationToken cancellationToken)
    {
        var accountName = $"{Environment.MachineName}\\{localUsername}";
        if (accountName.Length >= LocalUsernameCharacters) return WindowsUserVerificationResult.Unavailable;
        nint username = 0, password = 0;
        try
        {
            username = AllocateCharacters(LocalUsernameCharacters);
            password = AllocateCharacters(LocalPasswordCharacters);
            Marshal.Copy(accountName.ToCharArray(), 0, username, accountName.Length);
            if (cancellationToken.IsCancellationRequested) return WindowsUserVerificationResult.Canceled;

            var info = new CredentialUiInfo
            {
                Size = Marshal.SizeOf<CredentialUiInfo>(), Parent = ownerWindow,
                Caption = UiText.Get("Dialog.WindowsVerification"),
                Message = UiText.Format("Dialog.LocalWindowsVerificationMessage", purpose, accountName), Banner = 0
            };
            // This password-only native dialog fixes the local account instead of listing cloud providers.
            // It always requests fresh input and neither reads nor writes a saved Windows password.
            var save = false;
            var result = CredUIPromptForCredentials(ref info, $"oh-my-ssh:WindowsIdentity:{Guid.NewGuid():N}", 0, 0,
                username, LocalUsernameCharacters, password, LocalPasswordCharacters, ref save, LocalPasswordPromptFlags);
            if (cancellationToken.IsCancellationRequested || result == UserCanceled)
                return WindowsUserVerificationResult.Canceled;
            if (result != 0) return WindowsUserVerificationResult.Unavailable;
            if (Marshal.ReadInt16(password) == 0) return WindowsUserVerificationResult.Rejected;

            // Do not let an altered prompt result select another account or a Microsoft identity.
            var returnedAccount = Marshal.PtrToStringUni(username) ?? string.Empty;
            if (!TryGetLocalAccount(returnedAccount, Environment.MachineName, out var returnedUsername)
                || !string.Equals(localUsername, returnedUsername, StringComparison.OrdinalIgnoreCase))
                return WindowsUserVerificationResult.Rejected;

            // A dot explicitly selects the local SAM, regardless of provider/account suggestions.
            return AuthenticatePassword(expectedUser, localUsername, ".", password, cancellationToken);
        }
        finally
        {
            ClearAndFree(username, LocalUsernameCharacters * sizeof(char), coTaskMemory: false);
            ClearAndFree(password, LocalPasswordCharacters * sizeof(char), coTaskMemory: false);
        }
    }

    private static WindowsUserVerificationResult AuthenticatePassword(SecurityIdentifier expectedUser,
        string loginName, string? loginDomain, nint password, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return WindowsUserVerificationResult.Canceled;
        // NETWORK verifies the password without caching it. NEW_CREDENTIALS would not prove authentication.
        if (!LogonUser(loginName, loginDomain, password, NetworkLogon, DefaultProvider, out var token))
        {
            token?.Dispose();
            return WindowsUserVerificationResult.Rejected;
        }
        using (token)
        {
            if (token.IsInvalid || token.IsClosed) return WindowsUserVerificationResult.Rejected;
            using var authenticated = new WindowsIdentity(token.DangerousGetHandle());
            if (cancellationToken.IsCancellationRequested) return WindowsUserVerificationResult.Canceled;
            return IsSameUser(expectedUser, authenticated.User)
                ? WindowsUserVerificationResult.Verified : WindowsUserVerificationResult.Rejected;
        }
    }

    private static bool TryGetLocalAccount(string? accountName, string? machineName, out string username)
    {
        username = string.Empty;
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(machineName)
            || accountName.Any(char.IsControl)) return false;
        var separator = accountName.IndexOf('\\');
        if (separator <= 0 || separator == accountName.Length - 1
            || accountName.IndexOf('\\', separator + 1) >= 0) return false;
        var prefix = accountName[..separator];
        if (prefix != "." && !string.Equals(prefix, machineName, StringComparison.OrdinalIgnoreCase)) return false;
        var candidate = accountName[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        username = candidate;
        return true;
    }

    private static bool IsSameUser(SecurityIdentifier? expected, SecurityIdentifier? authenticated)
        => expected is not null && authenticated is not null && expected.Equals(authenticated);

    private static bool TrySplitAccount(string username, string domain, out string loginName, out string? loginDomain)
    {
        loginName = username;
        loginDomain = domain.Length == 0 ? null : domain;
        if (string.IsNullOrWhiteSpace(username) || username.StartsWith("@@", StringComparison.Ordinal)) return false;
        var separator = username.IndexOf('\\');
        if (separator >= 0)
        {
            if (separator == 0 || separator == username.Length - 1) return false;
            var prefix = username[..separator];
            if (domain.Length != 0 && !string.Equals(prefix, domain, StringComparison.OrdinalIgnoreCase)) return false;
            loginDomain = prefix;
            loginName = username[(separator + 1)..];
        }
        else if (username.Contains('@'))
        {
            // LogonUser requires a null domain for a UPN, even if a provider also supplied one.
            loginDomain = null;
        }
        else if (domain.Length == 0) loginDomain = ".";
        return true;
    }

    private static nint AllocateCharacters(uint characters)
    {
        var bytes = checked(characters * sizeof(char));
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        ZeroMemory(buffer, bytes);
        return buffer;
    }

    private static void ZeroMemory(nint buffer, uint bytes)
    {
        var cursor = buffer;
        for (var remaining = bytes; remaining > 0;)
        {
            var count = (int)Math.Min(remaining, (uint)ZeroBlock.Length);
            Marshal.Copy(ZeroBlock, 0, cursor, count);
            cursor = nint.Add(cursor, count);
            remaining -= (uint)count;
        }
    }

    private static void ClearAndFree(nint buffer, uint bytes, bool coTaskMemory)
    {
        if (buffer == 0) return;
        try { ZeroMemory(buffer, bytes); }
        finally
        {
            if (coTaskMemory) Marshal.FreeCoTaskMem(buffer);
            else Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialUiInfo
    {
        public int Size;
        public nint Parent;
        public string Message;
        public string Caption;
        public nint Banner;
    }

    [DllImport("credui.dll", EntryPoint = "CredUIPromptForWindowsCredentialsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CredUIPromptForWindowsCredentials(ref CredentialUiInfo info, uint authenticationError,
        ref uint authenticationPackage, nint input, uint inputBytes, out nint output, out uint outputBytes, nint save, uint flags);

    [DllImport("credui.dll", EntryPoint = "CredUIPromptForCredentialsW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CredUIPromptForCredentials(ref CredentialUiInfo info, string target, nint context,
        uint authenticationError, nint username, uint usernameCharacters, nint password, uint passwordCharacters,
        [MarshalAs(UnmanagedType.Bool)] ref bool save, uint flags);

    [DllImport("credui.dll", EntryPoint = "CredPackAuthenticationBufferW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredPackAuthenticationBuffer(uint flags, string username, string password, nint buffer, ref uint bytes);

    [DllImport("credui.dll", EntryPoint = "CredUnPackAuthenticationBufferW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredUnPackAuthenticationBuffer(uint flags, nint buffer, uint bytes,
        nint username, ref uint usernameCharacters, nint domain, ref uint domainCharacters, nint password, ref uint passwordCharacters);

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string? domain, nint password, int logonType, int provider, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
}
