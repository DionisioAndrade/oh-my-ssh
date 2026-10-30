using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
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
    private async Task VerifyRemoteBrowsingAsync()
    {
        var profile = CreateProfile();
        var files = new QaRemoteFiles();
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile]), new RecordingInteractions(), service);
        await vm.InitializeAsync();
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        Require(vm.RemotePath == "/home/qa" && vm.RemoteEntries.Any(x => x.Name == "QA résumé 空.txt"),
            "A confirmed session did not initialize the remote browser.");
        var delayed = new TaskCompletionSource<IReadOnlyList<FileEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        files.ListHandler = (path, _) => path switch
        {
            "/slow" => delayed.Task,
            "/missing" => Task.FromException<IReadOnlyList<FileEntry>>(new RemoteFileException(RemoteFileErrorCode.NotFound, "The remote directory was not found.")),
            _ => Task.FromResult<IReadOnlyList<FileEntry>>([QaRemoteFiles.Entry("latest.txt", path + "/latest.txt")])
        };
        var slowNavigation = vm.NavigateRemoteToAsync("/slow");
        await vm.NavigateRemoteToAsync("/latest");
        delayed.SetResult([QaRemoteFiles.Entry("stale.txt", "/slow/stale.txt")]);
        await slowNavigation;
        Require(vm.RemotePath == "/latest" && vm.RemoteEntries.Single().Name == "latest.txt", "An old listing replaced newer navigation.");
        await vm.NavigateRemoteToAsync("/missing");
        Require(vm.RemoteEntries.Single().Name == "latest.txt" && vm.HasRemoteError, "A failed navigation erased the previous listing.");

        var staleSessionListing = new TaskCompletionSource<IReadOnlyList<FileEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        files.ListHandler = (_, _) => staleSessionListing.Task;
        var oldSessionNavigation = vm.NavigateRemoteToAsync("/old-session");
        await vm.DisconnectCommand.ExecuteAsync();
        var replacementFiles = new QaRemoteFiles();
        service.ConnectHandler = (snapshot, _) => Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = replacementFiles });
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        staleSessionListing.SetResult([QaRemoteFiles.Entry("old-session.txt", "/old-session/old-session.txt")]);
        await oldSessionNavigation;
        Require(vm.RemoteEntries.Any(x => x.Name == "QA résumé 空.txt") && vm.RemoteEntries.All(x => x.Name != "old-session.txt"),
            "A retired session updated the replacement remote browser.");
        await vm.ShutdownAsync();
    }

    private async Task VerifyTransferQueueAsync()
    {
        var profile = CreateProfile();
        var other = profile with { Id = Guid.NewGuid(), Host = "other.example.invalid" };
        var files = new QaRemoteFiles();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.UploadHandler = async (_, _, progress, token) =>
        {
            progress?.Report(new TransferProgress(100, 100));
            await release.Task.WaitAsync(token);
        };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile, other]), new RecordingInteractions(), service);
        await vm.InitializeAsync();
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
        await vm.UploadCommand.ExecuteAsync();
        await WaitUntilAsync(() => vm.TransferJobs.Count == 1 && vm.TransferJobs[0].State == TransferState.Running);
        await Task.Delay(150);
        Require(vm.TransferJobs[0].ProgressPercent < 100, "Byte progress marked completion before destination finalization.");
        vm.SelectedConnection = other;
        await vm.UploadCommand.ExecuteAsync();
        Require(vm.TransferJobs.Count == 2 && vm.TransferJobs[1].State == TransferState.Queued && files.UploadCalls == 1,
            "The queue started concurrent transfers.");
        Require(vm.TransferJobs.All(job => job.Profile.Id == profile.Id && job.Profile.Host == profile.Host),
            "Changing the selected profile redirected queued work.");
        vm.CancelTransferCommand.Execute(vm.TransferJobs[1]);
        var navigationListing = new TaskCompletionSource<IReadOnlyList<FileEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        files.ListHandler = (path, _) => path == "/user-selected" ? navigationListing.Task : Task.FromResult(files.Entries);
        var navigation = vm.NavigateRemoteToAsync("/user-selected");
        release.SetResult();
        await vm.WaitForTransfersAsync();
        Require(vm.TransferJobs[0].State == TransferState.Completed && vm.TransferJobs[1].State == TransferState.Canceled
            && files.UploadCalls == 1, "Queued cancellation executed work or prevented the active transfer from finishing.");
        Require(vm.TransferJobs[0].ProgressPercent == 100, "Finalized transfer did not reach complete progress.");
        navigationListing.SetResult([QaRemoteFiles.Entry("selected-directory.txt", "/user-selected/selected-directory.txt")]);
        await navigation;
        Require(vm.RemotePath == "/user-selected" && vm.RemoteEntries.Single().Name == "selected-directory.txt",
            "Upload completion refresh canceled the user's newer pending navigation.");

        files.UploadHandler = async (_, _, progress, token) =>
        {
            progress?.Report(new TransferProgress(100, 100));
            await Task.Delay(Timeout.Infinite, token);
        };
        await vm.UploadCommand.ExecuteAsync();
        var waitingForFlush = vm.TransferJobs.Last();
        await WaitUntilAsync(() => waitingForFlush.ProgressPercent >= 99.8);
        Require(waitingForFlush.CanCancel && vm.CancelTransferCommand.CanExecute(waitingForFlush),
            "Transferring all bytes disabled cancellation before the actual commit boundary.");
        vm.CancelTransferCommand.Execute(waitingForFlush);
        await vm.WaitForTransfersAsync();
        Require(waitingForFlush.State == TransferState.Canceled && waitingForFlush.ProgressPercent < 100,
            "Cancellation before destination finalization appeared as a successful transfer.");
        await vm.ShutdownAsync();
    }

    private async Task VerifyTransferOverwriteAsync()
    {
        var profile = CreateProfile();
        var files = new QaRemoteFiles { ExistsHandler = (_, _) => Task.FromResult(true) };
        var interactions = new RecordingInteractions { AllowOverwrite = false };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile]), interactions, service);
        await vm.InitializeAsync();
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
        await vm.UploadCommand.ExecuteAsync();
        await vm.WaitForTransfersAsync();
        Require(interactions.OverwriteCalls == 1 && files.UploadCalls == 0 && vm.TransferJobs.Single().State == TransferState.Canceled,
            "Declining remote overwrite still performed a transfer.");
        var target = Path.Combine(_workspace, "existing-download.txt");
        await File.WriteAllTextAsync(target, "existing local content");
        interactions.DownloadFiles.Enqueue(target);
        vm.SelectedRemoteEntry = vm.RemoteEntries.First(x => !x.IsDirectory);
        await vm.DownloadCommand.ExecuteAsync();
        await vm.WaitForTransfersAsync();
        Require(interactions.OverwriteCalls == 2 && files.DownloadCalls == 0 && await File.ReadAllTextAsync(target) == "existing local content",
            "Declining local overwrite changed the destination.");

        var promptStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        interactions.OverwriteHandler = async (_, _, token) =>
        {
            promptStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return true;
        };
        await vm.UploadCommand.ExecuteAsync();
        await promptStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        vm.CancelTransferCommand.Execute(vm.TransferJobs.Last());
        await vm.WaitForTransfersAsync();
        Require(vm.TransferJobs.Last().State == TransferState.Canceled && files.UploadCalls == 0,
            "Canceling a pending overwrite prompt did not stop the job.");
        await vm.ShutdownAsync();

        var desktopInteractions = new DesktopInteractionService();
        using var dialogCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await ExpectExceptionAsync<OperationCanceledException>(async () =>
            await desktopInteractions.ConfirmOverwriteAsync("/home/qa/QA existing.txt", true, dialogCancellation.Token));
        Require(!application.Windows.OfType<OhMySsh.Desktop.Views.OverwriteConfirmationWindow>().Any(window => window.IsVisible),
            "Cancellation left the native overwrite confirmation open.");
    }

    private async Task VerifyTransferDisconnectionAsync()
    {
        var profile = CreateProfile();
        var files = new QaRemoteFiles { UploadHandler = (_, _, _, token) => Task.Delay(Timeout.Infinite, token) };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile]), new RecordingInteractions(), service);
        await vm.InitializeAsync();
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
        await vm.UploadCommand.ExecuteAsync();
        await WaitUntilAsync(() => files.UploadCalls == 1);
        await vm.UploadCommand.ExecuteAsync();
        vm.CancelTransferCommand.Execute(vm.TransferJobs.First());
        await WaitUntilAsync(() => files.UploadCalls == 2);
        Require(vm.TransferJobs.First().State == TransferState.Canceled, "Running cancellation did not release the serial queue.");
        await vm.UploadCommand.ExecuteAsync();
        await vm.DisconnectCommand.ExecuteAsync();
        await vm.WaitForTransfersAsync();
        Require(!vm.HasActiveSession && vm.RemoteEntries.Count == 0 && vm.SelectedRemoteEntry is null,
            "Disconnect retained remote session state.");
        Require(vm.TransferJobs.All(x => x.State == TransferState.Canceled), "Disconnect left queued or running transfers active.");
        Require(!vm.UploadCommand.CanExecute(null) && !vm.DownloadCommand.CanExecute(null), "Transfer actions remained enabled without a session.");

        var droppedSession = new QaRemoteConnectionSession(profile) { Files = files };
        service.ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(droppedSession);
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        await vm.UploadCommand.ExecuteAsync();
        await vm.UploadCommand.ExecuteAsync();
        await Task.Run(droppedSession.Drop);
        await WaitUntilAsync(() => !vm.HasActiveSession && !vm.IsDisconnecting);
        await vm.WaitForTransfersAsync();
        Require(vm.RemoteEntries.Count == 0 && vm.TransferJobs.All(x => x.State == TransferState.Canceled),
            "Unexpected transport loss left remote files or pending transfer work active.");

        var shutdownSession = new QaRemoteConnectionSession(profile) { Files = files };
        service.ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(shutdownSession);
        await vm.ConnectCommand.ExecuteAsync(profile);
        await vm.RemoteInitialization;
        Require(vm.HasActiveSession && vm.UploadCommand.CanExecute(null), "A new file session was not ready after remote-drop cleanup.");
        await vm.UploadCommand.ExecuteAsync();
        await vm.ShutdownAsync();
        Require(shutdownSession.DisposeCalls == 1 && vm.TransferJobs.Last().State == TransferState.Canceled,
            $"Application shutdown did not cancel pending file work and dispose the owning session (disposals: {shutdownSession.DisposeCalls}, final state: {vm.TransferJobs.Last().State}).");
    }

    private async Task CaptureSftpWorkspaceAsync()
    {
        var originalCulture = UiText.Current.CultureName;
        var profile = CreateProfile() with { Name = "SFTP workspace · QA" };
        IProgress<TransferProgress>? visibleProgress = null;
        var files = new QaRemoteFiles();
        files.UploadHandler = async (_, _, progress, token) =>
        {
            if (files.UploadCalls == 1)
                throw new RemoteFileException(RemoteFileErrorCode.Failed, "The QA transfer failed before finalization.");
            visibleProgress = progress;
            progress?.Report(new TransferProgress(0, 256 * 1024));
            await Task.Delay(600, token);
            progress?.Report(new TransferProgress(64 * 1024, 256 * 1024));
            await Task.Delay(Timeout.Infinite, token);
        };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var captureInteractions = new RecordingInteractions();
        var vm = CreateViewModel(new MemoryProfileStore([profile]), captureInteractions, service);
        await vm.InitializeAsync();
        await vm.NavigateToAsync(_workspace);
        var window = new MainWindow(vm, initializeOnLoad: false) { Width = 1440, Height = 950 };
        PrepareHiddenWindow(window);
        window.Show();
        try
        {
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            var completedDestination = Path.Combine(_workspace, "result café, 空.txt");
            captureInteractions.DownloadFiles.Enqueue(completedDestination);
            files.DownloadHandler = async (_, destination, progress, _) =>
            {
                await File.WriteAllTextAsync(destination, "QA completed result");
                progress?.Report(new TransferProgress(19, 19, IsFinalizing: true));
            };
            vm.SelectedRemoteEntry = vm.RemoteEntries.First(entry => !entry.IsDirectory);
            await vm.DownloadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("manifest.json", Path.Combine(_workspace, "manifest.json"));
            await vm.UploadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            await vm.RefreshRemoteCommand.ExecuteAsync();
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
            await vm.UploadCommand.ExecuteAsync();
            var running = vm.TransferJobs.Last();
            await WaitUntilAsync(() => running.BytesPerSecond > 0);
            vm.ToggleTransferQueueCommand.Execute(null);
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("README.md", Path.Combine(_workspace, "README.md"));
            await vm.UploadCommand.ExecuteAsync();
            await VerifyPopulatedWorkspaceLayoutsAsync(window, vm);
            UiText.Current.SetCulture("pt-BR");
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                var suffix = theme == AppTheme.Light ? "light" : "dark";
                window.NavigateTo("files");
                await SettleLayoutAsync(window);
                Require(((DataGrid)window.FindName("RemoteFilesGrid")).Items.Count == files.Entries.Count,
                    "The remote file grid did not bind the loaded listing.");
                RequireQueueControlsVisible(window);
                FindVisualChild<DataGrid>(window, grid => grid.Name == "TransferJobsGrid" && grid.IsVisible)!.ScrollIntoView(running);
                await SettleLayoutAsync(window);
                Capture(window, $"sftp-workspace-{suffix}.png");
                window.NavigateTo("transfers");
                await SettleLayoutAsync(window);
                RequireQueueControlsVisible(window);
                Capture(window, $"sftp-queue-{suffix}.png");
                var completed = vm.TransferJobs.First(job => job.State == TransferState.Completed);
                var actions = FindVisualChild<Button>(window, button => button.IsVisible
                    && ReferenceEquals(button.DataContext, completed) && button.ContextMenu is not null)
                    ?? throw new InvalidOperationException("The completed-transfer action menu was missing.");
                actions.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleLayoutAsync(window);
                var menu = actions.ContextMenu!;
                try
                {
                    Require(menu.IsOpen && ReferenceEquals(menu.DataContext, completed),
                        "The detached completion popup lost its originating transfer row.");
                    var copy = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Copiar caminho remoto"));
                    var folder = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Abrir pasta local"));
                    var reveal = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Selecionar arquivo baixado"));
                    Require(ReferenceEquals(copy.Command, vm.CopyRemotePathCommand) && ReferenceEquals(copy.CommandParameter, completed)
                        && ReferenceEquals(folder.Command, vm.OpenTransferFolderCommand)
                        && ReferenceEquals(reveal.Command, vm.RevealDownloadedFileCommand) && reveal.IsVisible,
                        "Completion menu commands did not resolve across the popup presentation tree.");
                    copy.Command!.Execute(copy.CommandParameter);
                    Require(captureInteractions.ClipboardText == completed.RemotePath,
                        "The completion menu copied the current path instead of its transfer's remote path.");
                }
                finally { menu.IsOpen = false; }
                vm.SelectedTransferFilter = TransferFilter.Failed;
                await SettleLayoutAsync(window);
                Capture(window, $"transfer-filter-failed-{suffix}.png");
                vm.SelectedTransferFilter = TransferFilter.Completed;
                await SettleLayoutAsync(window);
                Capture(window, $"transfer-filter-completed-{suffix}.png");
                vm.SelectedTransferFilter = TransferFilter.All;
            }
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.NavigateTo("files");
            await SettleLayoutAsync(window);
            RequireVisibleWithinWindow(window, (FrameworkElement)window.FindName("RemoteFilesGrid"), "remote files");
            RequireQueueControlsVisible(window);
            FindVisualChild<DataGrid>(window, grid => grid.Name == "TransferJobsGrid" && grid.IsVisible)!.ScrollIntoView(running);
            await SettleLayoutAsync(window);
            Capture(window, "sftp-minimum-workspace.png");
            // The queued byte update runs at Background priority, after the Normal-priority
            // commit marker. It must never regress the displayed commit phase or byte count.
            visibleProgress!.Report(new TransferProgress(128 * 1024, 256 * 1024));
            visibleProgress!.Report(new TransferProgress(256 * 1024, 256 * 1024, IsFinalizing: true));
            await WaitUntilAsync(() => running.ProgressPercent >= 99.8);
            Require(!running.CanCancel, "The actual non-cancellable commit phase still offered cancellation.");
            ThemeService.Current.Apply(AppTheme.Light);
            await SettleLayoutAsync(window);
            Require(FindVisualChild<TextBlock>(window, text => text.Text is "99.9%" or "99,9%") is not null,
                "A transfer awaiting finalization displayed rounded 100% completion.");
            Capture(window, "sftp-finalizing-light.png");
        }
        finally
        {
            await vm.ShutdownAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            UiText.Current.SetCulture(originalCulture);
        }
    }

    private async Task VerifyPopulatedWorkspaceLayoutsAsync(MainWindow window, MainViewModel vm)
    {
        var tabs = (TabControl)window.FindName("WorkspaceTabs");
        var transfersTab = (TabItem)window.FindName("TransfersWorkspaceTab");
        Require(ReferenceEquals(tabs.SelectedItem, transfersTab),
            "Opening the file workspace did not put the populated transfer queue in the default dock.");
        var jobs = vm.TransferJobs.ToArray();
        Require(new[] { TransferState.Running, TransferState.Completed, TransferState.Failed, TransferState.Queued }
                .All(state => jobs.Any(job => job.State == state)),
            "The layout fixture did not exercise running, completed, failed, and queued transfers.");
        var localPath = vm.LocalPath;
        var remotePath = vm.RemotePath;
        var localFiles = vm.LocalEntries.ToArray();
        var remoteFiles = vm.RemoteEntries.ToArray();
        var localSelection = vm.SelectedLocalEntries.ToArray();
        var remoteSelection = vm.SelectedRemoteEntries.ToArray();
        var session = vm.ActiveConnection;
        foreach (var (width, height) in new[] { (1180, 800), (1440, 950) })
        {
            window.Width = width;
            window.Height = height;
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                foreach (var culture in UiText.SupportedCultureNames)
                {
                    UiText.Current.SetCulture(culture);
                    window.NavigateTo("files");
                    await SettleLayoutAsync(window);
                    var queue = FindVisualChild<DataGrid>(window, grid => grid.Name == "TransferJobsGrid" && grid.IsVisible)
                        ?? throw new InvalidOperationException("The default workspace queue was not visible.");
                    queue.ScrollIntoView(jobs[0]);
                    await SettleLayoutAsync(window);
                    var label = $"{width}x{height}-{culture}-{theme.ToString().ToLowerInvariant()}";
                    Capture(window, $"workspace-transfer-layout-{label}.png");
                    Require(ReferenceEquals(tabs.SelectedItem, transfersTab), $"Transfers lost its default dock in {label}.");
                    foreach (var name in new[] { "LocalFilesGrid", "RemoteFilesGrid", "UploadFileButton", "DownloadFileButton", "ExpandTransfersToggle" })
                        RequireVisibleWithinWindow(window, (FrameworkElement)window.FindName(name), $"{name} ({label})");
                    RequireQueueControlsVisible(window);
                    RequireUsableTransferViewport(window, queue, label);
                    Require(ReferenceEquals(vm.ActiveConnection, session) && vm.HasActiveSession
                        && vm.LocalPath == localPath && vm.RemotePath == remotePath
                        && vm.LocalEntries.SequenceEqual(localFiles) && vm.RemoteEntries.SequenceEqual(remoteFiles)
                        && vm.SelectedLocalEntries.SequenceEqual(localSelection) && vm.SelectedRemoteEntries.SequenceEqual(remoteSelection)
                        && vm.TransferJobs.SequenceEqual(jobs),
                        $"Changing workspace size, theme, or culture changed the active session, paths, listings, selections, or jobs in {label}.");
                }
            }
        }
        await VerifyWorkspaceExpansionAndSplittersAsync(window, vm);
    }

    private async Task VerifyWorkspaceExpansionAndSplittersAsync(MainWindow window, MainViewModel vm)
    {
        window.Width = 1440;
        window.Height = 950;
        await SettleLayoutAsync(window);
        var files = (Grid)window.FindName("FileBrowserGrid");
        var fileRow = (RowDefinition)window.FindName("FileBrowserRow");
        var transferRow = (RowDefinition)window.FindName("WorkspaceTransferRow");
        var splitter = (GridSplitter)window.FindName("WorkspaceFilesSplitter");
        var horizontalSplitter = VisualDescendants<GridSplitter>(files).Single(control => control.ResizeDirection == GridResizeDirection.Columns);
        var beforeHeight = fileRow.ActualHeight;
        SendSplitterKey(window, splitter, Key.Up);
        await SettleLayoutAsync(window);
        Require(fileRow.ActualHeight < beforeHeight - 0.5,
            "The file/transfer splitter did not resize the workspace with an arrow key.");
        for (var index = 0; index < 70; index++) SendSplitterKey(window, splitter, Key.Up);
        await SettleLayoutAsync(window);
        Require(fileRow.ActualHeight >= fileRow.MinHeight - 1 && transferRow.ActualHeight >= transferRow.MinHeight - 1,
            "Keyboard resizing crossed the file or transfer panel's minimum height.");
        for (var index = 0; index < 70; index++) SendSplitterKey(window, splitter, Key.Down);
        await SettleLayoutAsync(window);
        Require(fileRow.ActualHeight >= fileRow.MinHeight - 1 && transferRow.ActualHeight >= transferRow.MinHeight - 1,
            "Keyboard resizing in the other direction crossed a panel's minimum height.");
        var beforeWidth = files.ColumnDefinitions[0].ActualWidth;
        SendSplitterKey(window, horizontalSplitter, Key.Left);
        await SettleLayoutAsync(window);
        Require(files.ColumnDefinitions[0].ActualWidth < beforeWidth - 0.5,
            "The local/remote splitter did not resize the file browsers with an arrow key.");
        for (var index = 0; index < 100; index++) SendSplitterKey(window, horizontalSplitter, Key.Left);
        await SettleLayoutAsync(window);
        Require(files.ColumnDefinitions[0].ActualWidth >= files.ColumnDefinitions[0].MinWidth - 1
            && files.ColumnDefinitions[2].ActualWidth >= files.ColumnDefinitions[2].MinWidth - 1,
            "Keyboard resizing crossed a file browser's minimum width.");
        for (var index = 0; index < 100; index++) SendSplitterKey(window, horizontalSplitter, Key.Right);
        await SettleLayoutAsync(window);
        Require(files.ColumnDefinitions[0].ActualWidth >= files.ColumnDefinitions[0].MinWidth - 1
            && files.ColumnDefinitions[2].ActualWidth >= files.ColumnDefinitions[2].MinWidth - 1,
            "Keyboard resizing in the other direction crossed a file browser's minimum width.");
        foreach (var name in new[] { "LocalFilesPanel", "RemoteFilesPanel", "UploadFileButton", "DownloadFileButton" })
            RequireVisibleWithinWindow(window, (FrameworkElement)window.FindName(name), $"{name} after keyboard resizing");

        // Non-default proportions catch restore code that silently replaces a user's split with the initial layout.
        fileRow.Height = new GridLength(1.7, GridUnitType.Star);
        transferRow.Height = new GridLength(1.3, GridUnitType.Star);
        files.ColumnDefinitions[0].Width = new GridLength(1.25, GridUnitType.Star);
        files.ColumnDefinitions[2].Width = new GridLength(1.75, GridUnitType.Star);
        await SettleLayoutAsync(window);
        var fileHeight = fileRow.Height;
        var queueHeight = transferRow.Height;
        var fileMinimum = fileRow.MinHeight;
        var queueMinimum = transferRow.MinHeight;
        var leftWidth = files.ColumnDefinitions[0].Width;
        var rightWidth = files.ColumnDefinitions[2].Width;
        var dock = (FrameworkElement)window.FindName("TransferQueuePanel");
        var beforeDockHeight = dock.ActualHeight;
        var toggle = (ToggleButton)window.FindName("ExpandTransfersToggle");
        var jobs = vm.TransferJobs.ToArray();
        var states = jobs.Select(job => job.State).ToArray();
        var session = vm.ActiveConnection;
        var localPath = vm.LocalPath;
        var remotePath = vm.RemotePath;
        var localFiles = vm.LocalEntries.ToArray();
        var remoteFiles = vm.RemoteEntries.ToArray();
        var localSelection = vm.SelectedLocalEntries.ToArray();
        var remoteSelection = vm.SelectedRemoteEntries.ToArray();
        var paused = vm.IsTransferQueuePaused;
        var filter = vm.SelectedTransferFilter;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            toggle.IsChecked = true;
            await SettleLayoutAsync(window);
            Require(!files.IsVisible && !splitter.IsVisible && dock.ActualHeight > beforeDockHeight + 150,
                "Expanding transfers did not reclaim the browser space for the queue.");
            var grid = FindVisualChild<DataGrid>(window, item => item.Name == "TransferJobsGrid" && item.IsVisible)!;
            RequireQueueControlsVisible(window);
            RequireUsableTransferViewport(window, grid, "expanded transfer dock");
            if (cycle == 0) Capture(window, "workspace-transfers-expanded.png");
            toggle.IsChecked = false;
            await SettleLayoutAsync(window);
            Require(files.IsVisible && splitter.IsVisible && fileRow.Height == fileHeight && transferRow.Height == queueHeight
                && fileRow.MinHeight == fileMinimum && transferRow.MinHeight == queueMinimum
                && files.ColumnDefinitions[0].Width == leftWidth && files.ColumnDefinitions[2].Width == rightWidth,
                "Restoring the workspace reset a custom split proportion or panel minimum.");
            Require(ReferenceEquals(vm.ActiveConnection, session) && vm.HasActiveSession && vm.LocalPath == localPath && vm.RemotePath == remotePath
                && vm.LocalEntries.SequenceEqual(localFiles) && vm.RemoteEntries.SequenceEqual(remoteFiles)
                && vm.SelectedLocalEntries.SequenceEqual(localSelection) && vm.SelectedRemoteEntries.SequenceEqual(remoteSelection)
                && vm.TransferJobs.SequenceEqual(jobs) && jobs.Select(job => job.State).SequenceEqual(states)
                && vm.IsTransferQueuePaused == paused && vm.SelectedTransferFilter == filter,
                "Expanding and restoring the queue changed session, navigation, selection, transfer work, pause state, or filter.");
        }
        Capture(window, "workspace-transfers-restored.png");
    }

    private static void SendSplitterKey(Window window, GridSplitter splitter, Key key)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("The native workspace did not have a keyboard presentation source.");
        splitter.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
            { RoutedEvent = Keyboard.KeyDownEvent });
        window.UpdateLayout();
    }

    private static void RequireUsableTransferViewport(Window window, DataGrid grid, string context)
    {
        RequireVisibleWithinWindow(window, grid, $"transfer rows ({context})");
        var scroll = FindVisualChild<ScrollViewer>(grid, viewer => viewer.IsVisible)
            ?? throw new InvalidOperationException("The queue did not expose a scrolling viewport.");
        Require(scroll.ScrollableWidth <= 1, $"The queue required horizontal scrolling in {context}: {scroll.ScrollableWidth:0.##} px.");
        foreach (var header in VisualDescendants<DataGridColumnHeader>(grid).Where(item => item.IsVisible && item.Content is string))
        {
            var caption = (string)header.Content;
            var formatted = new FormattedText(caption, UiText.Current.Culture, header.FlowDirection,
                new Typeface(header.FontFamily, header.FontStyle, header.FontWeight, header.FontStretch),
                header.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip);
            var available = header.ActualWidth - header.Padding.Left - header.Padding.Right;
            Require(formatted.WidthIncludingTrailingWhitespace <= available + 1,
                $"The transfer column header '{caption}' was clipped in {context}.");
        }
        var fullRows = VisualDescendants<DataGridRow>(grid).Where(row => row.IsVisible)
            .Where(row =>
            {
                var bounds = row.TransformToAncestor(grid).TransformBounds(new Rect(row.RenderSize));
                return bounds.Top >= grid.ColumnHeaderHeight - 1 && bounds.Bottom <= grid.ActualHeight + 1;
            }).ToArray();
        Require(fullRows.Length >= 2, $"The default queue exposed only {fullRows.Length} complete transfer rows in {context}.");
        foreach (var row in fullRows)
        {
            foreach (var property in new[] { "FileName", "SpeedLabel", "RemainingTimeLabel" })
            {
                var text = FindVisualChild<TextBlock>(row, item => BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == property)
                    ?? throw new InvalidOperationException($"A transfer row lost its {property} display in {context}.");
                RequireVisibleWithinWindow(window, text, $"{property} ({context})");
                if (property == "FileName") Require(text.ActualWidth >= 140, $"The transfer filename was squeezed in {context}.");
            }
            var progress = FindVisualChild<ProgressBar>(row, item => item.IsVisible)
                ?? throw new InvalidOperationException($"A transfer row lost its progress bar in {context}.");
            Require(progress.ActualWidth >= 125, $"The progress bar was squeezed to {progress.ActualWidth:0.##} px in {context}.");
            var percentage = FindVisualChild<TextBlock>(row, item => BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == "ProgressPercent")!;
            var speed = FindVisualChild<TextBlock>(row, item => BindingOperations.GetBinding(item, TextBlock.TextProperty)?.Path?.Path == "SpeedLabel")!;
            var percentageBounds = percentage.TransformToAncestor(row).TransformBounds(new Rect(percentage.RenderSize));
            var speedBounds = speed.TransformToAncestor(row).TransformBounds(new Rect(speed.RenderSize));
            Require(speedBounds.Left - percentageBounds.Right >= 8,
                $"The transfer percentage and speed ran together without a readable column gutter in {context}.");
            foreach (var control in VisualDescendants<FrameworkElement>(row).Where(item => item.IsVisible && item is Button or ProgressBar))
            {
                RequireVisibleWithinWindow(window, control, $"transfer action/progress ({context})");
                var cell = FindVisualAncestor<DataGridCell>(control);
                if (cell is null) continue;
                var bounds = control.TransformToAncestor(cell).TransformBounds(new Rect(control.RenderSize));
                Require(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= cell.ActualWidth + 1 && bounds.Bottom <= cell.ActualHeight + 1,
                    $"A transfer action or progress control crossed its column bounds in {context}: {bounds} inside {cell.RenderSize}.");
                if (control is Button { Content: string caption } button)
                {
                    var formatted = new FormattedText(caption, UiText.Current.Culture, button.FlowDirection,
                        new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                        button.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(window).PixelsPerDip);
                    var available = button.ActualWidth - button.Padding.Left - button.Padding.Right
                        - button.BorderThickness.Left - button.BorderThickness.Right;
                    Require(formatted.WidthIncludingTrailingWhitespace <= available + 1,
                        $"The transfer action '{caption}' was clipped in {context}: {formatted.WidthIncludingTrailingWhitespace:0.##} px required, {available:0.##} px available.");
                }
            }
        }
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected remote UI state was not reached.");
            await Task.Delay(10);
        }
    }

    private static void RequireQueueControlsVisible(Window window)
    {
        var controls = new List<FrameworkElement>();
        foreach (var name in new[] { "ToggleTransferQueueButton", "CancelAllTransfersButton", "ClearCompletedTransfersButton" })
        {
            var button = FindVisualChild<Button>(window, item => item.Name == name && item.IsVisible)
                ?? throw new InvalidOperationException($"The transfer queue control {name} was not visible.");
            RequireVisibleWithinWindow(window, button, name);
            controls.Add(button);
        }
        var grid = FindVisualChild<DataGrid>(window, item => item.Name == "TransferJobsGrid" && item.IsVisible)
            ?? throw new InvalidOperationException("The transfer queue was not visible.");
        var filter = FindVisualChild<ComboBox>(window, item => item.Name == "TransferFilterBox" && item.IsVisible)
            ?? throw new InvalidOperationException("The transfer state filter was not visible.");
        RequireVisibleWithinWindow(window, filter, "transfer state filter");
        controls.Add(filter);
        for (var first = 0; first < controls.Count; first++)
        for (var second = first + 1; second < controls.Count; second++)
        {
            var firstBounds = controls[first].TransformToAncestor(window).TransformBounds(new Rect(controls[first].RenderSize));
            var secondBounds = controls[second].TransformToAncestor(window).TransformBounds(new Rect(controls[second].RenderSize));
            var overlap = Rect.Intersect(firstBounds, secondBounds);
            Require(overlap.IsEmpty || overlap.Width <= 1 || overlap.Height <= 1,
                $"Transfer toolbar controls {controls[first].Name} and {controls[second].Name} overlap.");
        }
        foreach (var property in new[] { "SpeedLabel", "RemainingTimeLabel" })
            Require(FindVisualChild<TextBlock>(grid, text => text.IsVisible
                && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == property) is not null,
                $"The transfer queue lost its {property} display.");
    }
}

