using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Terminal;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Edits a private draft; preview and cancel never change saved or active preferences.</summary>
public sealed class TerminalPreferencesViewModel : ObservableObject
{
    private readonly TerminalPreferencesService _service;
    private TerminalPreferences _draft;
    private TerminalPreferences _preview;
    private bool _updatingFields;
    private bool _valid = true;
    private bool _saving;
    private string? _errorKey;
    private string _palettePreset = "custom";

    public TerminalPreferencesViewModel(TerminalPreferencesService service)
    {
        _service = service;
        _draft = _preview = service.Preferences.Validate();
        ForegroundField = new("TerminalPreferences.Foreground", _draft.Foreground);
        BackgroundField = new("TerminalPreferences.Background", _draft.Background);
        CursorField = new("TerminalPreferences.CursorColor", _draft.CursorColor);
        SelectionField = new("TerminalPreferences.SelectionColor", _draft.SelectionColor);
        foreach (var field in new[] { ForegroundField, BackgroundField, CursorField, SelectionField })
            field.PropertyChanged += ColorFieldChanged;
        for (var index = 0; index < 16; index++)
        {
            var field = new TerminalColorField("TerminalPreferences.AnsiColor", _draft.AnsiColors[index], index);
            field.PropertyChanged += ColorFieldChanged;
            PaletteColors.Add(field);
        }
        FontFamilies = GetMonospaceFonts();
    }

    public IReadOnlyList<string> FontFamilies { get; }
    public ObservableCollection<TerminalColorField> PaletteColors { get; } = [];
    public TerminalColorField ForegroundField { get; }
    public TerminalColorField BackgroundField { get; }
    public TerminalColorField CursorField { get; }
    public TerminalColorField SelectionField { get; }
    public IReadOnlyList<TerminalPreferenceChoice> PalettePresets { get; } =
    [new("custom", "TerminalPreferences.Custom"), new("default", "TerminalPreferences.PresetDefault"),
        new("midnight", "TerminalPreferences.PresetMidnight"), new("light", "TerminalPreferences.PresetLight")];
    public IReadOnlyList<TerminalCursorChoice> CursorStyles { get; } =
    [new(TerminalCursorShape.Block, "TerminalPreferences.CursorBlock"), new(TerminalCursorShape.Underline, "TerminalPreferences.CursorUnderline"),
        new(TerminalCursorShape.Bar, "TerminalPreferences.CursorBar")];
    public IReadOnlyList<TerminalPreferenceNumberChoice> FontWeights { get; } =
    [new(400, "TerminalPreferences.WeightRegular"), new(500, "TerminalPreferences.WeightMedium"), new(700, "TerminalPreferences.WeightBold")];
    public TerminalPreferences PreviewPreferences => _preview;
    public FontFamily PreviewFontFamily => _preview.CreateFontFamily();
    public string FontFamily { get => _draft.FontFamily; set => Update(_draft with { FontFamily = value }); }
    public double FontSize { get => _draft.FontSize; set => Update(_draft with { FontSize = value }); }
    public int FontWeight { get => _draft.FontWeight; set => Update(_draft with { FontWeight = value }); }
    public bool Ligatures { get => _draft.Ligatures; set => Update(_draft with { Ligatures = value }); }
    public double LineHeight { get => _draft.LineHeight; set => Update(_draft with { LineHeight = value }); }
    public double Padding { get => _draft.Padding; set => Update(_draft with { Padding = value }); }
    public TerminalCursorShape CursorStyle { get => _draft.CursorStyle; set => Update(_draft with { CursorStyle = value }); }
    public bool CursorBlink { get => _draft.CursorBlink; set => Update(_draft with { CursorBlink = value }); }
    public bool BoldBrightColors { get => _draft.BoldBrightColors; set => Update(_draft with { BoldBrightColors = value }); }
    public int ScrollbackLines { get => _draft.ScrollbackLines; set => Update(_draft with { ScrollbackLines = value }); }
    public double SelectionOpacity { get => _draft.SelectionOpacity; set => Update(_draft with { SelectionOpacity = value }); }
    public string FontSizeLabel => UiText.Format("TerminalPreferences.FontSizeValue", FontSize);
    public string LineHeightLabel => UiText.Format("TerminalPreferences.LineHeightValue", LineHeight);
    public string PaddingLabel => UiText.Format("TerminalPreferences.PaddingValue", Padding);
    public string ScrollbackLabel => UiText.Format("TerminalPreferences.ScrollbackValue", ScrollbackLines);
    public string SelectionOpacityLabel => UiText.Format("TerminalPreferences.OpacityValue", SelectionOpacity);
    public string ErrorMessage => _errorKey is null ? string.Empty : UiText.Get(_errorKey);
    public bool HasError => _errorKey is not null;
    public bool IsSaving { get => _saving; private set { _saving = value; OnPropertyChanged(string.Empty); } }
    public bool CanSave => _valid && !IsSaving;
    public bool CanEdit => !IsSaving;
    public string PalettePreset
    {
        get => _palettePreset;
        set
        {
            if (_palettePreset == value) return;
            _palettePreset = value;
            if (value != "custom")
            {
                var palette = TerminalPreferences.Preset(value);
                _draft = _draft with { Foreground = palette.Foreground, Background = palette.Background,
                    CursorColor = palette.CursorColor, SelectionColor = palette.SelectionColor,
                    SelectionOpacity = palette.SelectionOpacity, AnsiColors = palette.AnsiColors };
                PopulateFields();
                ValidatePreview();
            }
            OnPropertyChanged(string.Empty);
        }
    }

