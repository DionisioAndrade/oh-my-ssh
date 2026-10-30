using System.Globalization;
using System.Text.Json.Serialization;

namespace OhMySsh.Desktop.Terminal;

/// <summary>Appearance and bounded history preferences; never contains session or terminal data.</summary>
public sealed record TerminalPreferences
{
    public const double MinimumFontSize = 8;
    public const double MaximumFontSize = 32;
    public const double DefaultFontSize = 13;
    public static TerminalPreferences Default => new();

    public string FontFamily { get; init; } = "Fira Code";
    public double FontSize { get; init; } = DefaultFontSize;
    public int FontWeight { get; init; } = 400;
    public bool Ligatures { get; init; } = true;
    public double LineHeight { get; init; } = 1.38;
    public double Padding { get; init; } = 10;
    public string Foreground { get; init; } = "#D1D5DF";
    public string Background { get; init; } = "#202128";
    public string CursorColor { get; init; } = "#D1D5DF";
    public string SelectionColor { get; init; } = "#6E78B8";
    public double SelectionOpacity { get; init; } = .55;
    public IReadOnlyList<string> AnsiColors { get; init; } = Array.AsReadOnly(new[]
    {
        "#202128", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#ABB2BF",
        "#5C6370", "#FF7A85", "#B4E88D", "#FFD68A", "#80C7FF", "#DE9AF5", "#7CDDE8", "#F2F3F7"
    });
    public TerminalCursorShape CursorStyle { get; init; } = TerminalCursorShape.Block;
    public bool CursorBlink { get; init; } = true;
    public bool BoldBrightColors { get; init; } = true;
    public int ScrollbackLines { get; init; } = 2000;

    [JsonIgnore]
    public string FontFamilySource => FontFamily.Equals("Fira Code", StringComparison.OrdinalIgnoreCase)
        ? "pack://application:,,,/OhMySsh;component/Assets/Fonts/#Fira Code" : FontFamily;

    // WPF requires a base URI plus a relative font reference; an absolute pack string
    // silently selects fallback fonts instead of the bundled Fira Code family.
    public System.Windows.Media.FontFamily CreateFontFamily() => FontFamily.Equals("Fira Code", StringComparison.OrdinalIgnoreCase)
        ? new System.Windows.Media.FontFamily(new Uri("pack://application:,,,/"), "/OhMySsh;component/Assets/Fonts/#Fira Code")
        : new System.Windows.Media.FontFamily(FontFamily);

    /// <summary>Validates all fields and owns an immutable copy of palette entries.</summary>
    public TerminalPreferences Validate()
    {
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 128 || FontFamily.Any(char.IsControl)
            || FontFamily.IndexOfAny(['/', '\\', ':', '#', ';']) >= 0 || FontWeight is not (400 or 500 or 700)
            || !double.IsFinite(FontSize) || FontSize is < MinimumFontSize or > MaximumFontSize
            || !double.IsFinite(LineHeight) || LineHeight is < 1 or > 2
            || !double.IsFinite(Padding) || Padding is < 0 or > 24
            || !double.IsFinite(SelectionOpacity) || SelectionOpacity is < .1 or > .9
            || ScrollbackLines is < 500 or > 10000 || !Enum.IsDefined(CursorStyle)
            || AnsiColors is null || AnsiColors.Count != 16)
            throw new ArgumentException("The terminal preferences contain an invalid value.");

        return this with
        {
            FontFamily = FontFamily.Trim(), Foreground = NormalizeColor(Foreground), Background = NormalizeColor(Background),
            CursorColor = NormalizeColor(CursorColor), SelectionColor = NormalizeColor(SelectionColor),
            AnsiColors = Array.AsReadOnly(AnsiColors.Select(NormalizeColor).ToArray())
        };
    }

    public static string NormalizeColor(string color)
    {
        if (color is null || color.Length != 7 || color[0] != '#'
            || !uint.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("Terminal colors must use the #RRGGBB format.");
        return color.ToUpperInvariant();
    }

    public static uint ToArgb(string color, double opacity = 1)
    {
        var rgb = uint.Parse(NormalizeColor(color).AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        var alpha = (uint)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return alpha << 24 | rgb;
    }

    public static TerminalPreferences Preset(string id) => id switch
    {
        "light" => Default with
        {
            Background = "#FAFAFC", Foreground = "#273142", CursorColor = "#273142", SelectionColor = "#82AAEA", SelectionOpacity = .45,
            AnsiColors = Array.AsReadOnly(new[] { "#273142", "#B4233C", "#287D3C", "#896500", "#215DA8", "#8141A9", "#187583", "#697487",
                "#747D8C", "#C7374B", "#358C49", "#9E7500", "#376FBD", "#9656BD", "#228A98", "#FAFAFC" })
        },
        "midnight" => Default with
        {
            Background = "#0F172A", Foreground = "#DFE7F4", CursorColor = "#93C5FD", SelectionColor = "#3B82F6",
            AnsiColors = Array.AsReadOnly(new[] { "#0F172A", "#F87171", "#86EFAC", "#FDE68A", "#93C5FD", "#C4B5FD", "#67E8F9", "#CBD5E1",
                "#64748B", "#FCA5A5", "#BBF7D0", "#FEF3C7", "#BFDBFE", "#DDD6FE", "#A5F3FC", "#F8FAFC" })
        },
        _ => Default
    };
}
