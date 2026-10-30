using System.Text.Json;
using System.Text.Json.Serialization;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;

namespace OhMySsh.Infrastructure.Persistence;

/// <summary>
/// Stores non-secret profiles in a user-local JSON file. Writes replace the
/// destination from a temporary file in the same directory to avoid partial saves.
/// </summary>
public sealed class JsonConnectionProfileStore : IConnectionProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private const int MaximumReplacementAttempts = 5;
    private const int InitialReplacementDelayMilliseconds = 40;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonConnectionProfileStore(string? filePath = null)
    {
        StoragePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OhMySsh",
            "connections.json"));
    }

    public string StoragePath { get; }

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileStream stream;
            try
            {
                stream = new FileStream(StoragePath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
            }
            catch (FileNotFoundException)
            {
                return Array.Empty<ConnectionProfile>();
            }
            catch (DirectoryNotFoundException)
            {
                return Array.Empty<ConnectionProfile>();
            }

            await using (stream)
            {
                var profiles = await JsonSerializer.DeserializeAsync<List<ConnectionProfile>>(
                    stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The connection profile file is empty or has an invalid format.");

                ValidateProfiles(profiles);
                return profiles.ToArray();
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The connection profile file could not be read. Check that it contains valid JSON.", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var snapshot = profiles.ToArray();
        ValidateProfiles(snapshot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                await JsonSerializer.SerializeAsync(stream, snapshot, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ReplaceFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Cleanup must not hide an earlier save error.
                }
                catch (UnauthorizedAccessException)
                {
                    // Leave an inaccessible temporary file for the user to inspect.
                }
            }

            _gate.Release();
        }
    }

    private async Task ReplaceFileAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Keep the same staged snapshot and atomic replacement throughout contention.
                File.Move(temporaryPath, StoragePath, overwrite: true);
                return;
            }
            catch (Exception exception) when (IsRetryableReplacementFailure(exception))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= MaximumReplacementAttempts - 1 || IsNonReplaceableDestination()) throw;
                // Windows can deny replacement briefly while another process holds the file.
                // Real permission failures still propagate once this small retry budget expires.
                await Task.Delay(InitialReplacementDelayMilliseconds << attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryableReplacementFailure(Exception exception)
        => OperatingSystem.IsWindows() && exception is (IOException or UnauthorizedAccessException)
           && (exception.HResult & 0xffff) is (5 or 32 or 33);

    private bool IsNonReplaceableDestination()
    {
        try
        {
            return (File.GetAttributes(StoragePath) & (FileAttributes.ReadOnly | FileAttributes.Directory)) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Do not retry when even the destination's basic attributes cannot be inspected.
            return true;
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter<ConnectionProtocol>(allowIntegerValues: false));
        return options;
    }

    private static void ValidateProfiles(IEnumerable<ConnectionProfile> profiles)
    {
        var ids = new HashSet<Guid>();
        foreach (var profile in profiles)
        {
            if (profile is null || profile.Id == Guid.Empty || !ids.Add(profile.Id))
            {
                throw new InvalidDataException("Connection profiles must have valid, unique identifiers.");
            }

            if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Host)
                || string.IsNullOrWhiteSpace(profile.Username) || string.IsNullOrWhiteSpace(profile.Group))
            {
                throw new InvalidDataException("A connection profile has a blank name, host, username, or group.");
            }

            if (profile.Port is < 1 or > 65535 || !Enum.IsDefined(profile.Protocol)
                || profile.ConnectionTimeoutSeconds is < 1 or > 300)
            {
                throw new InvalidDataException("A connection profile has an invalid port, protocol, or connection timeout.");
            }
        }
    }
}
