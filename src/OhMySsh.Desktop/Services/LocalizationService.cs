using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using OhMySsh.Core.Localization;

namespace OhMySsh.Desktop.Services;

/// <summary>Applies the interface language and stores its preference separately from connection data.</summary>
public sealed class LocalizationService
{
    private const string DefaultCultureName = "pt-BR";
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private Application? _application;
    private string? _settingsPath;

    private LocalizationService()
    {
    }

    public static LocalizationService Current { get; } = new();

    public void Initialize(Application app, string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Initialize(app, settingsPath));
            return;
        }

        if (_application is not null && !ReferenceEquals(_application, app))
        {
            throw new InvalidOperationException("The localization service is already associated with another application.");
        }

        _application = app;
        _settingsPath = Path.GetFullPath(settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMySsh", "settings.json"));
        UiText.Current.SetCulture(ReadCulture(_settingsPath));
    }

    /// <summary>Changes existing controls immediately and then atomically saves the language preference.</summary>
    public Task SetLanguageAsync(string code)
    {
        if (!UiText.IsSupportedCulture(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported interface language.");
        }

        var app = _application ?? throw new InvalidOperationException("Initialize localization before changing the interface language.");
        if (!app.Dispatcher.CheckAccess())
        {
            return app.Dispatcher.Invoke(() => SetLanguageAsync(code));
        }

        UiText.Current.SetCulture(code);
        return SaveLanguageAsync(UiText.Current.CultureName, _settingsPath!);
    }

    private static string ReadCulture(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                var settings = JsonSerializer.Deserialize<LanguageSettings>(stream);
                if (UiText.IsSupportedCulture(settings?.Language))
                {
                    return settings!.Language!;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A missing or unreadable preference must not prevent startup.
        }

        return DefaultCultureName;
    }

    private async Task SaveLanguageAsync(string code, string path)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new LanguageSettings { Language = code },
                    new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original save failure if cleanup is also denied.
                }
            }

            _saveGate.Release();
        }
    }

    private sealed class LanguageSettings
    {
        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }
}
