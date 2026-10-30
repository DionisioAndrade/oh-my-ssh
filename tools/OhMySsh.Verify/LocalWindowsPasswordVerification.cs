using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using OhMySsh.Desktop.Services;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private Task VerifyLocalWindowsPasswordAsync()
    {
        var type = typeof(WindowsUserVerificationService);
        var localAccount = type.GetMethod("TryGetLocalAccount", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The local Windows account routing contract could not be verified.");
        foreach (var (account, machine, expected) in new (string?, string?, string?)[]
        {
            (@"QA-DESKTOP\test-user", "QA-DESKTOP", "test-user"),
            (@"qa-desktop\QA User", "QA-DESKTOP", "QA User"),
            (@".\test-usér", "QA-DESKTOP", "test-usér"),
            (@"CORP\test-user", "QA-DESKTOP", null),
            (@"QA-DESKTOP-OTHER\test-user", "QA-DESKTOP", null),
            (@"MicrosoftAccount\qa@example.invalid", "QA-DESKTOP", null),
            (@"AzureAD\test-user", "QA-DESKTOP", null),
            ("test-user@example.invalid", "QA-DESKTOP", null),
            ("test-user", "QA-DESKTOP", null),
            (null, "QA-DESKTOP", null), (string.Empty, "QA-DESKTOP", null),
            (@"QA-DESKTOP\", "QA-DESKTOP", null), (@"\test-user", "QA-DESKTOP", null),
            (@"QA-DESKTOP\test-user\other", "QA-DESKTOP", null), (@".\test-user\other", "QA-DESKTOP", null),
            (@"QA-DESKTOP\   ", "QA-DESKTOP", null),
            ("QA-DESKTOP\\test-user\r\n", "QA-DESKTOP", null), ("QA-DESKTOP\\test\0user", "QA-DESKTOP", null),
            (@"QA-DESKTOP\test-user", null, null), (@"QA-DESKTOP\test-user", string.Empty, null),
            (@".\test-user", " ", null)
        })
        {
            object?[] arguments = [account, machine, null];
            var accepted = (bool)localAccount.Invoke(null, arguments)!;
            Require(accepted == (expected is not null) && (string?)arguments[2] == (expected ?? string.Empty),
                "Local Windows account routing accepted a nonlocal or malformed identity, or changed the captured username.");
        }

        VerifyModernWindowsAccountParsing(type);
        VerifyLocalPasswordNativeContract(type);
        VerifyProductionPasswordBufferClearing(type);
        VerifyLocalPasswordSourceGuards();
        return Task.CompletedTask;
    }

    private static void VerifyModernWindowsAccountParsing(Type type)
    {
        var split = type.GetMethod("TrySplitAccount", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The existing nonlocal Windows account parser could not be verified.");
        foreach (var (username, domain, expectedName, expectedDomain) in new (string, string, string?, string?)[]
        {
            ("user", string.Empty, "user", "."), ("user", "CORP", "user", "CORP"),
            (@"CORP\user", string.Empty, "user", "CORP"), (@"CORP\user", "corp", "user", "CORP"),
            ("user@example.invalid", "CORP", "user@example.invalid", null),
            (string.Empty, "CORP", null, null), (" ", string.Empty, null, null),
            ("@@opaque-provider-identity", string.Empty, null, null),
            (@"\user", string.Empty, null, null), (@"CORP\", string.Empty, null, null),
            (@"CORP\user", "OTHER", null, null)
        })
        {
            object?[] arguments = [username, domain, null, null];
            var accepted = (bool)split.Invoke(null, arguments)!;
            Require(accepted == (expectedName is not null)
                && (!accepted || (string?)arguments[2] == expectedName && (string?)arguments[3] == expectedDomain),
                "The local Windows route changed existing domain, UPN, or provider-identity parsing rules.");
        }
    }

    private static void VerifyLocalPasswordNativeContract(Type type)
    {
        var prompt = type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).SingleOrDefault(method =>
            method.GetCustomAttribute<DllImportAttribute>()?.EntryPoint == "CredUIPromptForCredentialsW")
            ?? throw new InvalidOperationException("The local Windows password prompt import is missing.");
        var import = prompt.GetCustomAttribute<DllImportAttribute>()!;
        var parameters = prompt.GetParameters();
        Require(import.CharSet == CharSet.Unicode && import.ExactSpelling && parameters.Length == 10
            && parameters[4].ParameterType == typeof(nint) && parameters[5].ParameterType == typeof(uint)
            && parameters[6].ParameterType == typeof(nint) && parameters[7].ParameterType == typeof(uint)
            && parameters[8].ParameterType == typeof(bool).MakeByRefType()
            && parameters[8].GetCustomAttribute<MarshalAsAttribute>()?.Value == UnmanagedType.Bool,
            "The local password prompt changed its bounded unmanaged Unicode buffers or Win32 save-flag contract.");
        var flags = (uint)type.GetField("LocalPasswordPromptFlags", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Require(flags == 0x140082u,
            "The local password prompt must keep the account fixed, always show fresh input, and prohibit persistence without administrator or save-checkbox flags.");
        Require((int)type.GetField("NetworkLogon", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()! == 3,
            "Password verification no longer uses a real network logon to prove the supplied password.");
        var library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "credui.dll");
        Require(NativeLibrary.TryLoad(library, out var handle), "The Windows credential UI library could not be loaded.");
        try { Require(NativeLibrary.TryGetExport(handle, import.EntryPoint!, out _), "The local Windows password prompt export is unavailable."); }
        finally { NativeLibrary.Free(handle); }
    }

    private static void VerifyProductionPasswordBufferClearing(Type type)
    {
        var allocate = type.GetMethod("AllocateCharacters", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clear = type.GetMethod("ZeroMemory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var release = type.GetMethod("ClearAndFree", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var characters in new uint[] { 257, 514, 4097 })
        {
            var bytes = checked(characters * sizeof(char));
            var buffer = (nint)allocate.Invoke(null, [characters])!;
            try
            {
                var snapshot = new byte[bytes];
                Marshal.Copy(buffer, snapshot, 0, snapshot.Length);
                Require(snapshot.All(value => value == 0), "A new native password buffer contained uncleared memory.");
                var fixture = Enumerable.Repeat((byte)0x51, snapshot.Length).ToArray();
                Marshal.Copy(fixture, 0, buffer, fixture.Length);
                clear.Invoke(null, [buffer, bytes]);
                Marshal.Copy(buffer, snapshot, 0, snapshot.Length);
                Require(snapshot.All(value => value == 0), "Production cleanup did not clear the entire synthetic native password buffer.");
            }
            finally { release.Invoke(null, [buffer, bytes, false]); }
        }
    }

    private void VerifyLocalPasswordSourceGuards()
    {
        var repository = new DirectoryInfo(outputDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        Require(repository is not null, "The verifier could not locate the native password source contracts.");
        var source = File.ReadAllText(Path.Combine(repository!.FullName, "src", "OhMySsh.Desktop", "Services", "WindowsUserVerificationService.cs"));
        var route = PasswordMethodSource(source, "VerifyPassword");
        var local = PasswordMethodSource(source, "VerifyLocalPassword");
        var authentication = PasswordMethodSource(source, "AuthenticatePassword");
        var prompt = local.IndexOf("CredUIPromptForCredentials(", StringComparison.Ordinal);
        Require(route.IndexOf("TryGetLocalAccount(", StringComparison.Ordinal) < route.IndexOf("CredPackAuthenticationBuffer(", StringComparison.Ordinal)
            && route.Contains("return VerifyLocalPassword(ownerWindow, purpose, expectedUser, localUsername, cancellationToken)", StringComparison.Ordinal),
            "A local process identity is not routed directly to its captured local-account password prompt.");
        Require(prompt >= 0 && local.IndexOf("cancellationToken.IsCancellationRequested", StringComparison.Ordinal) < prompt
            && local.LastIndexOf("cancellationToken.IsCancellationRequested", StringComparison.Ordinal) > prompt
            && local.Contains("result == UserCanceled", StringComparison.Ordinal)
            && local.Contains("if (result != 0) return WindowsUserVerificationResult.Unavailable", StringComparison.Ordinal)
            && local.Contains("if (Marshal.ReadInt16(password) == 0) return WindowsUserVerificationResult.Rejected", StringComparison.Ordinal),
            "The local native prompt no longer rejects canceled, failed, late, or empty-password results before authentication.");
        Require(local.Contains("ref save, LocalPasswordPromptFlags", StringComparison.Ordinal)
            && local.Contains("TryGetLocalAccount(returnedAccount, Environment.MachineName, out var returnedUsername)", StringComparison.Ordinal)
            && local.Contains("string.Equals(localUsername, returnedUsername, StringComparison.OrdinalIgnoreCase)", StringComparison.Ordinal)
            && local.Contains("AuthenticatePassword(expectedUser, localUsername, \".\", password, cancellationToken)", StringComparison.Ordinal),
            "The prompt result can redirect authentication away from the captured local Windows account.");
        Require(!local.Contains("PtrToStringUni(password)", StringComparison.Ordinal)
            && local.Contains("finally", StringComparison.Ordinal)
            && local.Contains("ClearAndFree(username, LocalUsernameCharacters * sizeof(char), coTaskMemory: false)", StringComparison.Ordinal)
            && local.Contains("ClearAndFree(password, LocalPasswordCharacters * sizeof(char), coTaskMemory: false)", StringComparison.Ordinal),
            "The local Windows password can escape unmanaged memory or miss unconditional buffer cleanup.");
        Require(authentication.Contains("LogonUser(loginName, loginDomain, password, NetworkLogon, DefaultProvider, out var token)", StringComparison.Ordinal)
            && authentication.Contains("IsSameUser(expectedUser, authenticated.User)", StringComparison.Ordinal)
            && authentication.Contains("token.IsInvalid || token.IsClosed", StringComparison.Ordinal)
            && authentication.LastIndexOf("cancellationToken.IsCancellationRequested", StringComparison.Ordinal) > authentication.IndexOf("LogonUser(", StringComparison.Ordinal),
            "The shared password proof no longer requires a valid token for the exact process identity or discards late cancellation.");
    }

    private static string PasswordMethodSource(string source, string method)
    {
        var declaration = Regex.Match(source, @"private\s+static\s+WindowsUserVerificationResult\s+" + method + @"\s*\(");
        Require(declaration.Success, $"The password verification method {method} could not be audited.");
        var start = source.IndexOf('{', declaration.Index);
        var depth = 0;
        for (var index = start; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            if (source[index] == '}' && --depth == 0) return Regex.Replace(source[start..(index + 1)], @"\s+", " ");
        }

        throw new InvalidOperationException($"The password verification method {method} was not bounded.");
    }
}
