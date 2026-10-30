using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using OhMySsh.Desktop.Terminal;

namespace OhMySsh.Desktop.Services;

/// <summary>Atomically saves terminal appearance separately from credentials, profiles, and language.</summary>
public sealed class TerminalPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<TerminalCursorShape>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _pendingGate = new();
    private TerminalPreferences _preferences = TerminalPreferences.Default;
    private Dispatcher? _dispatcher;
    private string? _path;
    private Task _pending = Task.CompletedTask;

    public TerminalPreferencesService(string? path = null)
    {
        if (path is not null) Initialize(path);
    }

    public static TerminalPreferencesService Current { get; } = new();
    public TerminalPreferences Preferences => Volatile.Read(ref _preferences);
    public event EventHandler? Changed;

    public void Initialize(string? path = null)
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OhMySsh", "terminal-settings.json"));
        var preferences = TerminalPreferences.Default;
        try
        {
            var file = new FileInfo(_path);
            if (file.Exists && file.Length <= 64 * 1024)
            {
                using var stream = File.OpenRead(_path);
                preferences = (JsonSerializer.Deserialize<TerminalPreferences>(stream, JsonOptions) ?? preferences).Validate();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // Appearance cannot prevent startup; an explicit later save can replace corrupt preferences.
        }
        Volatile.Write(ref _preferences, preferences);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SaveAsync(TerminalPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return UpdateAsync(_ => preferences);
    }

    /// <summary>Reads the latest committed settings under the save gate, so zoom cannot overwrite a newer palette.</summary>
    public Task UpdateAsync(Func<TerminalPreferences, TerminalPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_pendingGate)
        {
            _pending = SaveAfterAsync(_pending, update);
            return _pending;
        }
    }

    public Task FlushAsync() { lock (_pendingGate) return _pending; }

    private async Task SaveAfterAsync(Task previous, Func<TerminalPreferences, TerminalPreferences> update)
    {
        var predecessor = previous;
        previous = Task.CompletedTask;
        try { await predecessor.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* The original caller observes its own save failure. */ }
        predecessor = Task.CompletedTask;
        await SaveCoreAsync(update).ConfigureAwait(false);
    }

    private async Task SaveCoreAsync(Func<TerminalPreferences, TerminalPreferences> update)
    {
        var path = _path ?? throw new InvalidOperationException("Initialize terminal preferences before saving them.");
        await _saveGate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        TerminalPreferences? saved = null;
        try
        {
            var preferences = update(Preferences).Validate();
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            temporary = null;
            Volatile.Write(ref _preferences, preferences);
            saved = preferences;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            _saveGate.Release();
        }
        if (saved is not null)
        {
            if (_dispatcher is null || _dispatcher.CheckAccess()) Changed?.Invoke(this, EventArgs.Empty);
            else await _dispatcher.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty));
        }
    }
}
