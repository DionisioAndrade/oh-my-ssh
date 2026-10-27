using System.Globalization;

namespace OhMySsh.Core.Models;

/// <summary>One file-system item, independent of the desktop UI or transport.</summary>
public sealed record FileEntry
{
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("pt-BR");

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }

    public string Extension => IsDirectory ? string.Empty : Path.GetExtension(Name);

    public string SizeLabel
    {
        get
        {
            if (IsDirectory)
            {
                return "—";
            }

            string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
            double value = Math.Max(0, Size);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return string.Concat(value.ToString(unit == 0 ? "N0" : "N1", DisplayCulture), " ", units[unit]);
        }
    }

    public string ModifiedLabel => LastModified == default
        ? "—"
        : LastModified.ToString("dd/MM/yyyy HH:mm", DisplayCulture);

    public string TypeLabel => IsDirectory
        ? "Pasta"
        : string.IsNullOrEmpty(Extension)
            ? "Arquivo"
            : $"Arquivo {Extension.TrimStart('.').ToUpperInvariant()}";

    /// <summary>A folder or document glyph for the Segoe Fluent Icons font.</summary>
    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";
}
