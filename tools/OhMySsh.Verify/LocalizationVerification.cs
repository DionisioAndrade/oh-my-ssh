using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Core.Models;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private Task VerifyLocalizationCatalogsAsync()
    {
        Require(UiText.Current.CatalogErrors.Count == 0, string.Join(Environment.NewLine, UiText.Current.CatalogErrors));
        var assembly = typeof(UiText).Assembly;
        var catalogs = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var code in UiText.SupportedCultureNames)
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var resources = assembly.GetManifestResourceNames()
                .Where(name => name.Contains(".Localization.Catalogs.", StringComparison.Ordinal) && name.EndsWith($".{code}.json", StringComparison.Ordinal)).ToArray();
            Require(resources.Length >= 4, $"Language {code} is missing a feature catalog.");
            foreach (var resource in resources)
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var document = JsonDocument.Parse(stream);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    Require(property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()),
                        $"Catalog {resource} has an empty or invalid value for {property.Name}.");
                    Require(entries.TryAdd(property.Name, property.Value.GetString()!), $"Duplicate catalog key {property.Name} in {code}.");
                }
            }

            catalogs.Add(code, entries);
        }

        var english = catalogs["en-US"];
        var failureType = typeof(MainViewModel).Assembly.GetType("OhMySsh.Desktop.ViewModels.TransferFailureDetails")!;
        foreach (var field in failureType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(field => field.FieldType == failureType))
        {
            var kind = failureType.GetProperty("Key")!.GetValue(field.GetValue(null)) as string;
            foreach (var part in new[] { "Summary", "Reason", "Action" })
                Require(english.ContainsKey($"Vm.TransferFailure.{kind}.{part}"), $"Transfer failure {kind} is missing its {part} translation.");
        }
        foreach (var (code, entries) in catalogs)
        {
            Require(english.Keys.Order().SequenceEqual(entries.Keys.Order()), $"Catalog key sets differ for {code}.");
            foreach (var (key, text) in entries)
            {
                var format = CompositeFormat.Parse(text);
                Require(PlaceholderIndices(text).SequenceEqual(PlaceholderIndices(english[key])), $"Format arguments differ for {code}:{key}.");
                _ = string.Format(CultureInfo.GetCultureInfo(code), format, Enumerable.Repeat<object>(1234.5m, format.MinimumArgumentCount).ToArray());
                if (key.Contains("Tray", StringComparison.OrdinalIgnoreCase) && key.Contains("Tooltip", StringComparison.OrdinalIgnoreCase))
                    Require(text.Length <= 63, $"Tray tooltip exceeds the native limit for {code}.");
            }
        }

        var repository = new DirectoryInfo(outputDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "Directory.Build.props"))) repository = repository.Parent;
        Require(repository is not null, "The source tree could not be located for the localization audit.");
        var sourceRoot = Path.Combine(repository!.FullName, "src");
        var references = 0;
        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".xaml", StringComparison.Ordinal))
                && !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(path);
            foreach (Match reference in Regex.Matches(source, "(?:UiText\\.(?:Get|Format|English)\\(\\s*\"|\\{[A-Za-z][A-Za-z0-9]*:Loc\\s+)(?<key>[A-Za-z][A-Za-z0-9_.]+)"))
            {
                var key = reference.Groups["key"].Value;
                Require(english.ContainsKey(key), $"Source {Path.GetFileName(path)} references missing localization key {key}.");
                references++;
            }
        }

        Require(references > 150, "The localization source audit did not cover the application surfaces.");
        return Task.CompletedTask;
    }

    private static IEnumerable<int> PlaceholderIndices(string text) =>
        Regex.Matches(text, @"(?<!\{)\{(\d+)(?:[^{}]*)\}")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order();

    private async Task VerifyLanguagePreferencesAsync()
    {
        var directory = Path.Combine(outputDirectory, "language-preference-cases");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            LocalizationService.Current.Initialize(application, path);
            Require(UiText.Current.CultureName == "pt-BR" && !File.Exists(path), "Missing preferences did not use the safe default without writing.");
            foreach (var code in UiText.SupportedCultureNames)
            {
                var change = LocalizationService.Current.SetLanguageAsync(code);
                Require(UiText.Current.CultureName == code, "Changing language was deferred until persistence completed.");
                await change;
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                Require(document.RootElement.EnumerateObject().Count() == 1 && document.RootElement.GetProperty("language").GetString() == code,
                    "The preference did not contain only the selected interface language.");
                LocalizationService.Current.Initialize(application, path);
                Require(UiText.Current.CultureName == code, "The language did not survive preference reload.");
            }

            foreach (var invalid in new[] { "{", "[]", "null", "{}", "{\"language\":\"pt-PT\"}", "{\"language\":123}", "{\"language\":null}" })
            {
                await File.WriteAllTextAsync(path, invalid);
                LocalizationService.Current.Initialize(application, path);
                Require(UiText.Current.CultureName == "pt-BR" && await File.ReadAllTextAsync(path) == invalid,
                    "An invalid preference prevented fallback or was overwritten during startup.");
            }

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                LocalizationService.Current.Initialize(application, path);
                Require(UiText.Current.CultureName == "pt-BR", "An unreadable preference prevented startup fallback.");
                await ExpectExceptionAsync<IOException>(() => LocalizationService.Current.SetLanguageAsync("en-US"));
                Require(UiText.Current.CultureName == "en-US", "A preference save failure prevented the requested live language change.");
            }

            await LocalizationService.Current.SetLanguageAsync("pt-BR");
            Require(Directory.GetFiles(directory, "*.tmp").Length == 0, "A failed preference replacement left a temporary file.");
            await ExpectExceptionAsync<ArgumentOutOfRangeException>(() => LocalizationService.Current.SetLanguageAsync("fr-FR"));
            var dispatched = false;
            EventHandler observer = (_, _) => dispatched = application.Dispatcher.CheckAccess();
            UiText.Current.LanguageChanged += observer;
            try { await Task.Run(() => LocalizationService.Current.SetLanguageAsync("en-US")); }
            finally { UiText.Current.LanguageChanged -= observer; }
            Require(dispatched && UiText.Current.CultureName == "en-US", "A background language request did not notify on the UI dispatcher.");
            await Task.WhenAll(LocalizationService.Current.SetLanguageAsync("pt-BR"), LocalizationService.Current.SetLanguageAsync("es-ES"));
            using var finalDocument = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Require(finalDocument.RootElement.GetProperty("language").GetString() == "es-ES" && Directory.GetFiles(directory, "*.tmp").Length == 0,
                "Concurrent language changes lost the last choice or left temporary preference files.");
        }
        finally
        {
            LocalizationService.Current.Initialize(application, Path.Combine(outputDirectory, "preferences", "settings.json"));
            await LocalizationService.Current.SetLanguageAsync("pt-BR");
        }
    }

    private async Task VerifyLocalizedModelRefreshAsync()
    {
        var folder = new FileEntry { Name = "QA folder", FullPath = "/qa/folder", IsDirectory = true };
        var file = new FileEntry { Name = "QA résumé 空.txt", FullPath = "/qa/QA résumé 空.txt", Size = 1572864, LastModified = new DateTime(2026, 10, 30, 17, 45, 0) };
        var clone = file with { };
        var hash = file.GetHashCode();
        var job = CreateTimedTransfer(new QaMonotonicTimeProvider());
        var failedJob = CreateTimedTransfer(new QaMonotonicTimeProvider());
        typeof(TransferItemViewModel).GetMethod("SetFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(failedJob, [new UnauthorizedAccessException("QA_LOCALIZATION_FAILURE_SECRET")]);
        var safeDiagnostic = failedJob.Error;
        var profile = CreateProfile();
        var vm = CreateViewModel(new MemoryProfileStore([profile]), new RecordingInteractions(), new QaRemoteConnectionService
        {
            ConnectHandler = (_, _) => Task.FromException<IRemoteConnectionSession>(new RemoteConnectionException(RemoteConnectionErrorCode.AuthenticationFailed, "QA_LOCALIZATION_CONNECTION_SECRET"))
        });
        await vm.InitializeAsync();
        await vm.ConnectCommand.ExecuteAsync(profile);
        var filters = vm.TransferFilters.ToArray();
        vm.SelectedTransferFilter = TransferFilter.Failed;
        var fileNotifications = 0;
        var jobNotifications = 0;
        file.PropertyChanged += (_, _) => fileNotifications++;
        job.PropertyChanged += (_, _) => jobNotifications++;
        var folderLabels = new HashSet<string>();
        try
        {
            foreach (var code in UiText.SupportedCultureNames)
            {
                await LocalizationService.Current.SetLanguageAsync(code);
                folderLabels.Add(folder.TypeLabel);
                Require(folder.TypeLabel == UiText.Get("Vm.File.Folder"), $"The existing folder label did not refresh for {code}.");
                Require(file.SizeLabel == 1.5.ToString("N1", CultureInfo.GetCultureInfo(code)) + " MB"
                    && file.ModifiedLabel == file.LastModified.ToString("g", CultureInfo.GetCultureInfo(code)), $"File size/date formats did not refresh for {code}.");
                Require(file == clone && file.GetHashCode() == hash && file.Name == "QA résumé 空.txt" && file.FullPath == "/qa/QA résumé 空.txt",
                    "Language notifications changed record equality or user-supplied file names and paths.");
                Require(vm.ConnectionError == UiText.Get("Vm.Connect.Authentication") && vm.StatusMessage == vm.ConnectionError
                    && vm.TransferFilters.SequenceEqual(filters) && vm.SelectedTransferFilter == TransferFilter.Failed
                    && filters.All(filter => filter.Label == UiText.Get($"Vm.Filter.{filter.Filter}")),
                    $"Existing status, error, or transfer filter bindings did not refresh safely for {code}.");
                Require(failedJob.Error == safeDiagnostic && failedJob.FailureSummary == UiText.Get("Vm.TransferFailure.PermissionDenied.Summary")
                    && failedJob.FailureReason == UiText.Get("Vm.TransferFailure.PermissionDenied.Reason")
                    && failedJob.SuggestedAction == UiText.Get("Vm.TransferFailure.PermissionDenied.Action")
                    && !failedJob.Error.Contains("QA_LOCALIZATION", StringComparison.Ordinal),
                    $"Existing failure details did not refresh independently of their safe English diagnostic for {code}.");
                foreach (var state in Enum.GetValues<TransferState>())
                {
                    SetTransferState(job, state);
                    Require(job.StatusLabel == UiText.Get($"Vm.Transfer.{state}"), $"The existing transfer {state} label did not refresh for {code}.");
                }
            }

            Require(folderLabels.Count == 3, "The existing folder label did not use all three catalogs.");
            Require(fileNotifications >= 3 && jobNotifications >= 3, "Existing model bindings were not notified of language changes.");
        }
        finally { await vm.ShutdownAsync(); await LocalizationService.Current.SetLanguageAsync("pt-BR"); }
    }

    private async Task VerifyLiveLanguagePickerAsync()
    {
        var profile = CreateProfile();
        var store = new MemoryProfileStore([profile]);
        var files = new QaRemoteFiles();
        var session = new QaRemoteConnectionSession(profile) { Files = files };
        var service = new QaRemoteConnectionService { ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(session) };
        var vm = CreateViewModel(store, new RecordingInteractions(), service);
        await vm.InitializeAsync();
        await vm.NavigateToAsync(_workspace);
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        vm.ToggleTransferQueueCommand.Execute(null);
        vm.SelectedLocalEntry = vm.LocalEntries.First(entry => !entry.IsDirectory);
        await vm.QueueUploadFilesAsync([vm.SelectedLocalEntry!.FullPath, _workspace]);
        var queued = vm.TransferJobs.Single();
        var profiles = vm.Connections.ToArray();
        var localFiles = vm.LocalEntries.ToArray();
        var remoteFiles = vm.RemoteEntries.ToArray();
        var localPath = vm.LocalPath;
        var remotePath = vm.RemotePath;
        var window = new MainWindow(vm, initializeOnLoad: false) { Width = 1180, Height = 800 };
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            window.NavigateTo("settings");
            await SettleLayoutAsync(window);
            var picker = window.FindName("LanguagePicker") as ComboBox ?? FindVisualChild<ComboBox>(window, control => control.Name == "LanguagePicker")
                ?? throw new InvalidOperationException("The Settings language picker could not be found.");
            Require(picker.Items.Count == 3, "The language picker does not expose exactly the three supported languages.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                foreach (var code in UiText.SupportedCultureNames)
                {
                    picker.SelectedValue = code;
                    await window.PendingLanguageChange;
                    await WaitUntilAsync(() => UiText.Current.CultureName == code);
                    await SettleLayoutAsync(window);
                    Require(window.Language.IetfLanguageTag.Equals(code, StringComparison.OrdinalIgnoreCase), $"Existing windows did not inherit live formatting language {code} (actual {window.Language.IetfLanguageTag}).");
                    foreach (var page in new[] { "settings", "home", "connections", "files", "terminal", "transfers", "automations" })
                    {
                        window.NavigateTo(page);
                        await SettleLayoutAsync(window);
                        Require(VerifyCatalogBindings(window) >= 5, $"Page {page} did not expose localized bindings in {code}.");
                        if (page is "settings" or "connections" or "files") Capture(window, $"localized-{page}-{code}-{theme.ToString().ToLowerInvariant()}.png");
                    }

                    Require(ReferenceEquals(window.DataContext, vm) && vm.HasActiveSession && session.IsConnected
                        && vm.ActiveConnection?.Id == profile.Id && vm.Connections.SequenceEqual(profiles)
                        && vm.LocalEntries.SequenceEqual(localFiles) && vm.RemoteEntries.SequenceEqual(remoteFiles)
                        && vm.LocalPath == localPath && vm.RemotePath == remotePath
                        && ReferenceEquals(vm.TransferJobs.Single(), queued) && queued.State == TransferState.Queued
                        && store.SaveCalls == 0, $"Language {code} or theme {theme} changed user profiles, session, paths, files, or queued work.");
                    Require(vm.BatchUploadNotice == UiText.Format("Vm.Batch.File", 1) + " " + UiText.Format("Vm.Batch.Directory", 1)
                        && vm.StatusMessage == UiText.Format("Vm.Status.BatchQueued", 1, 1, 0, 0),
                        $"An existing upload batch notice or status did not refresh for {code}.");
                }
            }

            window.NavigateTo("settings");
            picker.SelectedValue = "en-US";
            var pendingPreference = window.PendingLanguageChange;
            window.Close();
            await pendingPreference.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => !window.IsVisible);
            using var savedOnClose = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "preferences", "settings.json")));
            Require(savedOnClose.RootElement.GetProperty("language").GetString() == "en-US",
                "Closing immediately after selecting a language lost the persisted choice.");
        }
        finally
        {
            await vm.ShutdownAsync();
            if (window.IsVisible) window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            await LocalizationService.Current.SetLanguageAsync("pt-BR");
        }
    }

    private async Task VerifyLocalizedDialogsAsync()
    {
        const string secret = "QA_LOCALIZATION_SECRET_DO_NOT_EXPOSE";
        const string destination = "/home/qa/QA résumé 空.txt";
        const string fingerprint = "SHA256:QA_FIXTURE_FINGERPRINT";
        var profile = CreateProfile() with { PrivateKeyPath = null };
        var job = CreateTimedTransfer(new QaMonotonicTimeProvider());
        typeof(TransferItemViewModel).GetMethod("SetFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(job, [new UnauthorizedAccessException(secret)]);
        var credentials = new SshCredentialsWindow(profile, false, new QaWindowsUserVerifier(), new QaCredentialClipboard());
        var editor = new ConnectionEditorWindow(profile);
        var hostKey = new HostKeyVerificationWindow(new SshHostIdentity("2001:db8::17", 2222, "ssh-ed25519", fingerprint));
        var overwrite = new OverwriteConfirmationWindow(destination, true);
        var details = new TransferDetailsWindow(job);
        Window[] windows = [editor, credentials, hostKey, overwrite, details];
        var titleKeys = new Dictionary<Window, string>
        {
            [editor] = "Dialog.EditVps", [credentials] = "Dialog.SshAuthentication", [hostKey] = "Dialog.VerifyServer",
            [overwrite] = "Dialog.ReplaceFile", [details] = "Dialog.TransferDetails"
        };
        try
        {
            foreach (var window in windows) { PrepareHiddenWindow(window); window.Show(); }
            ((PasswordBox)credentials.FindName("SecretBox")).Password = secret;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                foreach (var code in UiText.SupportedCultureNames)
                {
                    await LocalizationService.Current.SetLanguageAsync(code);
                    foreach (var window in windows)
                    {
                        await SettleLayoutAsync(window);
                        Require(window.Title == UiText.Get(titleKeys[window]) && window.Language.IetfLanguageTag.Equals(code, StringComparison.OrdinalIgnoreCase) && VerifyCatalogBindings(window) > 0,
                            $"Existing {window.GetType().Name} did not refresh for {code}.");
                        Capture(window, $"localized-{window.GetType().Name}-{code}-{theme.ToString().ToLowerInvariant()}.png");
                    }

                    Require(((PasswordBox)credentials.FindName("SecretBox")).Password == secret
                        && !CredentialSurfaceText(credentials).Contains(secret, StringComparison.Ordinal)
                        && ((TextBox)hostKey.FindName("FingerprintTextBox")).Text == fingerprint
                        && ((TextBox)overwrite.FindName("DestinationTextBox")).Text == destination
                        && hostKey.Decision == HostKeyDecision.Reject,
                        $"Changing to {code} altered or disclosed sensitive dialog values.");
                }
            }

        }
        finally
        {
            foreach (var window in windows) window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            await LocalizationService.Current.SetLanguageAsync("pt-BR");
        }
    }

    private static int VerifyCatalogBindings(DependencyObject root)
    {
        var count = 0;
        var values = root.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            var property = values.Current.Property;
            var binding = BindingOperations.GetBinding(root, property);
            if (binding?.Source is UiText && binding.Path?.Path is { } path && path.StartsWith('[') && path.EndsWith(']'))
            {
                var key = path[1..^1];
                Require(root.GetValue(property) is string text && text == UiText.Get(key), $"Live binding {key} retained a previous language.");
                count++;
            }
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) count += VerifyCatalogBindings(VisualTreeHelper.GetChild(root, index));
        return count;
    }
}
