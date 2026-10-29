using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;

namespace OhMySsh.Infrastructure.Persistence;

/// <summary>Stores explicitly trusted host identities separately from profiles and credentials.</summary>
public sealed class JsonKnownHostStore : IKnownHostStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate;

    public JsonKnownHostStore(string? filePath = null)
    {
        StoragePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMySsh", "known-hosts.json"));
        _gate = Gates.GetOrAdd(StoragePath, static _ => new SemaphoreSlim(1, 1));
    }

    public string StoragePath { get; }

    public async Task<SshHostIdentity?> GetAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hosts = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return hosts.SingleOrDefault(item => SameEndpoint(item, host, port));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(SshHostIdentity identity, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            // Recheck under the same gate as the write: another trust decision must not replace a key.
            var hosts = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var existing = hosts.SingleOrDefault(item => SameEndpoint(item, identity.Host, identity.Port));
            if (existing is not null)
            {
                if (!SameKey(existing, identity))
                {
                    throw new RemoteConnectionException(RemoteConnectionErrorCode.HostKeyChanged,
                        "The server host key has changed. Verify the new fingerprint with the server administrator before updating the trusted host file.");
                }

                return;
            }

            hosts.Add(identity with { Host = NormalizeHost(identity.Host) });
            var directory = Path.GetDirectoryName(StoragePath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(StoragePath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            }))
            {
                await JsonSerializer.SerializeAsync(stream, new HostDatabase(1, hosts), JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, StoragePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw StorageError();
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }

            _gate.Release();
        }
    }

    public static string NormalizeHost(string host) => host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();

    public static bool SameKey(SshHostIdentity first, SshHostIdentity second)
    {
        // The public key fingerprint, rather than the negotiated RSA signature name, identifies a key.
        return string.Equals(first.Sha256Fingerprint, second.Sha256Fingerprint, StringComparison.Ordinal);
    }

    private static bool SameEndpoint(SshHostIdentity identity, string host, int port)
        => identity.Port == port && string.Equals(NormalizeHost(identity.Host), NormalizeHost(host), StringComparison.Ordinal);

    private async Task<List<SshHostIdentity>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(StoragePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var database = await JsonSerializer.DeserializeAsync<HostDatabase>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (database is null || database.Version != 1 || database.Hosts is null)
            {
                throw StorageError();
            }

            var endpoints = new HashSet<(string, int)>();
            foreach (var identity in database.Hosts)
            {
                ValidateIdentity(identity);
                if (!endpoints.Add((NormalizeHost(identity.Host), identity.Port)))
                {
                    throw StorageError();
                }
            }

            return database.Hosts;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            throw StorageError();
        }
    }

    private static void ValidateIdentity(SshHostIdentity? identity)
    {
        if (identity is null || string.IsNullOrWhiteSpace(identity.Host) || identity.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(identity.KeyAlgorithm) || string.IsNullOrWhiteSpace(identity.Sha256Fingerprint)
            || !identity.Sha256Fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            throw StorageError();
        }

        var fingerprint = identity.Sha256Fingerprint[7..];
        try
        {
            var decoded = Convert.FromBase64String(fingerprint.PadRight((fingerprint.Length + 3) / 4 * 4, '='));
            if (decoded.Length != SHA256.HashSizeInBytes)
            {
                throw StorageError();
            }
        }
        catch (FormatException)
        {
            throw StorageError();
        }
    }

    private static RemoteConnectionException StorageError() => new(RemoteConnectionErrorCode.HostTrustStorageFailed,
        "Trusted server identities could not be read or saved. Check the known-hosts file and its permissions before connecting.");

    private sealed record HostDatabase(int Version, List<SshHostIdentity> Hosts);
}
