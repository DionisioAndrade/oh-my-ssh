using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Core.Models;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private Task VerifyDevelopmentFileClassificationAsync()
    {
        (string Name, FileDevelopmentKind Kind)[] cases =
        [
            ("app.py", FileDevelopmentKind.Python), ("types.PYI", FileDevelopmentKind.Python),
            ("NOTEBOOK.IPYNB", FileDevelopmentKind.Python),
            ("Application.JAVA", FileDevelopmentKind.Java), ("App.cs", FileDevelopmentKind.CSharp),
            ("script.CSX", FileDevelopmentKind.CSharp), ("README.md", FileDevelopmentKind.Markdown),
            ("notes.MARKDOWN", FileDevelopmentKind.Markdown), ("index.jsx", FileDevelopmentKind.JavaScript),
            ("index.mjs", FileDevelopmentKind.JavaScript), ("app.tsx", FileDevelopmentKind.TypeScript),
            ("app.cts", FileDevelopmentKind.TypeScript), ("index.HTML", FileDevelopmentKind.Html),
            ("theme.scss", FileDevelopmentKind.Css), ("settings.jsonc", FileDevelopmentKind.Json),
            ("document.xml", FileDevelopmentKind.Xml), ("view.xaml", FileDevelopmentKind.Xml),
            ("deploy.YML", FileDevelopmentKind.Yaml), ("start.sh", FileDevelopmentKind.Shell),
            ("deploy.ps1", FileDevelopmentKind.PowerShell), ("queries.sql", FileDevelopmentKind.Sql),
            ("main.go", FileDevelopmentKind.Go), ("main.rs", FileDevelopmentKind.Rust),
            ("index.php", FileDevelopmentKind.Php), ("app.rb", FileDevelopmentKind.Ruby),
            ("main.c", FileDevelopmentKind.C), ("main.cpp", FileDevelopmentKind.CPlusPlus),
            ("main.HPP", FileDevelopmentKind.CPlusPlus), ("app.CSPROJ", FileDevelopmentKind.DotNetProject),
            ("workspace.sln", FileDevelopmentKind.DotNetProject), ("Dockerfile", FileDevelopmentKind.Docker),
            ("DOCKERFILE.production", FileDevelopmentKind.Docker), ("Containerfile", FileDevelopmentKind.Docker),
            (".dockerignore", FileDevelopmentKind.Docker), ("compose.yml", FileDevelopmentKind.Docker),
            (".bashrc", FileDevelopmentKind.Shell), ("Gemfile", FileDevelopmentKind.Ruby),
            ("README", FileDevelopmentKind.None), ("unknown", FileDevelopmentKind.None),
            ("photo.png", FileDevelopmentKind.None), ("archive.tar.gz", FileDevelopmentKind.None),
            ("settings.ini", FileDevelopmentKind.None), ("data.unknown", FileDevelopmentKind.None),
            ("movie.mts", FileDevelopmentKind.None), ("Application.class", FileDevelopmentKind.None),
            ("source.py.unknown", FileDevelopmentKind.None), (string.Empty, FileDevelopmentKind.None),
            (@"literal\SCRIPT.PY", FileDevelopmentKind.Python), (@"literal\Dockerfile", FileDevelopmentKind.None)
        ];
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            Require(FileDevelopmentKindClassifier.Classify(null) == FileDevelopmentKind.None,
                "A missing filename acquired a development-language icon.");
            foreach (var culture in new[] { "en-US", "pt-BR", "es-ES", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                foreach (var (name, expected) in cases)
                {
                    var entry = new FileEntry { Name = name, FullPath = @"Z:\unavailable\conflicting.java" };
                    Require(FileDevelopmentKindClassifier.Classify(name) == expected && entry.DevelopmentKind == expected,
                        $"The development icon for '{name}' was not {expected} in culture {culture}.");
                    Require(FileDevelopmentKindClassifier.Classify(name, isDirectory: true) == FileDevelopmentKind.None
                        && (entry with { IsDirectory = true }).DevelopmentKind == FileDevelopmentKind.None,
                        $"The directory '{name}' acquired a development-language icon in culture {culture}.");
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }

        var python = new FileEntry { Name = "script.py", FullPath = "/qa/script.py", Size = 512 };
        var equalCopy = python with { };
        var javaCopy = python with { Name = "Application.java" };
        var hash = python.GetHashCode();
        var notifications = 0;
        python.PropertyChanged += (_, _) => notifications++;
        equalCopy.OnLanguageChanged();
        Require(python == equalCopy && python.GetHashCode() == hash && notifications == 0
            && python.DevelopmentKind == FileDevelopmentKind.Python
            && javaCopy.DevelopmentKind == FileDevelopmentKind.Java && javaCopy.Kind == FileKind.Code,
            "Development classification changed record equality, copied notification state, or cached a renamed file's icon.");
        return Task.CompletedTask;
    }

    private async Task VerifyDevelopmentFileIconsAsync()
    {
        const string localPath = @"C:\QA\development-icons";
        Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "LICENSE.MaterialIconTheme.txt")),
            "The bundled development icon license was omitted from the verification output.");
        var originalCulture = UiText.Current.CultureName;
        var profile = CreateProfile() with { Name = "Development icons QA", Username = "test-user", PrivateKeyPath = null };
        var localEntries = CreateDevelopmentIconEntries(localPath, local: true);
        var remoteEntries = CreateDevelopmentIconEntries("/home/qa", local: false);
        remoteEntries = remoteEntries.Skip(12).Concat(remoteEntries.Take(12)).ToArray();
        var files = new QaRemoteFiles { ListHandler = (_, _) => Task.FromResult<IReadOnlyList<FileEntry>>(remoteEntries) };
        var connections = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var store = new MemoryProfileStore([profile]);
        var vm = new MainViewModel(store, new QaFileIconLocalFiles(localPath, localEntries), new RecordingInteractions(), connections);
        await vm.InitializeAsync();
        await vm.NavigateToAsync(localPath);
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        var window = new MainWindow(vm, initializeOnLoad: false) { Width = 1180, Height = 800 };
        var presentations = new Dictionary<(AppTheme Theme, FileDevelopmentKind Kind), string>();
        var developmentKinds = Enum.GetValues<FileDevelopmentKind>().Where(kind => kind != FileDevelopmentKind.None).ToArray();
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            var localGrid = (DataGrid)window.FindName("LocalFilesGrid");
            var remoteGrid = (DataGrid)window.FindName("RemoteFilesGrid");
            var fallbackStyle = application.TryFindResource("SharedFileIconStyle") as Style
                ?? throw new InvalidOperationException("The shared fallback file icon style was not available.");
            var selectedLocal = localEntries[0];
            var selectedRemote = remoteEntries.Single(entry => entry.DevelopmentKind == FileDevelopmentKind.Java);
            localGrid.SelectedItem = selectedLocal;
            remoteGrid.SelectedItem = selectedRemote;
            await SettleLayoutAsync(window);
            Require(localEntries.Select(entry => entry.DevelopmentKind).Where(kind => kind != FileDevelopmentKind.None)
                .ToHashSet().SetEquals(developmentKinds), "The native development icon fixture omitted a language or tool.");
            foreach (var culture in UiText.SupportedCultureNames)
            {
                UiText.Current.SetCulture(culture);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.Current.Apply(theme);
                    await SettleLayoutAsync(window);
                    Require(window.ActualWidth == window.MinWidth && window.ActualHeight == window.MinHeight,
                        "The development icon capture was not at minimum window size.");
                    foreach (var (grid, entries) in new[] { (localGrid, localEntries), (remoteGrid, remoteEntries) })
                    {
                        Require(grid.Items.Count == entries.Length, $"The {grid.Name} development listing lost files.");
                        RequireVisibleWithinWindow(window, grid, $"development icons in {grid.Name} ({culture}, {theme})");
                        foreach (var entry in entries)
                        {
                            if (entry.DevelopmentKind == FileDevelopmentKind.None)
                            {
                                await GetNativeFileIconAsync(window, grid, entry, fallbackStyle);
                                continue;
                            }
                            var presenter = await GetFileIconPresenterAsync(window, grid, entry);
                            var icons = VisualDescendants<Image>(presenter).Where(icon => icon.IsVisible).ToArray();
                            Require(icons.Length == 1 && icons[0].Source is DrawingImage,
                                $"The {entry.DevelopmentKind} file did not display exactly one bundled vector icon.");
                            var image = icons[0];
                            Require(ReferenceEquals(image.Source, application.TryFindResource($"Development{entry.DevelopmentKind}Icon")),
                                $"The {entry.DevelopmentKind} image did not resolve its language resource from the current {theme} palette.");
                            RequireVisibleWithinWindow(window, image, $"{grid.Name} {entry.DevelopmentKind} vector");
                            Require(image.ActualWidth >= 14 && image.ActualHeight >= 14 && image.Opacity > 0,
                                $"The {entry.DevelopmentKind} vector became squeezed or transparent.");
                            Require(!VisualDescendants<TextBlock>(presenter).Any(block => block.IsVisible && ReferenceEquals(block.Style, fallbackStyle)),
                                $"The {entry.DevelopmentKind} file displayed its generic glyph over its language icon.");
                            Require(Equals(presenter.ToolTip, entry.TypeLabel) && AutomationProperties.GetName(presenter) == entry.TypeLabel,
                                $"The {entry.DevelopmentKind} icon lost its localized tooltip or accessibility name in {culture}.");
                            var fingerprint = GetDevelopmentIconFingerprint(image, entry.DevelopmentKind);
                            var key = (theme, entry.DevelopmentKind);
                            if (presentations.TryGetValue(key, out var expected))
                                Require(fingerprint == expected,
                                    $"The {entry.DevelopmentKind} vector changed between languages or local and remote files in {theme}.");
                            else presentations[key] = fingerprint;
                        }
                    }
                    // The first viewport keeps the four specifically requested languages together.
                    foreach (var (suffix, index) in new[] { ("requested", 0), ("systems", 19) })
                    {
                        localGrid.ScrollIntoView(localEntries[index]);
                        remoteGrid.ScrollIntoView(remoteEntries[index]);
                        await SettleLayoutAsync(window);
                        Capture(window, $"development-icons-{culture}-{theme.ToString().ToLowerInvariant()}-{suffix}.png");
                    }
                }
            }
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                Require(developmentKinds.Select(kind => presentations[(theme, kind)]).Distinct().Count() == developmentKinds.Length,
                    $"Development languages and tools did not have visually distinct vector images in {theme}.");
            ThemeService.Current.Apply(AppTheme.Light);
            await SettleLayoutAsync(window);
            var livePresenter = await GetFileIconPresenterAsync(window, localGrid, localEntries[0]);
            var liveImage = VisualDescendants<Image>(livePresenter).Single(image => image.IsVisible);
            var lightFingerprint = GetDevelopmentIconFingerprint(liveImage, FileDevelopmentKind.Python);
            ThemeService.Current.Apply(AppTheme.Dark);
            await SettleLayoutAsync(window);
            // Changing WPF's native theme may recreate inner template visuals. Inspect the already
            // visible row without scrolling, navigating, refreshing its listing, or replacing the window.
            var darkPresenter = FindVisualChild<FrameworkElement>(localGrid, element => element.IsVisible
                && element.Name == "FileTypeIcon" && ReferenceEquals(element.DataContext, localEntries[0]))
                ?? throw new InvalidOperationException("The visible Python row disappeared during the live theme change.");
            var darkImage = VisualDescendants<Image>(darkPresenter).Single(image => image.IsVisible);
            var darkFingerprint = GetDevelopmentIconFingerprint(darkImage, FileDevelopmentKind.Python);
            Require(lightFingerprint != darkFingerprint && darkFingerprint == presentations[(AppTheme.Dark, FileDevelopmentKind.Python)]
                && ReferenceEquals(darkImage.Source, application.TryFindResource("DevelopmentPythonIcon")),
                "The existing native Python row did not redraw its theme colors after a live theme change.");
            Require(store.SaveCalls == 0 && vm.HasActiveSession && vm.LocalPath == localPath && vm.RemotePath == "/home/qa"
                && vm.LocalEntries.SequenceEqual(localEntries) && vm.RemoteEntries.SequenceEqual(remoteEntries)
                && ReferenceEquals(vm.SelectedLocalEntry, selectedLocal) && ReferenceEquals(vm.SelectedRemoteEntry, selectedRemote),
                "Changing development icon themes or languages changed profiles, listings, paths, selections, or the session.");
        }
        finally
        {
            await vm.ShutdownAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            UiText.Current.SetCulture(originalCulture);
        }
    }

    private static FileEntry[] CreateDevelopmentIconEntries(string path, bool local)
    {
        string[] names =
        [
            "00 script.py", "01 Application.java", "02 Application.cs", "03 README.md",
            "04 index.js", "05 app.ts", "06 index.html", "07 theme.css", "08 settings.json",
            "09 document.xml", "10 deploy.yaml", "11 start.sh", "12 deploy.ps1", "13 queries.sql",
            "14 main.go", "15 main.rs", "16 index.php", "17 app.rb", "18 main.c", "19 main.cpp",
            "20 app.csproj", "21 service.dockerfile", "22 folder.py", "23 unknown.data"
        ];
        return names.Select((name, index) => new FileEntry
        {
            Name = name, FullPath = path + (local ? "\\" : "/") + name, IsDirectory = index == 22,
            Size = index == 22 ? 0 : 1536, LastModified = new DateTime(2026, 10, 30, 12, 30, 0)
        }).ToArray();
    }

    private static async Task<FrameworkElement> GetFileIconPresenterAsync(Window window, DataGrid grid, FileEntry entry)
    {
        grid.ScrollIntoView(entry);
        await SettleLayoutAsync(window);
        var row = grid.ItemContainerGenerator.ContainerFromItem(entry) as DataGridRow
            ?? throw new InvalidOperationException($"The native {grid.Name} row for {entry.Name} was not realized.");
        var presenter = FindVisualChild<FrameworkElement>(row, element => element.Name == "FileTypeIcon")
            ?? throw new InvalidOperationException($"The {grid.Name} row for {entry.Name} did not use the shared file icon presenter.");
        var cell = FindVisualAncestor<DataGridCell>(presenter)
            ?? throw new InvalidOperationException("The file icon presenter was detached from its native file cell.");
        var bounds = presenter.TransformToAncestor(cell).TransformBounds(new Rect(presenter.RenderSize));
        Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= cell.ActualWidth + 1 && bounds.Bottom <= cell.ActualHeight + 1,
            $"The {grid.Name} {entry.DevelopmentKind} icon presenter crossed its file cell bounds.");
        return presenter;
    }

    private static string GetDevelopmentIconFingerprint(Image image, FileDevelopmentKind kind)
    {
        var width = (int)Math.Ceiling(image.ActualWidth);
        var height = (int)Math.Ceiling(image.ActualHeight);
        Require(width > 0 && height > 0, $"The native {kind} image did not receive a layout size.");
        // Render the actual retained Image, including its WPF drawing cache and invalidation behavior.
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(image);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Require(Enumerable.Range(0, width * height).Count(index => pixels[index * 4 + 3] > 0) > 16,
            $"The bundled {kind} vector rendered as an empty or nearly empty image.");
        return Convert.ToHexString(SHA256.HashData(pixels));
    }
}
