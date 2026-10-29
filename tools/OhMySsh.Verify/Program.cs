using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Converters;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Views;
using OhMySsh.Infrastructure.Files;
using OhMySsh.Infrastructure.Connections;
using OhMySsh.Infrastructure.Persistence;

namespace OhMySsh.Verify;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var localRoot = Path.Combine(repositoryRoot, ".local");
            var outputParent = args.Length == 0 ? localRoot : Path.GetFullPath(args[0], repositoryRoot);
            if (!IsWithin(outputParent, localRoot))
            {
                throw new ArgumentException("Verification output must remain inside the repository's .local directory.");
            }

            var outputDirectory = Path.Combine(outputParent, $"verify-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outputDirectory);
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
            var runner = new VerificationRunner(application, outputDirectory);
            var execution = runner.RunAsync();
            _ = execution.ContinueWith(
                _ => application.Dispatcher.BeginInvoke(application.Shutdown),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            application.Run();
            execution.GetAwaiter().GetResult();
            Console.WriteLine($"Report: {Path.Combine(outputDirectory, "verification.json")}");
            return runner.FailureCount == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && Directory.Exists(Path.Combine(directory.FullName, "src", "OhMySsh.Desktop")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The verification repository could not be located.");
    }

    private static bool IsWithin(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed partial class VerificationRunner(Application application, string outputDirectory)
{
    private readonly List<CheckResult> _checks = [];
    private readonly List<string> _screenshots = [];
    private readonly BindingErrorListener _bindingErrors = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private string _workspace = string.Empty;

    public int FailureCount => _checks.Count(check => !check.Passed);

    public async Task RunAsync()
    {
        await CheckAsync("XAML: shared resources", () =>
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/OhMySsh;component/Themes/Workbench.xaml")
            });
            application.Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
            application.Resources["InverseBoolToVisibility"] = new InverseBooleanToVisibilityConverter();
            application.Resources["ProtocolToLabel"] = new ConnectionProtocolToLabelConverter();
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingErrors);
            ThemeService.Current.Initialize(application, AppTheme.Light);
            return Task.CompletedTask;
        });

        await CheckAsync("Fixtures: isolated QA data", CreateFixturesAsync);
        await CheckAsync("Profiles: persistence and loading of every field", VerifyProfileRoundtripAsync);
        await CheckAsync("Profiles: create, edit, search, favorite, and delete", VerifyProfileCrudAsync);
        await CheckAsync("VPS: creation, editing, and favorites survive restart", VerifySavedVpsLifecycleAsync);
        await CheckAsync("VPS: copyable SSH command with escaped arguments and apostrophes", VerifySshCommandCopyAsync);
        await CheckAsync("Profiles: failed saves preserve loaded data", VerifyFailedSaveAsync);
        await CheckAsync("Profiles: invalid JSON blocks changes and preserves the file", VerifyFailedLoadAsync);
        await CheckAsync("Files: actual navigation and failure without losing the listing", VerifyLocalNavigationAsync);
        await CheckAsync("Files: concurrent navigation does not mix results", VerifyConcurrentNavigationAsync);
        await CheckAsync("Manual connection: validation, temporary session, and explicit saving", VerifyManualConnectionAsync);
        await CheckAsync("Connection: gate, awaited confirmation, failure, cancellation, and disconnection", VerifyConnectionSessionStateAsync);
        await CheckAsync("Connection: remote loss, stale callbacks, retry, and shutdown", VerifyConnectionLossAndShutdownAsync);
        await CheckAsync("Connection: English diagnostic messages and sanitized failures", VerifyEnglishConnectionDiagnosticsAsync);
        await CheckAsync("SSH dialogs: secrets, manual credentials, trust decisions, and themes", VerifySshDialogsAsync);
        await CheckAsync("SSH prompts: cancellation closes pending credential and host dialogs", VerifyPromptCancellationAsync);
        await CheckAsync("SFTP browser: initialization, failed navigation, and stale results", VerifyRemoteBrowsingAsync);
        await CheckAsync("SFTP queue: serial work, progress, cancellation, and profile isolation", VerifyTransferQueueAsync);
        await CheckAsync("SFTP overwrite: rejection and pending-prompt cancellation", VerifyTransferOverwriteAsync);
        await CheckAsync("SFTP lifecycle: disconnect cancels work and clears remote state", VerifyTransferDisconnectionAsync);
        await CheckAsync("Transfer metrics: monotonic speed, remaining time, stalls, and terminal states", VerifyTransferMetricsAsync);
        await CheckAsync("Transfer queue controls: pause, resume, cancel all, and clear completed", VerifyTransferQueueControlsAsync);
        await CheckAsync("Transfer retry: captured targets, confirmation, reconnection, and source failures", VerifyTransferRetryAsync);
        await CheckAsync("SFTP interface: populated remote files and queue in both themes", CaptureSftpWorkspaceAsync);
        await CheckAsync("XAML: empty and saved connection center in both themes", async () =>
        {
            await CaptureConnectionsPageAsync(false);
            await CaptureConnectionsPageAsync(true);
        });
        await CheckAsync("XAML: dashboard and workspace require a confirmed session", CaptureConnectedWorkspaceAsync);
        await CheckAsync("Layout: connection center and workspace at minimum size", VerifyMinimumLayoutAsync);
        await CheckAsync("XAML: editor and server validation", VerifyConnectionEditorAsync);
        await CheckAsync("XAML: QA editor in light and dark themes", CaptureEditorAsync);
        await CheckAsync("Themes and navigation preserve profiles, selection, and files", VerifyThemeNavigationStateAsync);
        await CheckAsync("WPF bindings: no failures", () =>
        {
            Require(_bindingErrors.Messages.Count == 0, string.Join(Environment.NewLine, _bindingErrors.Messages));
            return Task.CompletedTask;
        });

        PresentationTraceSources.DataBindingSource.Listeners.Remove(_bindingErrors);
        var report = new
        {
            StartedAt = _startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            OutputDirectory = outputDirectory,
            Passed = _checks.Count(check => check.Passed),
            Failed = FailureCount,
            Checks = _checks,
            Screenshots = _screenshots,
            BindingErrors = _bindingErrors.Messages,
            FixtureNotice = "All servers are fixtures under example.invalid. Controlled QA sessions exist only in this verifier; no network connection was opened."
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verification.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var text = new StringBuilder();
        text.AppendLine($"oh-my-ssh verification: {report.Passed} passed, {report.Failed} failed");
        foreach (var check in _checks)
        {
            text.AppendLine($"{(check.Passed ? "PASS" : "FAIL")} {check.Name} ({check.DurationMs} ms)");
            if (check.Error is not null)
            {
                text.AppendLine(check.Error);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "verification.txt"), text.ToString());
        Console.Write(text);
    }

    private async Task CreateFixturesAsync()
    {
        _workspace = Path.Combine(outputDirectory, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, "deploy"));
        Directory.CreateDirectory(Path.Combine(_workspace, "logs"));
        Directory.CreateDirectory(Path.Combine(_workspace, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(_workspace, "README.md"), "# QA workspace\nFixture files for interface verification.\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "deploy", "release.txt"), "qa-release-0.1.0\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "deploy.ps1"), "# QA fixture; not executed.\n");
        await File.WriteAllTextAsync(Path.Combine(_workspace, "manifest.json"), "{\"environment\":\"qa\",\"version\":\"0.1.0\"}\n");
    }

    private async Task VerifyProfileRoundtripAsync()
    {
        var store = new JsonConnectionProfileStore(Path.Combine(outputDirectory, "roundtrip", "connections.json"));
        Require((await store.LoadAsync()).Count == 0, "A missing file must produce an empty list.");
        ConnectionProfile[] profiles = [CreateProfile(), CreateProfile() with
        {
            Id = Guid.NewGuid(), Name = "Arquivos de QA", Host = "files.example.invalid", Protocol = ConnectionProtocol.Ftps, Port = 21,
            PrivateKeyPath = null, IsFavorite = false
        }];
        await store.SaveAsync(profiles);
        Require((await store.LoadAsync()).SequenceEqual(profiles), "The serialization round trip changed profile fields.");
        var original = await File.ReadAllTextAsync(store.StoragePath);
        await ExpectExceptionAsync<InvalidDataException>(() => store.SaveAsync([profiles[0], profiles[0]]));
        Require(await File.ReadAllTextAsync(store.StoragePath) == original, "An invalid write changed the existing file.");
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(store.StoragePath)!, "*.tmp").Any(), "A temporary file remained after saving.");
    }

    private async Task VerifyProfileCrudAsync()
    {
        var store = new MemoryProfileStore();
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(store, interactions);
        Require(!viewModel.AddConnectionCommand.CanExecute(null), "Creation was enabled before the store loaded.");
        await viewModel.InitializeAsync();
        Require(viewModel.ProfilesAvailable && viewModel.AddConnectionCommand.CanExecute(null), "Creation was not enabled after a successful load.");
        var profile = CreateProfile();
        interactions.EditResults.Enqueue(profile);
        await viewModel.AddConnectionCommand.ExecuteAsync();
        Require(viewModel.ConnectionCount == 1 && viewModel.SelectedConnection?.Id == profile.Id, "Creation did not select the saved profile.");
        Require(store.Profiles.Single() == profile, "The new profile was not persisted.");

        var second = profile with { Id = Guid.NewGuid(), Name = "Backup de QA", Host = "backup.example.invalid", IsFavorite = false };
        interactions.EditResults.Enqueue(second);
        await viewModel.AddConnectionCommand.ExecuteAsync();
        viewModel.SearchText = "EDGE.EXAMPLE";
        Require(viewModel.FilteredConnections.Cast<ConnectionProfile>().Single().Id == profile.Id, "Host search must be case-insensitive.");
        viewModel.SearchText = "";
        viewModel.FilterFavoritesCommand.Execute(null);
        Require(viewModel.FilteredConnections.Cast<ConnectionProfile>().Single().IsFavorite, "The favorites filter included another profile.");
        viewModel.FilterFavoritesCommand.Execute(null);
        viewModel.SelectedConnection = profile;
        var edited = profile with { Name = "Edge QA atualizado", Port = 2222 };
        interactions.EditResults.Enqueue(edited);
        await viewModel.EditConnectionCommand.ExecuteAsync();
        Require(viewModel.SelectedConnection == edited && store.Profiles.Any(item => item == edited), "Editing did not preserve selection and persistence.");
        viewModel.CopyEndpointCommand.Execute(null);
        Require(interactions.ClipboardText == "deploy@edge.example.invalid:2222", "The copied endpoint is incorrect.");
        var saveCount = store.SaveCalls;
        var messageCount = interactions.Messages.Count;
        await viewModel.ConnectCommand.ExecuteAsync();
        Require(!viewModel.HasActiveSession && !viewModel.IsConnecting && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "The default adapter activated a session or failed to show the inline error.");
        Require(interactions.Messages.Count == messageCount, "An unavailable connection opened a modal instead of showing an inline error.");
        Require(store.SaveCalls == saveCount, "Attempting a connection changed saved profiles.");
        await viewModel.DeleteConnectionCommand.ExecuteAsync();
        Require(viewModel.ConnectionCount == 1 && store.Profiles.Single().Id == second.Id, "Deletion was not persisted.");
    }

    private async Task VerifyFailedSaveAsync()
    {
        var original = CreateProfile();
        var store = new MemoryProfileStore([original]) { FailSave = true };
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(store, interactions);
        await viewModel.InitializeAsync();
        viewModel.SearchText = "filtro que precisa permanecer";
        viewModel.FavoritesOnly = true;
        viewModel.SelectedConnection = original;
        var savedEvents = 0;
        viewModel.ProfileSaved += (_, _) => savedEvents++;
        interactions.EditResults.Enqueue(original with { Name = "Esta alteração não pode ser salva" });
        await ExpectExceptionAsync<IOException>(() => viewModel.EditConnectionCommand.ExecuteAsync());
        Require(viewModel.Connections.Single() == original, "A failed save changed the displayed list.");
        Require(viewModel.SelectedConnection == original && !viewModel.IsBusy, "A failed save lost selection or left the window busy.");
        Require(store.Profiles.Single() == original, "The fixture changed the store despite the simulated failure.");
        Require(viewModel.SearchText == "filtro que precisa permanecer" && viewModel.FavoritesOnly,
            "A failed edit cleared the filters.");

        interactions.EditResults.Enqueue(original with { Id = Guid.NewGuid(), Name = "Cadastro que falha", IsFavorite = false });
        await ExpectExceptionAsync<IOException>(() => viewModel.AddConnectionCommand.ExecuteAsync());
        Require(viewModel.Connections.Single() == original && viewModel.SelectedConnection == original,
            "A failed creation changed profiles or selection.");
        Require(viewModel.SearchText == "filtro que precisa permanecer" && viewModel.FavoritesOnly && savedEvents == 0,
            "A failed creation cleared filters or raised the profile-saved event.");
    }

    private async Task VerifySavedVpsLifecycleAsync()
    {
        var storagePath = Path.Combine(outputDirectory, "vps-lifecycle", "connections.json");
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), interactions);
        await viewModel.InitializeAsync();
        var original = CreateProfile() with
        {
            Name = "VPS · aplicação QA", Group = "VPS", Port = 22022, IsFavorite = false,
            PrivateKeyPath = @"C:\QA\keys\vps_ed25519"
        };
        var savedEvents = 0;
        viewModel.ProfileSaved += (_, _) => savedEvents++;
        viewModel.SearchText = "não corresponde à VPS cadastrada";
        viewModel.FavoritesOnly = true;
        interactions.EditResults.Enqueue(original);
        await viewModel.AddConnectionCommand.ExecuteAsync();
        Require(viewModel.SelectedConnection == original && viewModel.FilteredConnections.Cast<ConnectionProfile>().Single() == original,
            "Saving a VPS did not select it in the visible list.");
        Require(viewModel.SearchText.Length == 0 && !viewModel.FavoritesOnly && savedEvents == 1,
            "Saving a profile did not clear filters or notify the shell.");

        // A new store and view model represent closing and reopening the app.
        var restarted = CreateViewModel(new JsonConnectionProfileStore(storagePath), new RecordingInteractions());
        await restarted.InitializeAsync();
        Require(restarted.Connections.Single() == original && restarted.SelectedConnection == original,
            "Restarting the app changed the saved VPS fields.");

        var edited = original with
        {
            Name = "VPS · aplicação QA revisada", Host = "reviewed.example.invalid", Username = "ops-build",
            Group = "VPS de QA", Port = 2222, PrivateKeyPath = @"C:\QA\keys\reviewed_ed25519"
        };
        viewModel.SelectedConnection = null;
        viewModel.SearchText = "outro filtro sem resultado";
        viewModel.FavoritesOnly = true;
        interactions.EditResults.Enqueue(edited);
        await viewModel.EditConnectionCommand.ExecuteAsync(original);
        Require(viewModel.SelectedConnection == edited && viewModel.SearchText.Length == 0 && !viewModel.FavoritesOnly && savedEvents == 2,
            "Editing by parameter lost the target profile or did not reveal it.");

        restarted = CreateViewModel(new JsonConnectionProfileStore(storagePath), new RecordingInteractions());
        await restarted.InitializeAsync();
        Require(restarted.Connections.Single() == edited, "Edits to all fields did not survive restart.");
        await restarted.ToggleFavoriteCommand.ExecuteAsync(edited);
        var favorite = edited with { IsFavorite = true };
        Require(restarted.SelectedConnection == favorite, "Favoriting lost profile selection.");

        var deleteInteractions = new RecordingInteractions { AllowDelete = false };
        var deleteViewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), deleteInteractions);
        await deleteViewModel.InitializeAsync();
        Require(deleteViewModel.Connections.Single() == favorite, "The favorite did not survive restart.");
        var originalFile = await File.ReadAllBytesAsync(storagePath);
        await deleteViewModel.DeleteConnectionCommand.ExecuteAsync(favorite);
        Require(deleteInteractions.ConfirmDeleteCalls == 1 && deleteViewModel.Connections.Single() == favorite,
            "Canceling confirmation removed the profile from the interface.");
        Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(originalFile),
            "Canceling deletion changed the profile file.");

        deleteInteractions.AllowDelete = true;
        // Deny replacement of the real JSON destination to exercise an actual
        // filesystem failure, rather than an in-memory store approximation.
        await using (var lockedFile = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failedToReplace = false;
            try
            {
                await deleteViewModel.DeleteConnectionCommand.ExecuteAsync(favorite);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failedToReplace = true;
            }

            Require(failedToReplace, "The fixture could not prevent replacement of the profile file.");
            Require(deleteViewModel.Connections.Single() == favorite && deleteViewModel.SelectedConnection == favorite && !deleteViewModel.IsBusy,
                "An actual deletion failure changed the list/selection or left the shell busy.");
            Require((await File.ReadAllBytesAsync(storagePath)).SequenceEqual(originalFile),
                "An actual deletion failure changed the original file.");
        }

        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(storagePath)!, "*.tmp").Any(),
            "An actual write failure left a temporary file.");
        await deleteViewModel.DeleteConnectionCommand.ExecuteAsync(favorite);
        restarted = CreateViewModel(new JsonConnectionProfileStore(storagePath), new RecordingInteractions());
        await restarted.InitializeAsync();
        Require(restarted.Connections.Count == 0, "A confirmed deletion reappeared after restart.");
    }

    private async Task VerifySshCommandCopyAsync()
    {
        var profile = CreateProfile() with
        {
            Username = "ops'build", Port = 2222, PrivateKeyPath = @"C:\QA\keys\O'Brien_ed25519"
        };
        var ftp = profile with { Id = Guid.NewGuid(), Protocol = ConnectionProtocol.Ftp, Port = 21, PrivateKeyPath = null };
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new MemoryProfileStore([profile, ftp]), interactions);
        await viewModel.InitializeAsync();
        viewModel.SelectedConnection = ftp;
        Require(viewModel.CopySshCommand.CanExecute(profile), "The SSH command must accept an SSH profile passed as a parameter.");
        viewModel.CopySshCommand.Execute(profile);
        const string expected = "ssh -l 'ops''build' -p 2222 -i 'C:\\QA\\keys\\O''Brien_ed25519' -- 'edge.example.invalid'";
        Require(interactions.ClipboardText == expected, "The SSH command lost the user, port, key, or PowerShell apostrophe escaping.");
        Require(viewModel.SelectedConnection == ftp, "Copying a command from another card changed selection.");
        Require(!viewModel.CopySshCommand.CanExecute(ftp), "An FTP profile must not produce an SSH command.");
        viewModel.CopySshCommand.Execute(ftp);
        Require(interactions.ClipboardText == expected, "An FTP profile overwrote the copied SSH command.");

        viewModel.SelectConnectionCommand.Execute(profile);
        Require(viewModel.SelectedConnection == profile, "Selecting a card did not select its associated profile.");
        var unknown = profile with { Id = Guid.NewGuid() };
        Require(!viewModel.SelectConnectionCommand.CanExecute(unknown), "Selection must reject profiles outside the collection.");
    }

    private async Task VerifyFailedLoadAsync()
    {
        var storagePath = Path.Combine(outputDirectory, "invalid", "connections.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        const string invalidOriginal = "[{\"id\": \"perfil-recuperavel\", esta parte precisa de recuperação";
        await File.WriteAllTextAsync(storagePath, invalidOriginal);
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), interactions);
        await viewModel.InitializeAsync();
        Require(!viewModel.ProfilesAvailable && viewModel.StatusKind == "Error", "Successful local navigation hid a profile load failure.");
        Require(!viewModel.AddConnectionCommand.CanExecute(null)
            && !viewModel.EditConnectionCommand.CanExecute(null)
            && !viewModel.DeleteConnectionCommand.CanExecute(null), "A load failure did not block profile changes.");
        interactions.EditResults.Enqueue(CreateProfile());
        await viewModel.AddConnectionCommand.ExecuteAsync();
        await viewModel.EditConnectionCommand.ExecuteAsync();
        await viewModel.DeleteConnectionCommand.ExecuteAsync();
        await viewModel.NavigateToAsync(_workspace);
        Require(viewModel.StatusKind == "Error", "Opening a folder hid the connection file error.");
        Require(interactions.EditCalls == 0, "A blocked operation opened the editor.");
        Require(await File.ReadAllTextAsync(storagePath) == invalidOriginal, "The recoverable file was overwritten after a load failure.");
    }

    private async Task VerifyLocalNavigationAsync()
    {
        var viewModel = CreateViewModel(new MemoryProfileStore([CreateProfile()]), new RecordingInteractions());
        await viewModel.InitializeAsync();
        var selected = viewModel.SelectedConnection;
        await viewModel.NavigateToAsync(_workspace);
        Require(viewModel.LocalEntries.Count == 6, "Navigation did not display fixture files.");
        Require(viewModel.LocalEntries.Take(3).All(entry => entry.IsDirectory), "Directories must appear before files.");
        var originalEntries = viewModel.LocalEntries.ToArray();
        await viewModel.NavigateToAsync(Path.Combine(_workspace, "pasta-inexistente"));
        Require(viewModel.LocalPath == _workspace && viewModel.LocalEntries.SequenceEqual(originalEntries), "A missing directory changed the loaded path or listing.");
        Require(viewModel.SelectedConnection == selected, "A navigation failure changed the selected profile.");
        await viewModel.OpenLocalEntryCommand.ExecuteAsync(originalEntries.First(entry => entry.Name == "deploy"));
        Require(viewModel.LocalEntries.Single().Name == "release.txt", "Opening a directory did not navigate to its contents.");
        await viewModel.GoUpCommand.ExecuteAsync();
        Require(viewModel.LocalPath == _workspace, "Voltar deve utilizar o caminho carregado.");
    }

    private async Task VerifyConcurrentNavigationAsync()
    {
        var delayedPath = Path.Combine(_workspace, "slow");
        var ignoredPath = Path.Combine(_workspace, "ignored");
        var files = new DelayedLocalFileService(delayedPath);
        var viewModel = new MainViewModel(new MemoryProfileStore(), files, new RecordingInteractions());
        await viewModel.InitializeAsync();
        var first = viewModel.NavigateToAsync(delayedPath);
        Require(viewModel.IsBusy, "Pending navigation did not block commands.");
        await viewModel.NavigateToAsync(ignoredPath);
        files.Completion.SetResult([new FileEntry { Name = "slow-result.txt", FullPath = Path.Combine(delayedPath, "slow-result.txt") }]);
        await first;
        Require(viewModel.LocalPath == delayedPath && viewModel.LocalEntries.Single().Name == "slow-result.txt", "Navigation applied an out-of-order listing.");
        Require(!files.RequestedPaths.Contains(ignoredPath) && !viewModel.IsBusy, "Concurrent navigation started or left the shell busy.");
    }

    private async Task VerifyManualConnectionAsync()
    {
        var store = new MemoryProfileStore();
        var interactions = new RecordingInteractions();
        var service = new QaRemoteConnectionService();
        var viewModel = CreateViewModel(store, interactions, service);
        await viewModel.InitializeAsync();
        var savedEvents = 0;
        viewModel.ProfileSaved += (_, _) => savedEvents++;

        viewModel.ManualHost = string.Empty;
        viewModel.ManualUsername = "deploy";
        await viewModel.ManualConnectCommand.ExecuteAsync();
        Require(service.Requests.Count == 0 && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "A manual connection without a host reached the adapter.");
        viewModel.ManualHost = "ssh://manual.example.invalid";
        await viewModel.ManualConnectCommand.ExecuteAsync();
        Require(service.Requests.Count == 0 && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "The manual form accepted a host with a protocol prefix.");
        viewModel.ManualHost = "manual.example.invalid";
        viewModel.ManualPort = "0";
        await viewModel.ManualConnectCommand.ExecuteAsync();
        Require(service.Requests.Count == 0 && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
            "The manual form accepted an out-of-range port.");

        viewModel.ManualName = string.Empty;
        viewModel.ManualPort = "2222";
        viewModel.ManualUsername = "ops";
        viewModel.ManualProtocol = ConnectionProtocol.Ssh;
        viewModel.ManualPrivateKeyPath = @"C:\QA\keys\manual_ed25519";
        Require(store.SaveCalls == 0 && service.Requests.Count == 0,
            "Filling in the manual form opened a connection or persisted data.");
        await viewModel.ManualConnectCommand.ExecuteAsync();
        var request = service.Requests.Single();
        Require(viewModel.HasActiveSession && viewModel.ActiveConnection == request,
            "The manual form did not activate the session confirmed by the QA adapter.");
        Require(request.Name == "manual.example.invalid" && request.Host == "manual.example.invalid"
            && request.Username == "ops" && request.Port == 2222 && request.Protocol == ConnectionProtocol.Ssh
            && request.PrivateKeyPath == @"C:\QA\keys\manual_ed25519" && request.Group == "Conexão rápida",
            "The manual request lost fields or did not default its optional name to the host.");
        Require(store.SaveCalls == 0 && viewModel.Connections.Count == 0 && savedEvents == 0,
            "Connecting manually saved a profile without an explicit request.");

        await viewModel.DisconnectCommand.ExecuteAsync();
        Require(!viewModel.HasActiveSession && viewModel.ManualHost == "manual.example.invalid",
            "Disconnecting did not end the session or cleared the manual form.");
        await viewModel.SaveManualConnectionCommand.ExecuteAsync();
        var saved = store.Profiles.Single();
        Require(saved.Name == request.Host && saved.Host == request.Host && saved.Port == request.Port
            && saved.Username == request.Username && saved.Protocol == request.Protocol
            && saved.PrivateKeyPath == request.PrivateKeyPath && saved.Group == "VPS",
            "Saving the manual form lost fields or failed to create a VPS profile.");
        Require(!viewModel.HasActiveSession && service.Requests.Count == 1 && savedEvents == 1,
            "Saving manually opened another connection or failed to notify the shell.");
        Require(interactions.Messages.Count == 0, "Manual validation or connection opened unexpected modal dialogs.");
    }

    private async Task VerifyConnectionSessionStateAsync()
    {
        var profile = CreateProfile();
        var store = new MemoryProfileStore([profile]);
        var interactions = new RecordingInteractions();
        var completion = new TaskCompletionSource<IRemoteConnectionSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new QaRemoteConnectionService { ConnectHandler = (_, _) => completion.Task };
        var viewModel = CreateViewModel(store, interactions, service);
        await viewModel.InitializeAsync();
        var sessionEvents = 0;
        viewModel.ActiveSessionChanged += (_, _) => sessionEvents++;
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            await SettleLayoutAsync(window);
            RequireConnectionLanding(window);
            foreach (var page in new[] { "home", "files", "terminal", "transfers", "automations" })
            {
                window.NavigateTo(page);
                await SettleLayoutAsync(window);
                RequireConnectionLanding(window);
            }

            var connection = viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(viewModel.IsConnecting && !viewModel.HasActiveSession && sessionEvents == 0,
                "The shell activated a session before asynchronous adapter confirmation.");
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            RequireConnectionLanding(window);
            var session = new QaRemoteConnectionSession(profile);
            completion.SetResult(session);
            await connection;
            await SettleLayoutAsync(window);
            Require(viewModel.HasActiveSession && viewModel.ActiveConnection == profile && !viewModel.IsConnecting && sessionEvents == 1,
                "A confirmed session did not update connection state.");
            Require((window.FindName("HomePage") as FrameworkElement)?.IsVisible == true,
                "A confirmed session did not open the VPS dashboard.");
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            Require((window.FindName("MainWorkspaceGrid") as FrameworkElement)?.IsVisible == true,
                "The workspace remained blocked after session confirmation.");
            await viewModel.DisconnectCommand.ExecuteAsync();
            await SettleLayoutAsync(window);
            Require(!viewModel.HasActiveSession && viewModel.ActiveConnection is null && session.DisposeCalls == 1 && sessionEvents == 2,
                "Disconnecting did not dispose the confirmed session exactly once.");
            RequireConnectionLanding(window);
            Require(viewModel.Connections.Single() == profile && store.SaveCalls == 0,
                "Connecting or disconnecting changed saved profiles.");

            service.ConnectHandler = (_, _) => Task.FromException<IRemoteConnectionSession>(new IOException("QA-controlled connection failure."));
            await viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(!viewModel.HasActiveSession && !viewModel.IsConnecting && !string.IsNullOrWhiteSpace(viewModel.ConnectionError),
                "A connection failure did not return to disconnected state with an inline error.");
            RequireConnectionLanding(window);
            var disconnectedSession = new QaRemoteConnectionSession(profile, isConnected: false);
            service.ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(disconnectedSession);
            await viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(!viewModel.HasActiveSession && disconnectedSession.DisposeCalls == 1,
                "A disconnected session was accepted or not disposed.");

            var lateCompletion = new TaskCompletionSource<IRemoteConnectionSession>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.ConnectHandler = (_, _) => lateCompletion.Task; // Deliberately ignores cancellation.
            var pending = viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(viewModel.IsConnecting && viewModel.CancelConnectionCommand.CanExecute(null),
                "A pending attempt did not enable cancellation.");
            viewModel.CancelConnectionCommand.Execute(null);
            await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!viewModel.IsConnecting && !viewModel.HasActiveSession,
                "Cancellation waited indefinitely for the adapter or retained an active session.");
            var lateSession = new QaRemoteConnectionSession(profile);
            lateCompletion.SetResult(lateSession);
            await lateSession.Disposed.WaitAsync(TimeSpan.FromSeconds(3));
            Require(lateSession.DisposeCalls == 1 && !viewModel.HasActiveSession && sessionEvents == 2,
                "A late session after cancellation was activated or not disposed.");
            RequireConnectionLanding(window);
            Require(store.SaveCalls == 0 && interactions.Messages.Count == 0,
                "Failure or cancellation changed profiles or opened modal dialogs.");
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
        }
    }

    private static void RequireConnectionLanding(MainWindow window)
    {
        Require((window.FindName("ConnectionsPage") as FrameworkElement)?.IsVisible == true,
            "The connection center is not visible without an active session.");
        foreach (var name in new[] { "HomePage", "MainWorkspaceGrid", "TerminalPage", "TransfersPage", "AutomationsPage" })
        {
            Require((window.FindName(name) as FrameworkElement)?.IsVisible == false,
                $"The protected page {name} was visible without a confirmed session.");
        }
    }

    private async Task VerifyConnectionLossAndShutdownAsync()
    {
        var profile = CreateProfile();
        var secondProfile = profile with { Id = Guid.NewGuid(), Host = "second.example.invalid" };
        var service = new QaRemoteConnectionService();
        var viewModel = CreateViewModel(new MemoryProfileStore([profile, secondProfile]), new RecordingInteractions(), service);
        await viewModel.InitializeAsync();
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        PrepareHiddenWindow(window);
        window.Show();
        try
        {
            var first = new QaRemoteConnectionSession(profile);
            service.ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(first);
            await viewModel.ConnectCommand.ExecuteAsync(profile);
            viewModel.SelectedConnection = secondProfile;
            Require(viewModel.ActiveConnection == profile, "Selecting another profile redirected the active session.");
            await Task.Run(first.Drop);
            await SettleLayoutAsync(window);
            Require(!viewModel.HasActiveSession && first.DisposeCalls == 1, "A remote drop did not retire the active session exactly once.");
            RequireConnectionLanding(window);
            var second = new QaRemoteConnectionSession(secondProfile);
            service.ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(second);
            await viewModel.ConnectCommand.ExecuteAsync(secondProfile);
            await Task.Run(first.SignalDisconnected);
            await SettleLayoutAsync(window);
            Require(viewModel.HasActiveSession && viewModel.ActiveConnection == secondProfile && second.DisposeCalls == 0,
                "An old session callback disconnected a replacement session.");
            await viewModel.ShutdownAsync();
            Require(!viewModel.HasActiveSession && second.DisposeCalls == 1, "Shutdown did not dispose the active session.");
            Require(!viewModel.ConnectCommand.CanExecute(profile), "Shutdown allowed another connection attempt.");
        }
        finally
        {
            await viewModel.ShutdownAsync();
            window.Close();
        }

        var completion = new TaskCompletionSource<IRemoteConnectionSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingService = new QaRemoteConnectionService { ConnectHandler = (_, _) => completion.Task };
        var pendingViewModel = CreateViewModel(new MemoryProfileStore([profile]), new RecordingInteractions(), pendingService);
        await pendingViewModel.InitializeAsync();
        var pendingConnection = pendingViewModel.ConnectCommand.ExecuteAsync(profile);
        var shuttingDown = pendingViewModel.ShutdownAsync();
        await pendingConnection.WaitAsync(TimeSpan.FromSeconds(3));
        Require(!shuttingDown.IsCompleted, "Shutdown finished before an abandoned transport was retired.");
        var late = new QaRemoteConnectionSession(profile);
        completion.SetResult(late);
        await shuttingDown.WaitAsync(TimeSpan.FromSeconds(3));
        await late.Disposed.WaitAsync(TimeSpan.FromSeconds(3));
        Require(!pendingViewModel.HasActiveSession && late.DisposeCalls == 1, "Shutdown accepted or leaked a late connection result.");

        var transportCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retiringService = new QaRemoteConnectionService { ShutdownHandler = () => transportCleanup.Task };
        var retiringViewModel = CreateViewModel(new MemoryProfileStore(), new RecordingInteractions(), retiringService);
        await retiringViewModel.InitializeAsync();
        var retirement = retiringViewModel.ShutdownAsync();
        Require(retiringService.ShutdownCalls == 1 && !retirement.IsCompleted,
            "Application shutdown did not await the adapter's outstanding transport cleanup.");
        transportCleanup.SetResult();
        await retirement.WaitAsync(TimeSpan.FromSeconds(3));
        await retiringViewModel.ShutdownAsync();
        Require(retiringService.ShutdownCalls == 1, "Repeated application shutdown invoked transport cleanup more than once.");
    }

    private async Task VerifyEnglishConnectionDiagnosticsAsync()
    {
        var profile = CreateProfile();
        var logs = new List<string>();
        var service = new QaRemoteConnectionService();
        var viewModel = new MainViewModel(new MemoryProfileStore([profile]), new RestrictedLocalFileService(outputDirectory),
            new RecordingInteractions(), service, (kind, message) => logs.Add($"{kind}: {message}"));
        await viewModel.InitializeAsync();
        await viewModel.ConnectCommand.ExecuteAsync(profile);
        Require(viewModel.StatusMessage.StartsWith("Connected to", StringComparison.Ordinal), "Successful connection status is not in English.");
        await viewModel.DisconnectCommand.ExecuteAsync();
        Require(viewModel.StatusMessage.StartsWith("Disconnected.", StringComparison.Ordinal), "Disconnection status is not in English.");
        foreach (var failure in new[]
        {
            new RemoteConnectionException(RemoteConnectionErrorCode.AuthenticationFailed, "SSH authentication failed. Check the username and credentials."),
            new RemoteConnectionException(RemoteConnectionErrorCode.Timeout, "The SSH connection timed out. Check the host and port.")
        })
        {
            service.ConnectHandler = (_, _) => Task.FromException<IRemoteConnectionSession>(failure);
            await viewModel.ConnectCommand.ExecuteAsync(profile);
            Require(viewModel.ConnectionError == failure.Message, "The connection did not retain a safe English error.");
        }
        const string secretSentinel = "QA_SECRET_MUST_NOT_APPEAR";
        service.ConnectHandler = (_, _) => Task.FromException<IRemoteConnectionSession>(new IOException(secretSentinel));
        await viewModel.ConnectCommand.ExecuteAsync(profile);
        Require(!viewModel.ConnectionError.Contains(secretSentinel, StringComparison.Ordinal), "An upstream exception exposed secret diagnostic content.");
        var completion = new TaskCompletionSource<IRemoteConnectionSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConnectHandler = (_, _) => completion.Task;
        var pending = viewModel.ConnectCommand.ExecuteAsync(profile);
        viewModel.CancelConnectionCommand.Execute(null);
        await pending;
        Require(viewModel.StatusMessage == "Connection canceled.", "Cancellation status is not in English.");
        completion.SetCanceled();
        Require(logs.All(message => !message.Contains(secretSentinel, StringComparison.Ordinal)
            && !message.Contains("Conectando", StringComparison.Ordinal) && !message.Contains("Desconectado", StringComparison.Ordinal)
            && !message.Contains("cancelada", StringComparison.Ordinal)), "Connection diagnostics leaked a secret or Portuguese status text.");
        Require(logs.Any(message => message.Contains("Connected to", StringComparison.Ordinal))
            && logs.Any(message => message.Contains("authentication failed", StringComparison.Ordinal))
            && logs.Any(message => message.Contains("timed out", StringComparison.Ordinal)), "Expected success and failure diagnostics were not recorded.");
        await viewModel.ShutdownAsync();
    }

    private async Task VerifySshDialogsAsync()
    {
        var profile = CreateProfile() with { PrivateKeyPath = null };
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        {
            ThemeService.Current.Apply(theme);
            var suffix = theme == AppTheme.Light ? "light" : "dark";
            var credentials = new SshCredentialsWindow(profile, allowRemember: true);
            PrepareHiddenWindow(credentials);
            credentials.Show();
            await SettleLayoutAsync(credentials);
            Require(((CheckBox)credentials.FindName("RememberCheckBox")).IsEnabled, "Saved profiles cannot opt into credential storage.");
            Require(credentials.FindName("SecretBox") is PasswordBox, "The secret field is not a native PasswordBox.");
            Capture(credentials, $"ssh-credentials-{suffix}.png");
            credentials.Close();
            var host = new HostKeyVerificationWindow(new SshHostIdentity("qa.example.invalid", 2222, "ssh-ed25519",
                "SHA256:QaFixtureFingerprintForVisualVerificationOnly123"));
            PrepareHiddenWindow(host);
            host.Show();
            await SettleLayoutAsync(host);
            Require(((TextBox)host.FindName("FingerprintTextBox")).IsReadOnly, "The host fingerprint can be edited in the trust dialog.");
            Require(host.Decision == HostKeyDecision.Reject, "The host dialog trusts a server by default.");
            Capture(host, $"ssh-host-identity-{suffix}.png");
            host.Close();
        }
        ThemeService.Current.Apply(AppTheme.Light);
        var manual = new SshCredentialsWindow(profile, allowRemember: false);
        PrepareHiddenWindow(manual);
        Exception? failure = null;
        manual.Loaded += (_, _) => application.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var remember = (CheckBox)manual.FindName("RememberCheckBox");
                Require(!remember.IsEnabled, "Manual credentials offer persistent storage.");
                remember.IsChecked = true; // A programmatic change must not bypass the restriction.
                ((PasswordBox)manual.FindName("SecretBox")).Password = "fixture-only";
                var connect = FindVisualChild<Button>(manual, button => Equals(button.Content, "Connect"))
                    ?? throw new InvalidOperationException("The credential Connect button could not be found.");
                connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception exception) { failure = exception; manual.Close(); }
        });
        Require(manual.ShowDialog() == true, "The manual credential dialog did not complete.");
        if (failure is not null) throw failure;
        using var result = manual.Credentials;
        Require(result is { Remember: false, Method: RemoteAuthenticationMethod.Password }, "Manual credentials enabled storage or used the wrong authentication method.");
        Require(((PasswordBox)manual.FindName("SecretBox")).SecurePassword.Length == 0, "A closed credential dialog retained the entered secret.");
    }

    private async Task VerifyPromptCancellationAsync()
    {
        // Keep native provider-created dialogs off the user's desktop during this QA check.
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is SshCredentialsWindow or HostKeyVerificationWindow or OverwriteConfirmationWindow) PrepareHiddenWindow((Window)sender);
        }));
        var provider = new DesktopSshInteractionService(new WindowsCredentialStore($"OhMySsh.Verify.{Guid.NewGuid():N}"),
            _ => false, application.Dispatcher);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            await ExpectExceptionAsync<OperationCanceledException>(async () => await provider.GetCredentialsAsync(CreateProfile(), cancellation.Token));
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            await ExpectExceptionAsync<OperationCanceledException>(async () => await provider.ConfirmAsync(
                new SshHostIdentity("qa.example.invalid", 22, "ssh-ed25519", "SHA256:fixture"), cancellation.Token));
        Require(!application.Windows.Cast<Window>().Any(window => window is SshCredentialsWindow or HostKeyVerificationWindow),
            "Cancellation left a native SSH prompt open.");
    }

    private async Task CaptureConnectedWorkspaceAsync()
    {
        var profiles = new[] { CreateProfile(), CreateProfile() with
        {
            Id = Guid.NewGuid(), Name = "Arquivos de QA", Host = "files.example.invalid", Username = "backup", Group = "Arquivos",
            Protocol = ConnectionProtocol.Ftps, Port = 21, IsFavorite = false, PrivateKeyPath = null
        } };
        var viewModel = CreateViewModel(new MemoryProfileStore(profiles), new RecordingInteractions(), new QaRemoteConnectionService());
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        viewModel.SelectedConnection = viewModel.Connections.Single(profile => profile.Host == "edge.example.invalid");

        ThemeService.Current.Apply(AppTheme.Light);
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            await viewModel.ConnectCommand.ExecuteAsync();
            Require(viewModel.HasActiveSession, "The connected screenshot did not receive a confirmed QA session.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                window.NavigateTo("home");
                await SettleLayoutAsync(window);
                var themeName = theme == AppTheme.Light ? "light" : "dark";
                Capture(window, $"connected-home-{themeName}.png");
                window.NavigateTo("files");
                await SettleLayoutAsync(window);
                Capture(window, $"connected-workspace-{themeName}.png");
                var files = window.FindName("LocalFilesGrid") as DataGrid
                    ?? throw new InvalidOperationException("The local files grid could not be found.");
                Require(!files.AutoGenerateColumns && files.Columns.Count == 3,
                    $"Switching to {theme} changed the three local file columns.");
            }
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private async Task VerifyConnectionEditorAsync()
    {
        var editor = new ConnectionEditorWindow();
        PrepareHiddenWindow(editor);
        Exception? validationFailure = null;
        editor.Loaded += (_, _) => application.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var name = (TextBox)editor.FindName("NameTextBox");
                var host = (TextBox)editor.FindName("HostTextBox");
                var username = (TextBox)editor.FindName("UsernameTextBox");
                var save = (Button)editor.FindName("SaveButton");
                var validation = (FrameworkElement)editor.FindName("ValidationPanel");
                name.Text = "Perfil de validação QA";
                host.Text = "ssh://edge.example.invalid";
                username.Text = "deploy";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(editor.Profile is null && validation.Visibility == Visibility.Visible, "The editor accepted a host containing a protocol or path.");
                host.Text = "edge.example.invalid";
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception exception)
            {
                validationFailure = exception;
                editor.Close();
            }
        });
        var result = editor.ShowDialog();
        if (validationFailure is not null)
        {
            throw validationFailure;
        }

        Require(result == true && editor.Profile?.Host == "edge.example.invalid", "The editor did not return the validated profile.");
        Require(editor.Profile?.Group == "VPS", "A new VPS did not receive the default VPS group.");
        await Task.CompletedTask;
    }

    private async Task CaptureConnectionsPageAsync(bool withProfiles)
    {
        var storagePath = Path.Combine(outputDirectory, withProfiles ? "vps-saved" : "vps-empty", "connections.json");
        var interactions = new RecordingInteractions();
        var viewModel = CreateViewModel(new JsonConnectionProfileStore(storagePath), interactions);
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        ThemeService.Current.Apply(AppTheme.Light);
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            if (withProfiles)
            {
                // Save through the same command the home/editor flow uses;
                // the shell should reveal the saved VPS page on ProfileSaved.
                window.NavigateTo("home");
                interactions.EditResults.Enqueue(CreateProfile() with { Group = "VPS" });
                await viewModel.AddConnectionCommand.ExecuteAsync();
                interactions.EditResults.Enqueue(CreateProfile() with
                {
                    Id = Guid.NewGuid(), Name = "Backup · VPS de QA", Host = "backup.example.invalid",
                    Username = "backup", Group = "VPS", Port = 2222, IsFavorite = false, PrivateKeyPath = null
                });
                await viewModel.AddConnectionCommand.ExecuteAsync();
            }
            else
            {
                window.NavigateTo("connections");
            }

            var connectionsPage = window.FindName("ConnectionsPage") as FrameworkElement
                ?? throw new InvalidOperationException("The saved VPS page could not be found.");
            var profilesList = window.FindName("SavedProfilesList") as ListBox
                ?? throw new InvalidOperationException("The saved VPS list could not be found.");
            var workspace = window.FindName("MainWorkspaceGrid") as FrameworkElement
                ?? throw new InvalidOperationException("The file workspace could not be found.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                await SettleLayoutAsync(window);
                Require(connectionsPage.IsVisible && !workspace.IsVisible,
                    "The VPS page did not become visible after navigation or saving a profile.");
                Require(profilesList.Items.Count == (withProfiles ? 2 : 0),
                    "The VPS list does not match the profiles loaded by the fixture.");
                var themeName = theme == AppTheme.Light ? "light" : "dark";
                Capture(window, $"connection-center-{(withProfiles ? "saved" : "empty")}-{themeName}.png");
            }

            if (!withProfiles && FindVisualChild<Expander>(window, item => item.Name == "QuickOptionsExpander") is { } advanced)
            {
                advanced.IsExpanded = true;
                // Let the native Fluent expansion animation reveal its content
                // before the optional visual snapshot is rendered.
                await Task.Delay(250);
                await SettleLayoutAsync(window);
                Capture(window, "connection-center-advanced-dark.png");
                advanced.IsExpanded = false;
            }
        }
        finally
        {
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private async Task VerifyMinimumLayoutAsync()
    {
        ThemeService.Current.Apply(AppTheme.Light);
        var viewModel = CreateViewModel(new MemoryProfileStore([CreateProfile()]), new RecordingInteractions(), new QaRemoteConnectionService());
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        var window = new MainWindow(viewModel, initializeOnLoad: false)
        {
            Width = 1180,
            Height = 800
        };
        try
        {
            PrepareHiddenWindow(window);
            var navigation = window.FindName("NavigationLayout") as Grid
                ?? throw new InvalidOperationException("The navigation layout could not be found.");
            navigation.ColumnDefinitions[0].Width = new GridLength(300);
            window.Show();
            window.NavigateTo("connections");
            await SettleLayoutAsync(window);
            Capture(window, "minimum-connection-center.png");
            var hostBox = FindVisualChild<TextBox>(window, item => item.Name == "QuickHostBox")
                ?? throw new InvalidOperationException("The manual connection host field could not be found.");
            RequireVisibleWithinWindow(window, hostBox, "manual connection host");
            foreach (var name in new[] { "QuickSaveButton", "QuickConnectButton" })
            {
                var action = FindVisualChild<Button>(window, item => item.Name == name)
                    ?? throw new InvalidOperationException($"The manual action {name} could not be found.");
                RequireVisibleWithinWindow(window, action, name);
            }

            await viewModel.ConnectCommand.ExecuteAsync();
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            Capture(window, "minimum-connected-workspace.png");
            foreach (var name in new[] { "RemoteFilesPanel", "TransferQueuePanel" })
            {
                var panel = window.FindName(name) as FrameworkElement
                    ?? throw new InvalidOperationException($"The {name} panel could not be found.");
                RequireVisibleWithinWindow(window, panel, name);
            }
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
        }
    }

    private async Task CaptureEditorAsync()
    {
        ThemeService.Current.Apply(AppTheme.Light);
        var editor = new ConnectionEditorWindow(CreateProfile());
        try
        {
            PrepareHiddenWindow(editor);
            editor.Show();
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                await SettleLayoutAsync(editor);
                Capture(editor, theme == AppTheme.Light ? "connection-editor-light.png" : "connection-editor-dark.png");
            }
        }
        finally
        {
            editor.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private async Task VerifyThemeNavigationStateAsync()
    {
        var profile = CreateProfile();
        var second = profile with { Id = Guid.NewGuid(), Name = "Backup de QA", Host = "backup.example.invalid", IsFavorite = false };
        var store = new MemoryProfileStore([profile, second]);
        var viewModel = CreateViewModel(store, new RecordingInteractions(), new QaRemoteConnectionService());
        await viewModel.InitializeAsync();
        await viewModel.NavigateToAsync(_workspace);
        await viewModel.ConnectCommand.ExecuteAsync(profile);
        viewModel.SelectedConnection = viewModel.Connections.Single(item => item.Id == second.Id);
        var profilesBefore = viewModel.Connections.ToArray();
        var filesBefore = viewModel.LocalEntries.ToArray();
        var selectedBefore = viewModel.SelectedConnection;
        var pathBefore = viewModel.LocalPath;
        var paletteColors = new List<Color>();
        ThemeService.Current.Apply(AppTheme.Light);
        var window = new MainWindow(viewModel, initializeOnLoad: false);
        try
        {
            PrepareHiddenWindow(window);
            window.Show();
            var workspace = window.FindName("MainWorkspaceGrid") as FrameworkElement
                ?? throw new InvalidOperationException("The workspace could not be found.");
            var connectionsPage = window.FindName("ConnectionsPage") as FrameworkElement
                ?? throw new InvalidOperationException("The saved VPS page could not be found.");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                Require(ThemeService.Current.Preference == theme && ThemeService.Current.IsDark == (theme == AppTheme.Dark),
                    "The applied theme does not match the selected preference.");
                var background = application.TryFindResource("AppBackgroundBrush") as SolidColorBrush
                    ?? throw new InvalidOperationException("The palette did not define an application background.");
                paletteColors.Add(background.Color);
                foreach (var page in new[] { "home", "connections", "files", "terminal", "transfers", "automations", "settings" })
                {
                    window.NavigateTo(page);
                    await SettleLayoutAsync(window);
                    Require(ThemeService.Current.Preference == theme,
                        $"Page {page} changed theme preference {theme}.");
                    Require(ReferenceEquals(window.DataContext, viewModel), "Navigation or theme switching replaced the view model.");
                    Require(viewModel.Connections.SequenceEqual(profilesBefore) && viewModel.SelectedConnection == selectedBefore,
                        $"Page {page} changed profiles or selection in theme {theme}.");
                    Require(viewModel.LocalPath == pathBefore && viewModel.LocalEntries.SequenceEqual(filesBefore),
                        $"Page {page} changed the path or files in theme {theme}.");
                    Require(workspace.IsVisible == (page == "files"), $"Navigation to {page} did not update workspace visibility.");
                    Require(connectionsPage.IsVisible == (page == "connections"), $"Navigation to {page} did not update saved VPS visibility.");

                    if (page == "settings")
                    {
                        Capture(window, theme == AppTheme.Light ? "main-settings-light.png" : "main-settings-dark.png");
                    }
                }
            }

            Require(paletteColors.Distinct().Count() == 2, "Light and dark themes used the same background color.");
            Require(store.SaveCalls == 0, "Navigation or theme switching saved connection data.");
        }
        finally
        {
            await viewModel.DisconnectCommand.ExecuteAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
        }
    }

    private MainViewModel CreateViewModel(IConnectionProfileStore store, RecordingInteractions interactions,
        IRemoteConnectionService? remoteConnections = null)
        => new(store, new RestrictedLocalFileService(outputDirectory), interactions, remoteConnections);

    private static ConnectionProfile CreateProfile() => new()
    {
        Name = "Edge · homologação QA", Host = "edge.example.invalid", Username = "deploy", Group = "Homologação",
        Protocol = ConnectionProtocol.Ssh, Port = 22, IsFavorite = true, PrivateKeyPath = @"C:\QA\keys\deploy_ed25519"
    };

    private async Task CheckAsync(string name, Func<Task> check)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await check();
            _checks.Add(new CheckResult(name, true, stopwatch.ElapsedMilliseconds, null));
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            _checks.Add(new CheckResult(name, false, stopwatch.ElapsedMilliseconds, exception.ToString()));
            Console.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static void PrepareHiddenWindow(Window window)
    {
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
    }

    private static async Task SettleLayoutAsync(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Render);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, Predicate<T> match) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T candidate && match(candidate))
            {
                return candidate;
            }

            if (FindVisualChild(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void RequireVisibleWithinWindow(Window window, FrameworkElement element, string name)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
        Require(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0,
            $"Element {name} is not visible at minimum size.");
        Require(bounds.Left >= -1 && bounds.Top >= -1
            && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1,
            $"Element {name} exceeds the minimum window bounds: {bounds}.");
    }

    private void Capture(Window window, string filename)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        Require(width > 0 && height > 0, "The window did not receive a valid size.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(outputDirectory, filename);
        using var stream = File.Create(path);
        encoder.Save(stream);
        _screenshots.Add(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ExpectExceptionAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected an exception of type {typeof(TException).Name}.");
    }
}

internal sealed record CheckResult(string Name, bool Passed, long DurationMs, string? Error);

internal sealed class BindingErrorListener : TraceListener
{
    public List<string> Messages { get; } = [];
    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Messages.Add(message);
        }
    }

    public override void WriteLine(string? message) => Write(message);
}

