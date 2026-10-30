using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OhMySsh.Core.Localization;

/// <summary>Provides the shared, live interface catalog. Diagnostic text can explicitly use English.</summary>
public sealed class UiText : INotifyPropertyChanged
{
    private static readonly string[] SupportedCodes = ["en-US", "pt-BR", "es-ES"];
    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WeakReference<IUiLanguageObserver>> _observers = [];
    private readonly ConditionalWeakTable<IUiLanguageObserver, object> _registeredObservers = new();
    private readonly object _observerGate = new();
    private int _registrationsSincePrune;
    private readonly List<string> _catalogErrors = [];
    private CultureInfo _culture = CultureInfo.GetCultureInfo("pt-BR");

    private UiText()
    {
        foreach (var code in SupportedCodes)
        {
            _catalogs[code] = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var assembly = typeof(UiText).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.Contains(".Localization.Catalogs.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var code = SupportedCodes.FirstOrDefault(candidate => resource.EndsWith($".{candidate}.json", StringComparison.OrdinalIgnoreCase));
            if (code is null)
            {
                _catalogErrors.Add($"Unsupported catalog resource: {resource}.");
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var document = JsonDocument.Parse(stream);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Name))
                    {
                        _catalogErrors.Add($"Invalid catalog entry in {resource}: {property.Name}.");
                    }
                    else if (!_catalogs[code].TryAdd(property.Name, property.Value.GetString()!))
                    {
                        _catalogErrors.Add($"Duplicate catalog key in {code}: {property.Name}.");
                    }
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException)
            {
                _catalogErrors.Add($"Invalid catalog resource {resource}: {exception.GetType().Name}.");
            }
        }

        foreach (var code in SupportedCodes)
        {
            foreach (var key in _catalogs["en-US"].Keys.Except(_catalogs[code].Keys, StringComparer.Ordinal))
            {
                _catalogErrors.Add($"Missing catalog key in {code}: {key}.");
            }
        }
    }

    public static UiText Current { get; } = new();

    public static IReadOnlyList<string> SupportedCultureNames { get; } = Array.AsReadOnly(SupportedCodes);

    /// <summary>Catalog defects are reported for verification while production can still use English fallbacks.</summary>
    public IReadOnlyList<string> CatalogErrors => _catalogErrors.AsReadOnly();

    public string CultureName => _culture.Name;

    public CultureInfo Culture => _culture;

    public string this[string key] => Resolve(CultureName, key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public static string Get(string key) => Current[key];

    public static string Format(string key, params object?[] arguments) =>
        string.Format(Current.Culture, Current[key], arguments);

    public static string English(string key, params object?[] arguments) =>
        string.Format(CultureInfo.GetCultureInfo("en-US"), Current.Resolve("en-US", key), arguments);

    public static bool IsSupportedCulture(string? code) =>
        code is not null && SupportedCodes.Contains(code, StringComparer.OrdinalIgnoreCase);

    public void SetCulture(string code)
    {
        var canonicalCode = SupportedCodes.FirstOrDefault(candidate => candidate.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentOutOfRangeException(nameof(code), code, "Unsupported interface language.");
        var culture = CultureInfo.GetCultureInfo(canonicalCode);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (_culture.Name == canonicalCode)
        {
            return;
        }

        _culture = culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CultureName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        WeakReference<IUiLanguageObserver>[] observers;
        lock (_observerGate)
        {
            _observers.RemoveAll(reference => !reference.TryGetTarget(out _));
            observers = _observers.ToArray();
        }

        foreach (var reference in observers)
        {
            if (reference.TryGetTarget(out var observer))
            {
                observer.OnLanguageChanged();
            }
        }

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RegisterObserver(IUiLanguageObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_observerGate)
        {
            if (_registeredObservers.TryGetValue(observer, out _))
            {
                return;
            }

            _registeredObservers.Add(observer, new object());
            _observers.Add(new WeakReference<IUiLanguageObserver>(observer));
            if (++_registrationsSincePrune >= 128)
            {
                _observers.RemoveAll(reference => !reference.TryGetTarget(out _));
                _registrationsSincePrune = 0;
            }
        }
    }

    private string Resolve(string code, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _catalogs[code].GetValueOrDefault(key)
            ?? _catalogs["en-US"].GetValueOrDefault(key)
            ?? key;
    }
}
