using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Core.Models;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private Task VerifyFileKindClassificationAsync()
    {
        (string Name, FileKind Kind)[] cases =
        [
            ("app.cs", FileKind.Code), ("MODULE.TS", FileKind.Code), ("deploy.py", FileKind.Code),
            ("deploy.ps1", FileKind.Code), ("start.sh", FileKind.Code), ("Dockerfile", FileKind.Code),
            ("MAKEFILE", FileKind.Code), ("settings.json", FileKind.Configuration),
            ("settings.INI", FileKind.Configuration), ("ID_ED25519", FileKind.Key),
            ("service.YAML", FileKind.Configuration), ("app.config", FileKind.Configuration),
            (".env", FileKind.Configuration), (".ENV.production", FileKind.Configuration),
            (".gitignore", FileKind.Configuration), ("notes.txt", FileKind.Text),
            ("report.docx", FileKind.Text), ("budget.XLSX", FileKind.Spreadsheet),
            ("records.csv", FileKind.Spreadsheet), ("slides.pptx", FileKind.Presentation),
            ("README.md", FileKind.Text), ("README", FileKind.Text), ("LICENSE", FileKind.Text),
            ("events.LOG", FileKind.Text), ("guide.PDF", FileKind.Pdf),
            ("release.zip", FileKind.Archive), ("release.tar.gz", FileKind.Archive),
            ("release.TAR.BZ2", FileKind.Archive), ("photo.PNG", FileKind.Image),
            ("photo.TIFF", FileKind.Image),
            ("vector.svg", FileKind.Image), ("sound.flac", FileKind.Audio),
            ("clip.MP4", FileKind.Video), ("setup.EXE", FileKind.Executable),
            ("cache.sqlite", FileKind.Database), ("interface.woff2", FileKind.Font),
            ("certificate.PEM", FileKind.Key), ("id_ed25519", FileKind.Key),
            ("id_rsa.pub", FileKind.Key), ("unknown", FileKind.Generic),
            ("data.unrecognized", FileKind.Generic), (".unknown", FileKind.Generic),
            ("trailing.", FileKind.Generic), (string.Empty, FileKind.Generic),
            ("archive.tar.gz.txt", FileKind.Text), ("settings.json.unknown", FileKind.Generic),
            (@"literal\name.PY", FileKind.Code), (@"literal\README", FileKind.Generic),
            (@"literal\name.TAR.GZ", FileKind.Archive), ("İMAGE.PNG", FileKind.Image)
        ];
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        Require(FileKindClassifier.Classify(null) == FileKind.Generic
            && FileKindClassifier.Classify(null, isDirectory: true) == FileKind.Folder,
            "A missing filename did not retain the generic or directory fallback.");
        try
        {
            foreach (var culture in new[] { "en-US", "pt-BR", "es-ES", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                foreach (var (name, expected) in cases)
                {
                    // A conflicting, nonexistent path must not influence filename classification.
                    var entry = new FileEntry { Name = name, FullPath = @"Z:\unavailable\different.exe" };
                    Require(FileKindClassifier.Classify(name) == expected && entry.Kind == expected,
                        $"File kind for '{name}' was not {expected} in culture {culture}.");
                    Require(FileKindClassifier.Classify(name, isDirectory: true) == FileKind.Folder
                        && (entry with { IsDirectory = true }).Kind == FileKind.Folder,
                        $"A directory named '{name}' lost its folder icon in culture {culture}.");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        var original = new FileEntry { Name = "release.tar.gz", FullPath = "/qa/release.tar.gz", Size = 1024 };
        var equalCopy = original with { };
        var renamedCopy = original with { Name = "release.py" };
        var hash = original.GetHashCode();
        var originalNotifications = 0;
        var copyNotifications = 0;
        original.PropertyChanged += (_, _) => originalNotifications++;
        equalCopy.PropertyChanged += (_, _) => copyNotifications++;
        equalCopy.OnLanguageChanged();
        Require(original == equalCopy && original.GetHashCode() == hash
            && original.Kind == FileKind.Archive && renamedCopy.Kind == FileKind.Code
            && originalNotifications == 0 && copyNotifications == 1,
            "File icon classification changed record equality, copy behavior, or independent notification state.");
        original.OnLanguageChanged();
        Require(originalNotifications == 1 && copyNotifications == 1 && original == equalCopy,
            "File language notifications leaked between record copies or changed their equality.");
        return Task.CompletedTask;
    }

    private async Task VerifyNativeFileIconsAsync()
    {
        const string localPath = @"C:\QA\file-icons";
        var originalCulture = UiText.Current.CultureName;
        var profile = CreateProfile() with { Name = "File icons QA", Username = "test-user", PrivateKeyPath = null };
        var localEntries = CreateFileIconEntries(localPath, local: true);
        var remoteEntries = CreateFileIconEntries("/home/qa", local: false);
        // Complementary viewports show different categories side by side in native captures.
        remoteEntries = remoteEntries.Skip(7).Concat(remoteEntries.Take(7)).ToArray();
        var files = new QaRemoteFiles { ListHandler = (_, _) => Task.FromResult<IReadOnlyList<FileEntry>>(remoteEntries) };
        var remoteConnections = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var store = new MemoryProfileStore([profile]);
        var vm = new MainViewModel(store, new QaFileIconLocalFiles(localPath, localEntries),
            new RecordingInteractions(), remoteConnections);
        await vm.InitializeAsync();
        await vm.NavigateToAsync(localPath);
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        var window = new MainWindow(vm, initializeOnLoad: false) { Width = 1180, Height = 800 };
        var presentations = new Dictionary<(AppTheme Theme, FileKind Kind), (string Glyph, Color Color)>();
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            var localGrid = (DataGrid)window.FindName("LocalFilesGrid");
            var remoteGrid = (DataGrid)window.FindName("RemoteFilesGrid");
            var iconStyle = application.TryFindResource("SharedFileIconStyle") as Style
                ?? throw new InvalidOperationException("The shared file icon style was not available.");
            var selectedLocal = localEntries.Single(entry => entry.Kind == FileKind.Code);
            var selectedRemote = remoteEntries.Single(entry => entry.Kind == FileKind.Code);
            localGrid.SelectedItem = selectedLocal;
            remoteGrid.SelectedItem = selectedRemote;
            await SettleLayoutAsync(window);
            Require(localGrid.Items.Count == localEntries.Length && remoteGrid.Items.Count == remoteEntries.Length,
                "The native file grids did not retain every icon fixture.");
            Require(localEntries.Select(entry => entry.Kind).ToHashSet().SetEquals(Enum.GetValues<FileKind>()),
                "The native icon fixture did not cover every file category.");
            foreach (var culture in UiText.SupportedCultureNames)
            {
                UiText.Current.SetCulture(culture);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.Current.Apply(theme);
                    await SettleLayoutAsync(window);
                    Require(window.ActualWidth == window.MinWidth && window.ActualHeight == window.MinHeight,
                        "The file icon capture was not at the minimum native window size.");
                    foreach (var (grid, entries) in new[] { (localGrid, localEntries), (remoteGrid, remoteEntries) })
                    {
                        RequireVisibleWithinWindow(window, grid, $"{grid.Name} ({culture}, {theme})");
                        foreach (var entry in entries)
                        {
                            var icon = await GetNativeFileIconAsync(window, grid, entry, iconStyle);
                            RequireNativeGlyph(icon, entry.Kind);
                            Require(Equals(icon.ToolTip, entry.TypeLabel),
                                $"The {entry.Kind} icon tooltip did not follow the localized file type in {culture}.");
                            var foreground = icon.Foreground as SolidColorBrush
                                ?? throw new InvalidOperationException($"The {entry.Kind} icon did not use a solid themed brush.");
                            var categoryBrush = application.TryFindResource($"FileIcon{entry.Kind}Brush") as SolidColorBrush
                                ?? throw new InvalidOperationException($"The palette omitted the {entry.Kind} icon brush.");
                            Require(foreground.Color.A > 0 && icon.Opacity > 0,
                                $"The {entry.Kind} icon became transparent in {culture}, {theme}.");
                            Require(foreground.Color == categoryBrush.Color,
                                $"The {entry.Kind} icon did not use its category palette brush in {culture}, {theme}.");
                            var presentation = (icon.Text, foreground.Color);
                            var key = (theme, entry.Kind);
                            if (presentations.TryGetValue(key, out var expected))
                                Require(presentation == expected,
                                    $"The {entry.Kind} icon differed between file grids or languages in {theme}.");
                            else presentations[key] = presentation;
                        }
                    }
                    foreach (var (suffix, index) in new[] { ("first", 0), ("last", localEntries.Length - 1) })
                    {
                        localGrid.ScrollIntoView(localEntries[index]);
                        remoteGrid.ScrollIntoView(remoteEntries[index]);
                        await SettleLayoutAsync(window);
                        Capture(window, $"file-icons-{culture}-{theme.ToString().ToLowerInvariant()}-{suffix}.png");
                    }
                }
            }
            foreach (var kind in Enum.GetValues<FileKind>())
            {
                var light = presentations[(AppTheme.Light, kind)];
                var dark = presentations[(AppTheme.Dark, kind)];
                Require(light.Glyph == dark.Glyph && light.Color != dark.Color,
                    $"The existing {kind} presentation did not keep its glyph and update its color between themes.");
            }
            Require(presentations.Where(pair => pair.Key.Theme == AppTheme.Light).Select(pair => pair.Value.Glyph).Distinct().Count()
                == Enum.GetValues<FileKind>().Length, "File categories did not have distinct icon silhouettes.");
            Require(store.SaveCalls == 0 && vm.HasActiveSession
                && vm.LocalPath == localPath && vm.RemotePath == "/home/qa"
                && vm.LocalEntries.SequenceEqual(localEntries) && vm.RemoteEntries.SequenceEqual(remoteEntries)
                && ReferenceEquals(vm.SelectedLocalEntry, selectedLocal) && ReferenceEquals(vm.SelectedRemoteEntry, selectedRemote),
                "Changing native icon themes or languages changed profiles, paths, listings, selections, or session state.");

            // Hold the exact realized visuals across a theme switch to catch stale StaticResource colors.
            ThemeService.Current.Apply(AppTheme.Light);
            await SettleLayoutAsync(window);
            var liveLocal = await GetNativeFileIconAsync(window, localGrid, localEntries[1], iconStyle);
            var liveRemote = await GetNativeFileIconAsync(window, remoteGrid,
                remoteEntries.Single(entry => entry.Kind == FileKind.Code), iconStyle);
            var beforeLocal = ((SolidColorBrush)liveLocal.Foreground).Color;
            var beforeRemote = ((SolidColorBrush)liveRemote.Foreground).Color;
            ThemeService.Current.Apply(AppTheme.Dark);
            await SettleLayoutAsync(window);
            Require(((SolidColorBrush)liveLocal.Foreground).Color != beforeLocal
                && ((SolidColorBrush)liveRemote.Foreground).Color != beforeRemote
                && liveLocal.Text == liveRemote.Text,
                "Existing local and remote icon visuals did not react to a live theme change.");
        }
        finally
        {
            await vm.ShutdownAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            UiText.Current.SetCulture(originalCulture);
        }
    }

    private static FileEntry[] CreateFileIconEntries(string path, bool local)
    {
        (string Name, bool Directory)[] names =
        [
            ("00 folder.zip", true), ("01 source.ex", false), ("02 settings.ini", false),
            ("03 notes.txt", false), ("04 guide.pdf", false), ("05 release.tar.gz", false),
            ("06 sample.png", false), ("07 sound.flac", false), ("08 clip.mp4", false),
            ("09 setup.exe", false), ("10 cache.sqlite", false), ("11 interface.woff2", false),
            ("12 fixture.key", false), ("13 budget.xlsx", false), ("14 slides.pptx", false),
            ("15 data.unknown", false)
        ];
        return names.Select(item => new FileEntry
        {
            Name = item.Name,
            FullPath = path + (local ? "\\" : "/") + item.Name,
            IsDirectory = item.Directory,
            Size = item.Directory ? 0 : 2048,
            LastModified = new DateTime(2026, 10, 30, 12, 30, 0)
        }).ToArray();
    }

    private static async Task<TextBlock> GetNativeFileIconAsync(Window window, DataGrid grid, FileEntry entry, Style iconStyle)
    {
        grid.ScrollIntoView(entry);
        await SettleLayoutAsync(window);
        var row = grid.ItemContainerGenerator.ContainerFromItem(entry) as DataGridRow
            ?? throw new InvalidOperationException($"The native {grid.Name} row for {entry.Name} was not realized.");
        var presenter = FindVisualChild<FrameworkElement>(row, element => element.Name == "FileTypeIcon")
            ?? throw new InvalidOperationException($"The {grid.Name} row for {entry.Name} did not use the shared icon presenter.");
        var icon = FindVisualChild<TextBlock>(presenter, block => block.IsVisible && ReferenceEquals(block.Style, iconStyle))
            ?? throw new InvalidOperationException($"The {grid.Name} row for {entry.Name} did not use the shared icon style.");
        Require(!VisualDescendants<Image>(presenter).Any(image => image.IsVisible),
            $"The {entry.Kind} fallback glyph displayed an unrelated development icon.");
        RequireVisibleWithinWindow(window, icon, $"{grid.Name} {entry.Kind} icon");
        var cell = FindVisualAncestor<DataGridCell>(icon)
            ?? throw new InvalidOperationException("The file icon was detached from its native file cell.");
        var bounds = icon.TransformToAncestor(cell).TransformBounds(new Rect(icon.RenderSize));
        Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= cell.ActualWidth + 1
            && bounds.Bottom <= cell.ActualHeight + 1 && icon.ActualWidth >= 14 && icon.ActualHeight >= 14,
            $"The {grid.Name} {entry.Kind} icon was clipped or squeezed within its file cell.");
        return icon;
    }

    private static void RequireNativeGlyph(TextBlock icon, FileKind kind)
    {
        var codepoints = icon.Text.EnumerateRunes().Select(rune => rune.Value).ToArray();
        Require(codepoints.Length == 1, $"The {kind} icon did not resolve to one native glyph.");
        var supported = icon.FontFamily.Source.Split(',').Select(name => name.Trim()).Any(name =>
        {
            var face = new Typeface(new FontFamily(name), icon.FontStyle, icon.FontWeight, icon.FontStretch);
            return face.TryGetGlyphTypeface(out var glyphs)
                && glyphs.CharacterToGlyphMap.TryGetValue(codepoints[0], out var glyphIndex) && glyphIndex != 0;
        });
        Require(supported, $"The native icon font did not contain the {kind} codepoint U+{codepoints[0]:X4}.");
    }
}

/// <summary>Supplies only synthetic listing data; it never reads a local filesystem path.</summary>
internal sealed class QaFileIconLocalFiles(string path, IReadOnlyList<FileEntry> entries) : ILocalFileService
{
    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string requestedPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FileEntry>>(requestedPath.Equals(path, StringComparison.OrdinalIgnoreCase) ? entries : []);
    }
}
