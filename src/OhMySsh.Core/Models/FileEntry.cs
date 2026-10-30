using System.ComponentModel;
using System.Runtime.CompilerServices;
using OhMySsh.Core.Localization;

namespace OhMySsh.Core.Models;

/// <summary>One file-system item, independent of the desktop UI or transport.</summary>
public sealed record FileEntry : INotifyPropertyChanged, IUiLanguageObserver
{
    // Notification state lives outside record fields so subscribing never changes value equality.
    private static readonly ConditionalWeakTable<FileEntry, NotificationState> Notifications = new();

    public FileEntry() => UiText.Current.RegisterObserver(this);

    private FileEntry(FileEntry original)
    {
        Name = original.Name;
        FullPath = original.FullPath;
        IsDirectory = original.IsDirectory;
        Size = original.Size;
        LastModified = original.LastModified;
        UiText.Current.RegisterObserver(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => Notifications.GetValue(this, static _ => new NotificationState()).Handlers += value;
        remove { if (Notifications.TryGetValue(this, out var state)) state.Handlers -= value; }
    }

    public void OnLanguageChanged()
    {
        if (Notifications.TryGetValue(this, out var state))
            state.Handlers?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastModified { get; init; }

    public string Extension
    {
        get
        {
            if (IsDirectory) return string.Empty;
            // Name is one component, including when a POSIX server uses a literal backslash.
            var separator = Name.LastIndexOf('.');
            return separator < 0 || separator == Name.Length - 1 ? string.Empty : Name[separator..];
        }
    }

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

            return string.Concat(value.ToString(unit == 0 ? "N0" : "N1", UiText.Current.Culture), " ", units[unit]);
        }
    }

    public string ModifiedLabel => LastModified == default
        ? "—"
        : LastModified.ToString("g", UiText.Current.Culture);

    public string TypeLabel => IsDirectory
        ? UiText.Get("Vm.File.Folder")
        : string.IsNullOrEmpty(Extension)
            ? UiText.Get("Vm.File.File")
            : UiText.Format("Vm.File.Extension", Extension.TrimStart('.').ToUpperInvariant());

    /// <summary>A folder or document glyph for the Segoe Fluent Icons font.</summary>
    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";

    private sealed class NotificationState
    {
        public PropertyChangedEventHandler? Handlers;
    }
}