internal sealed class MemoryProfileStore(IEnumerable<ConnectionProfile>? profiles = null) : IConnectionProfileStore
{
    public List<ConnectionProfile> Profiles { get; private set; } = profiles?.ToList() ?? [];
    public bool FailSave { get; set; }
    public int SaveCalls { get; private set; }
    public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ConnectionProfile>>(Profiles.ToArray());

    public Task SaveAsync(IReadOnlyCollection<ConnectionProfile> profilesToSave, CancellationToken cancellationToken = default)
    {
        SaveCalls++;
        if (FailSave)
        {
            return Task.FromException(new IOException("A write failure simulated by verification."));
        }

        Profiles = profilesToSave.ToList();
        return Task.CompletedTask;
    }
}

/// <summary>A QA-only transport seam. It never opens sockets or starts a process.</summary>
internal sealed class QaRemoteConnectionService : IRemoteConnectionService
{
    public List<ConnectionProfile> Requests { get; } = [];
    public Func<ConnectionProfile, CancellationToken, Task<IRemoteConnectionSession>>? ConnectHandler { get; set; }
    public Func<Task>? ShutdownHandler { get; set; }
    public int ShutdownCalls { get; private set; }

    public Task ShutdownAsync()
    {
        ShutdownCalls++;
        return ShutdownHandler?.Invoke() ?? Task.CompletedTask;
    }

