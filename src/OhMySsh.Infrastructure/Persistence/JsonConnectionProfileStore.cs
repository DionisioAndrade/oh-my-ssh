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
                    ?? throw new InvalidDataException("O arquivo de conexões está vazio ou possui formato inválido.");

                ValidateProfiles(profiles);
                return profiles.ToArray();
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Não foi possível ler o arquivo de conexões. Verifique se o JSON é válido.", exception);
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
            File.Move(temporaryPath, StoragePath, overwrite: true);
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
                throw new InvalidDataException("As conexões devem possuir identificadores únicos e válidos.");
            }

            if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Host)
                || string.IsNullOrWhiteSpace(profile.Username) || string.IsNullOrWhiteSpace(profile.Group))
            {
                throw new InvalidDataException("Uma conexão possui nome, servidor, usuário ou grupo em branco.");
            }

            if (profile.Port is < 1 or > 65535 || !Enum.IsDefined(profile.Protocol))
            {
                throw new InvalidDataException("Uma conexão possui porta ou protocolo inválido.");
            }
        }
    }
}