    public void RestoreDefaults()
    {
        _draft = TerminalPreferences.Default;
        _palettePreset = "default";
        PopulateFields();
        ValidatePreview();
        OnPropertyChanged(string.Empty);
    }

    public async Task<bool> SaveAsync()
    {
        if (!CanSave) return false;
        IsSaving = true;
        try
        {
            await _service.SaveAsync(CaptureDraft());
            return true;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            _errorKey = "TerminalPreferences.SaveFailed";
            return false;
        }
        finally { IsSaving = false; }
    }

    private void Update(TerminalPreferences value)
    {
        _draft = value;
        ValidatePreview();
        OnPropertyChanged(string.Empty);
    }

    private void ColorFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingFields || e.PropertyName != nameof(TerminalColorField.Color)) return;
        _palettePreset = "custom";
        ValidatePreview();
        OnPropertyChanged(string.Empty);
    }

    private TerminalPreferences CaptureDraft() => (_draft with
    {
        Foreground = ForegroundField.Color, Background = BackgroundField.Color,
        CursorColor = CursorField.Color, SelectionColor = SelectionField.Color,
        AnsiColors = Array.AsReadOnly(PaletteColors.Select(field => field.Color).ToArray())
    }).Validate();

    private void ValidatePreview()
    {
        try
        {
            _preview = CaptureDraft();
            _valid = true;
            _errorKey = null;
        }
        catch (ArgumentException)
        {
            _valid = false;
            _errorKey = "TerminalPreferences.InvalidValue";
        }
        OnPropertyChanged(nameof(PreviewPreferences));
        OnPropertyChanged(nameof(PreviewFontFamily));
    }

    private void PopulateFields()
    {
        _updatingFields = true;
        try
        {
            ForegroundField.Color = _draft.Foreground; BackgroundField.Color = _draft.Background;
            CursorField.Color = _draft.CursorColor; SelectionField.Color = _draft.SelectionColor;
            for (var index = 0; index < 16; index++) PaletteColors[index].Color = _draft.AnsiColors[index];
        }
        finally { _updatingFields = false; }
    }

    private static IReadOnlyList<string> GetMonospaceFonts()
    {
        var families = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase) { "Fira Code", "Consolas", "Cascadia Mono", "Courier New" };
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                var typeface = new Typeface(family, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out var glyphs)
                    && glyphs.CharacterToGlyphMap.TryGetValue('i', out var narrow)
                    && glyphs.CharacterToGlyphMap.TryGetValue('W', out var wide)
                    && glyphs.CharacterToGlyphMap.TryGetValue('0', out var zero)
                    && Math.Abs(glyphs.AdvanceWidths[narrow] - glyphs.AdvanceWidths[wide]) < .001
                    && Math.Abs(glyphs.AdvanceWidths[wide] - glyphs.AdvanceWidths[zero]) < .001)
                    families.Add(family.Source);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.IOException) { }
        }
        return Array.AsReadOnly(families.ToArray());
    }
}

public sealed class TerminalColorField(string resourceKey, string color, int index = -1) : ObservableObject
{
    private string _color = color;
    public string Label => index >= 0 ? UiText.Format(resourceKey, index) : UiText.Get(resourceKey);
    public string Color { get => _color; set { if (SetProperty(ref _color, value)) OnPropertyChanged(nameof(Brush)); } }
    public SolidColorBrush Brush
    {
        get
        {
            try
            {
                var argb = TerminalPreferences.ToArgb(Color);
                return new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            }
            catch (ArgumentException) { return new SolidColorBrush(Colors.Transparent); }
        }
    }
}

public sealed class TerminalPreferenceChoice(string id, string resourceKey) : ObservableObject
{
    public string Id { get; } = id;
    public string Label => UiText.Get(resourceKey);
}

public sealed class TerminalPreferenceNumberChoice(int value, string resourceKey) : ObservableObject
{
    public int Value { get; } = value;
    public string Label => UiText.Get(resourceKey);
}

public sealed class TerminalCursorChoice(TerminalCursorShape value, string resourceKey) : ObservableObject
{
    public TerminalCursorShape Value { get; } = value;
    public string Label => UiText.Get(resourceKey);
}