    public Task<IRemoteConnectionSession> ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        Requests.Add(profile);
        cancellationToken.ThrowIfCancellationRequested();
        return ConnectHandler?.Invoke(profile, cancellationToken)
            ?? Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(profile));
    }
}

/// <summary>Records session ownership and disposal without any remote resource.</summary>
internal sealed class QaRemoteConnectionSession(ConnectionProfile profile, bool isConnected = true) : IRemoteConnectionSession
{
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _isConnected = isConnected ? 1 : 0;
    private int _disposeCalls;

    public ConnectionProfile Profile { get; } = profile;
    public IRemoteFileService? Files { get; init; }
    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);
    public Task Disposed => _disposed.Task;
    public event EventHandler? Disconnected;

    public void Drop()
    {
        Interlocked.Exchange(ref _isConnected, 0);
        SignalDisconnected();
    }

    public void SignalDisconnected() => Disconnected?.Invoke(this, EventArgs.Empty);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        Interlocked.Exchange(ref _isConnected, 0);
        _disposed.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingInteractions : IDesktopInteractionService
{
    public Queue<ConnectionProfile?> EditResults { get; } = new();
    public List<string> Messages { get; } = [];
    public string? ClipboardText { get; private set; }
    public int EditCalls { get; private set; }
    public bool AllowDelete { get; set; } = true;
    public int ConfirmDeleteCalls { get; private set; }
    public Queue<string?> UploadFiles { get; } = new();
    public Queue<string?> DownloadFiles { get; } = new();
    public bool AllowOverwrite { get; set; }
    public int OverwriteCalls { get; private set; }
    public Func<string, bool, CancellationToken, Task<bool>>? OverwriteHandler { get; set; }
    public ConnectionProfile? EditConnection(ConnectionProfile? existing)
    {
        EditCalls++;
        return EditResults.TryDequeue(out var profile) ? profile : null;
    }

    public bool ConfirmDelete(ConnectionProfile profile)
    {
        ConfirmDeleteCalls++;
        return AllowDelete;
    }
    public string? PickLocalFolder(string initialPath) => null;
    public string? PickUploadFile(string initialDirectory) => UploadFiles.TryDequeue(out var path) ? path : null;
    public string? PickDownloadFile(string suggestedFileName, string initialDirectory) => DownloadFiles.TryDequeue(out var path) ? path : null;
    public Task<bool> ConfirmOverwriteAsync(string destination, bool isRemote, CancellationToken cancellationToken = default)
    {
        OverwriteCalls++;
        return OverwriteHandler?.Invoke(destination, isRemote, cancellationToken) ?? Task.FromResult(AllowOverwrite);
    }
    public void ShowInformation(string title, string message) => Messages.Add($"{title}: {message}");
    public void CopyToClipboard(string text) => ClipboardText = text;
}

/// <summary>Prevents even the initial listing from inspecting the user's folders.</summary>
internal sealed class RestrictedLocalFileService(string allowedRoot) : ILocalFileService
{
    private readonly LocalFileService _inner = new();
    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string path, CancellationToken cancellationToken = default)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(allowedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? _inner.GetEntriesAsync(path, cancellationToken)
            : Task.FromResult<IReadOnlyList<FileEntry>>([]);
}

internal sealed class DelayedLocalFileService(string delayedPath) : ILocalFileService
{
    public TaskCompletionSource<IReadOnlyList<FileEntry>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> RequestedPaths { get; } = [];
    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string path, CancellationToken cancellationToken = default)
    {
        RequestedPaths.Add(path);
        return path == delayedPath ? Completion.Task : Task.FromResult<IReadOnlyList<FileEntry>>([]);
    }
}