internal sealed class QaRemoteFiles : IRemoteFileService
{
    public IReadOnlyList<FileEntry> Entries { get; } = [
        Entry("QA builds", "/home/qa/QA builds", true),
        Entry("QA résumé 空.txt", "/home/qa/QA résumé 空.txt"),
        Entry("QA empty.bin", "/home/qa/QA empty.bin") with { Size = 0 }
    ];
    public Func<string, CancellationToken, Task<IReadOnlyList<FileEntry>>>? ListHandler { get; set; }
    public Func<string, CancellationToken, Task<bool>>? ExistsHandler { get; set; }
    public Func<string, string, IProgress<TransferProgress>?, CancellationToken, Task>? UploadHandler { get; set; }
    public Func<string, string, IProgress<TransferProgress>?, CancellationToken, Task>? DownloadHandler { get; set; }
    public int UploadCalls { get; private set; }
    public int DownloadCalls { get; private set; }
    public List<(string Source, string Destination, bool Overwrite)> UploadRequests { get; } = [];
    public List<(string Source, string Destination, bool Overwrite)> DownloadRequests { get; } = [];
    public static FileEntry Entry(string name, string path, bool directory = false)
        => new() { Name = name, FullPath = path, IsDirectory = directory, Size = directory ? 0 : 256 * 1024,
            LastModified = new DateTime(2026, 10, 29, 12, 30, 0) };
    public Task<string> GetHomeDirectoryAsync(CancellationToken cancellationToken = default) => Task.FromResult("/home/qa");
    public Task<IReadOnlyList<FileEntry>> GetEntriesAsync(string remotePath, CancellationToken cancellationToken = default)
        => ListHandler?.Invoke(remotePath, cancellationToken) ?? Task.FromResult(Entries);
    public Task<bool> ExistsAsync(string remotePath, CancellationToken cancellationToken = default)
        => ExistsHandler?.Invoke(remotePath, cancellationToken) ?? Task.FromResult(false);
    public async Task UploadAsync(string localPath, string remoteDestination, bool overwrite,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        UploadCalls++;
        UploadRequests.Add((localPath, remoteDestination, overwrite));
        if (UploadHandler is not null) await UploadHandler(localPath, remoteDestination, progress, cancellationToken);
    }
    public async Task DownloadAsync(string remoteFile, string localDestination, bool overwrite,
        IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        DownloadCalls++;
        DownloadRequests.Add((remoteFile, localDestination, overwrite));
        if (DownloadHandler is not null) await DownloadHandler(remoteFile, localDestination, progress, cancellationToken);
    }
}
