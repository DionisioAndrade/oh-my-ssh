using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OhMySsh.Core.Models;
using OhMySsh.Infrastructure.Persistence;

namespace OhMySsh.Infrastructure.Connections;

/// <summary>Stores opt-in credentials in the current Windows user's native credential vault.</summary>
public sealed class WindowsCredentialStore(string? targetPrefix = null)
{
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumBlobBytes = 2560;
    private readonly string _targetPrefix = targetPrefix ?? "OhMySsh/SSH";

    public Task<RemoteConnectionCredentials?> LoadAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!CredRead(GetTarget(profile), GenericCredential, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == ErrorNotFound)
            {
                return Task.FromResult<RemoteConnectionCredentials?>(null);
            }

            throw StorageError();
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize > MaximumBlobBytes || credential.CredentialBlobSize % 2 != 0)
            {
                throw StorageError();
            }

            var payloadText = Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
            var payload = JsonSerializer.Deserialize<CredentialPayload>(payloadText ?? string.Empty);
            if (payload is null || payload.Version != 1 || !Enum.IsDefined(payload.Method) || payload.Secret is null)
            {
                throw StorageError();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RemoteConnectionCredentials?>(new(payload.Method, payload.Secret, remember: true));
        }
        catch (JsonException)
        {
            throw StorageError();
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public Task SaveAsync(ConnectionProfile profile, RemoteConnectionCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!credentials.Remember)
        {
            return Task.CompletedTask;
        }

        var bytes = Encoding.Unicode.GetBytes(JsonSerializer.Serialize(new CredentialPayload(1, credentials.Method, credentials.Secret)));
        if (bytes.Length > MaximumBlobBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw StorageError();
        }

        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var native = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = GetTarget(profile),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = buffer,
                Persist = PersistLocalMachine,
                UserName = profile.Username,
                Comment = "oh-my-ssh saved SSH authentication"
            };
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredWrite(ref native, 0))
            {
                throw StorageError();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.FreeHGlobal(buffer);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!CredDelete(GetTarget(profile), GenericCredential, 0) && Marshal.GetLastWin32Error() != ErrorNotFound)
        {
            throw StorageError();
        }

        return Task.CompletedTask;
    }

    private string GetTarget(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // A saved profile edited to another host/user/key must never reuse credentials from its old endpoint.
        var keyPath = string.IsNullOrWhiteSpace(profile.PrivateKeyPath) ? string.Empty : Path.GetFullPath(profile.PrivateKeyPath).ToUpperInvariant();
        var scope = JsonSerializer.Serialize(new
        {
            profile.Id,
            Host = JsonKnownHostStore.NormalizeHost(profile.Host),
            profile.Port,
            profile.Username,
            profile.Protocol,
            Authentication = keyPath.Length == 0 ? "password" : "private-key",
            PrivateKeyPath = keyPath
        });
        var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        return $"{_targetPrefix}/{profile.Id:N}/{scopeHash}";
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is available only on Windows.");
        }
    }

    private static RemoteConnectionException StorageError() => new(RemoteConnectionErrorCode.CredentialStorageFailed,
        "Windows Credential Manager could not read or save these credentials. Check your Windows credential storage and try again.");

    private sealed record CredentialPayload(int Version, RemoteAuthenticationMethod Method, string Secret);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
