using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Core.Models;
using OhMySsh.Desktop;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyRemoteDownloadSelectionAsync()
    {
        var first = QaRemoteFiles.Entry("résumé café 空 one.txt", "/home/qa/résumé café 空 one.txt") with { Size = 12 };
        var second = QaRemoteFiles.Entry("second space.bin", "/home/qa/second space.bin") with { Size = 8 };
        var folder = QaRemoteFiles.Entry("folder with spaces", "/home/qa/folder with spaces", true);
        var fixture = await CreateRemoteDownloadFixtureAsync([folder, first, second]);
        var vm = fixture.Model;
        var interactions = fixture.Interactions;
        var files = fixture.Files;
        var target = Path.Combine(_workspace, "native remote batch");
        Directory.CreateDirectory(target);
        Window? window = null;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Require(!vm.DownloadCommand.CanExecute(null), "Downloading was enabled with no remote selection.");
            var singleTarget = Path.Combine(target, "user renamed café.txt");
            interactions.DownloadFiles.Enqueue(singleTarget);
            files.DownloadHandler = async (_, destination, progress, token) =>
            {
                await File.WriteAllBytesAsync(destination, new byte[12], token);
                progress?.Report(new TransferProgress(12, 12, IsFinalizing: true));
            };
            vm.SelectedRemoteEntry = first;
            await vm.DownloadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            Require(interactions.DownloadFileRequests.SequenceEqual([(first.Name, _workspace)])
                && interactions.DownloadFolderRequests.Count == 0
                && files.DownloadRequests.Single().Source == first.FullPath
                && files.DownloadRequests.Single().Destination == singleTarget
                && vm.TransferJobs.Single().State == TransferState.Completed
                && vm.TransferJobs.Single().ProgressPercent == 100
                && new FileInfo(singleTarget).Length == 12,
                "A single remote file lost its suggested name, save-as destination, or completed transfer flow.");

            var active = 0;
            var maximumActive = 0;
            var payloads = new Dictionary<string, byte[]>
            {
                [first.FullPath] = Enumerable.Range(1, 12).Select(value => (byte)value).ToArray(),
                [second.FullPath] = Enumerable.Range(31, 8).Select(value => (byte)value).ToArray()
            };
            files.DownloadHandler = async (source, destination, progress, token) =>
            {
                active++;
                maximumActive = Math.Max(maximumActive, active);
                try
                {
                    var payload = payloads[source];
                    progress?.Report(new TransferProgress(payload.Length / 2, payload.Length));
                    if (source == first.FullPath) await release.Task.WaitAsync(token);
                    await File.WriteAllBytesAsync(destination, payload, token);
                    progress?.Report(new TransferProgress(payload.Length, payload.Length, IsFinalizing: true));
                }
                finally { active--; }
            };
            vm.SelectedRemoteEntry = null;
            vm.ToggleTransferQueueCommand.Execute(null);
            interactions.DownloadFolders.Enqueue(target);
            window = new MainWindow(vm, initializeOnLoad: false);
            PrepareHiddenWindow(window);
            window.Show();
            ((MainWindow)window).NavigateTo("files");
            await SettleLayoutAsync(window);
            var grid = (DataGrid)window.FindName("RemoteFilesGrid");
            var button = (Button)window.FindName("DownloadFileButton");
            Require(grid.SelectionMode == DataGridSelectionMode.Extended && grid.SelectionUnit == DataGridSelectionUnit.FullRow,
                "The native remote grid does not permit extended full-row selection.");
            grid.SelectedItems.Add(folder);
            grid.SelectedItems.Add(first);
            grid.SelectedItems.Add(second);
            Require(vm.SelectedRemoteEntries.SequenceEqual([folder, first, second])
                && ReferenceEquals(button.Command, vm.DownloadCommand) && button.IsEnabled,
                "The native selection did not reach the download action when its active row was a folder.");
            button.Command!.Execute(button.CommandParameter);
            await WaitUntilAsync(() => vm.TransferJobs.Count == 3);
            Require(interactions.DownloadFolderRequests.SequenceEqual([_workspace])
                && interactions.DownloadFileRequests.Count == 1
                && vm.TransferJobs.Skip(1).All(job => job.State == TransferState.Queued)
                && vm.TransferJobs.Skip(1).Select(job => job.SourcePath).SequenceEqual([first.FullPath, second.FullPath])
                && vm.PendingBytes == 20 && files.DownloadCalls == 1,
                "A native batch opened multiple pickers, included a directory, changed selection order, or lost known file sizes.");
            vm.ToggleTransferQueueCommand.Execute(null);
            await WaitUntilAsync(() => files.DownloadCalls == 2 && vm.TransferJobs[1].BytesTransferred > 0);
            Require(vm.TransferJobs[1].State == TransferState.Running && vm.TransferJobs[1].ProgressPercent is > 0 and < 100
                && vm.TransferJobs[2].State == TransferState.Queued && maximumActive == 1,
                "The first batch file did not expose progress while the next selected file waited in the serial queue.");
            release.TrySetResult();
            await vm.WaitForTransfersAsync();
            Require(maximumActive == 1 && files.DownloadRequests.Skip(1).Select(request => request.Source)
                    .SequenceEqual([first.FullPath, second.FullPath])
                && vm.TransferJobs.All(job => job.State == TransferState.Completed && job.ProgressPercent == 100)
                && (await File.ReadAllBytesAsync(Path.Combine(target, first.Name))).SequenceEqual(payloads[first.FullPath])
                && (await File.ReadAllBytesAsync(Path.Combine(target, second.Name))).SequenceEqual(payloads[second.FullPath]),
                "The batch omitted, reordered, overlapped, or corrupted a selected file with spaces or Unicode in its name.");
            grid.SelectedItems.Clear();
            Require(vm.SelectedRemoteEntries.Count == 0 && !button.IsEnabled,
                "Clearing the native remote selection retained a downloadable selection.");
            grid.SelectedItems.Add(folder);
            Require(!button.IsEnabled, "A folder-only native selection enabled the file download action.");
        }
        finally
        {
            release.TrySetResult();
            await vm.ShutdownAsync();
            window?.Close();
        }
    }

    private async Task VerifyRemoteDownloadFilteringAsync()
    {
        var kept = QaRemoteFiles.Entry("case.txt", "/home/qa/case.txt");
        var collision = QaRemoteFiles.Entry("CASE.TXT", "/home/qa/CASE.TXT");
        var other = QaRemoteFiles.Entry("café 空 accepted.txt", "/home/qa/café 空 accepted.txt");
        var directory = QaRemoteFiles.Entry("skipped folder", "/home/qa/skipped folder", true);
        FileEntry[] unsafeFiles = [QaRemoteFiles.Entry("CON.txt", "/home/qa/CON.txt"),
            QaRemoteFiles.Entry("trailing.", "/home/qa/trailing."), QaRemoteFiles.Entry("unsafe:name.txt", "/home/qa/unsafe:name.txt")];
        FileEntry[] selection = [directory, kept, collision, .. unsafeFiles, other];
        var fixture = await CreateRemoteDownloadFixtureAsync(selection);
        var vm = fixture.Model;
        var interactions = fixture.Interactions;
        var files = fixture.Files;
        var target = Path.Combine(_workspace, "filtered remote batch");
        Directory.CreateDirectory(target);
        var keptTarget = Path.Combine(target, kept.Name);
        var otherTarget = Path.Combine(target, other.Name);
        await File.WriteAllTextAsync(keptTarget, "keep existing content");
        await File.WriteAllTextAsync(otherTarget, "replace existing content");
        var originalCulture = UiText.Current.CultureName;
        try
        {
            vm.ToggleTransferQueueCommand.Execute(null);
            vm.UpdateSelectedRemoteSelection(selection);
            interactions.DownloadFolders.Enqueue(target);
            await vm.DownloadCommand.ExecuteAsync();
            Require(vm.TransferJobs.Count == 2 && vm.TransferJobs.Select(job => job.SourcePath).SequenceEqual([kept.FullPath, other.FullPath])
                && interactions.DownloadFolderRequests.Count == 1 && interactions.DownloadFileRequests.Count == 0,
                "A batch did not keep the first case-insensitive Windows destination or skip unsupported names and directories.");
            var notices = new List<string>();
            foreach (var culture in UiText.SupportedCultureNames)
            {
                UiText.Current.SetCulture(culture);
                var expected = string.Join(' ', UiText.Format("Vm.BatchDownload.Files", 2),
                    UiText.Format("Vm.BatchDownload.Directory", 1), UiText.Format("Vm.BatchDownload.Invalids", 3),
                    UiText.Format("Vm.BatchDownload.Duplicate", 1));
                Require(vm.HasBatchDownloadNotice && vm.BatchDownloadNotice == expected,
                    $"The existing batch notice did not refresh its queued, directory, unsafe-name, and collision counts in {culture}.");
                notices.Add(vm.BatchDownloadNotice);
            }
            Require(notices.Distinct().Count() == 3, "Batch download rejection notices did not use all three catalogs.");
            UiText.Current.SetCulture(originalCulture);
            var prompts = new List<(string Destination, bool IsRemote)>();
            interactions.OverwriteHandler = (destination, isRemote, _) =>
            {
                prompts.Add((destination, isRemote));
                return Task.FromResult(destination == otherTarget);
            };
            files.DownloadHandler = async (_, destination, progress, token) =>
            {
                await File.WriteAllTextAsync(destination, "accepted replacement", token);
                progress?.Report(new TransferProgress(20, 20, IsFinalizing: true));
            };
            vm.ToggleTransferQueueCommand.Execute(null);
            await vm.WaitForTransfersAsync();
            Require(prompts.SequenceEqual([(keptTarget, false), (otherTarget, false)])
                && vm.TransferJobs[0].State == TransferState.Canceled && vm.TransferJobs[1].State == TransferState.Completed
                && files.DownloadRequests.Single().Source == other.FullPath && files.DownloadRequests.Single().Overwrite
                && await File.ReadAllTextAsync(keptTarget) == "keep existing content"
                && await File.ReadAllTextAsync(otherTarget) == "accepted replacement",
                "Overwrite approval was not requested independently for each existing batch destination.");

            vm.UpdateSelectedRemoteSelection([kept, other]);
            interactions.DownloadFolders.Enqueue(null);
            await vm.DownloadCommand.ExecuteAsync();
            Require(vm.TransferJobs.Count == 2 && interactions.DownloadFolderRequests.Count == 2 && files.DownloadCalls == 1,
                "Canceling the batch destination picker added work or performed a transfer.");
            vm.UpdateSelectedRemoteSelection(unsafeFiles);
            await vm.DownloadCommand.ExecuteAsync();
            Require(vm.TransferJobs.Count == 2 && interactions.DownloadFolderRequests.Count == 2 && vm.HasBatchDownloadNotice,
                "A wholly unsupported selection opened a destination picker or silently lost its rejection notice.");
            vm.UpdateSelectedRemoteSelection([directory]);
            vm.SelectedRemoteEntry = directory;
            Require(!vm.DownloadCommand.CanExecute(null), "A directory-only selection enabled file downloads.");
        }
        finally { UiText.Current.SetCulture(originalCulture); await vm.ShutdownAsync(); }
    }

    private async Task VerifyRemoteDownloadCaptureAsync()
    {
        var first = QaRemoteFiles.Entry("captured café one.txt", "/home/qa/captured café one.txt") with { Size = 13 };
        var second = QaRemoteFiles.Entry("captured 空 two.txt", "/home/qa/captured 空 two.txt") with { Size = 17 };
        var replacement = QaRemoteFiles.Entry("other selection.txt", "/different/other selection.txt");
        var fixture = await CreateRemoteDownloadFixtureAsync([first, second]);
        var vm = fixture.Model;
        var localOther = Path.Combine(_workspace, "changed local navigation");
        var target = Path.Combine(_workspace, "captured remote batch");
        Directory.CreateDirectory(localOther);
        Directory.CreateDirectory(target);
        try
        {
            vm.ToggleTransferQueueCommand.Execute(null);
            var mutableSelection = new List<FileEntry> { first, second };
            vm.UpdateSelectedRemoteSelection(mutableSelection);
            mutableSelection.Clear();
            Require(vm.SelectedRemoteEntries.SequenceEqual([first, second]), "The remote selection retained a mutable caller-owned list.");
            fixture.Files.ListHandler = (path, _) => path == "/missing"
                ? Task.FromException<IReadOnlyList<FileEntry>>(new RemoteFileException(RemoteFileErrorCode.NotFound, "QA directory is unavailable."))
                : Task.FromResult<IReadOnlyList<FileEntry>>([replacement]);
            fixture.Interactions.DownloadFolderHandler = initialDirectory =>
            {
                Require(initialDirectory == _workspace, "The download picker did not use the pre-picker local directory.");
                RunInsideDownloadPicker(async () =>
                {
                    vm.SelectedConnection = fixture.OtherProfile;
                    await vm.NavigateRemoteToAsync("/different");
                    await vm.NavigateToAsync(localOther);
                    vm.UpdateSelectedRemoteSelection([replacement]);
                    vm.SelectedRemoteEntry = replacement;
                });
                return target;
            };
            await vm.DownloadCommand.ExecuteAsync();
            Require(vm.TransferJobs.Count == 2 && vm.TransferJobs.All(job => job.Profile.Id == fixture.Profile.Id)
                && vm.TransferJobs.Select(job => job.SourcePath).SequenceEqual([first.FullPath, second.FullPath])
                && vm.TransferJobs.Select(job => job.DestinationPath).SequenceEqual([Path.Combine(target, first.Name), Path.Combine(target, second.Name)])
                && vm.PendingBytes == 30 && vm.RemotePath == "/different" && vm.LocalPath == localOther,
                "Selection, profile, or navigation changes inside a modal picker redirected the captured batch.");

            var selectionBeforeFailure = vm.SelectedRemoteEntries.ToArray();
            await vm.NavigateRemoteToAsync("/missing");
            Require(vm.HasRemoteError && vm.RemotePath == "/different"
                && vm.SelectedRemoteEntries.SequenceEqual(selectionBeforeFailure) && vm.SelectedRemoteEntry == replacement,
                "A failed remote navigation erased the usable selection from the preserved listing.");
            await vm.RefreshRemoteCommand.ExecuteAsync();
            Require(vm.SelectedRemoteEntries.Count == 0 && vm.SelectedRemoteEntry is null && !vm.DownloadCommand.CanExecute(null),
                "Replacing the remote listing retained its previous downloadable selection.");
            fixture.Files.DownloadHandler = async (source, destination, progress, token) =>
            {
                await File.WriteAllTextAsync(destination, source, token);
                progress?.Report(new TransferProgress(1, 1, IsFinalizing: true));
            };
            vm.ToggleTransferQueueCommand.Execute(null);
            await vm.WaitForTransfersAsync();
            Require(await File.ReadAllTextAsync(Path.Combine(target, first.Name)) == first.FullPath
                && await File.ReadAllTextAsync(Path.Combine(target, second.Name)) == second.FullPath,
                "Captured downloads resolved their sources from the replacement selection or current directory.");
        }
        finally { await vm.ShutdownAsync(); }
    }

    private async Task VerifyRemoteDownloadCancellationAsync()
    {
        FileEntry[] entries = Enumerable.Range(0, 130)
            .Select(index => QaRemoteFiles.Entry($"cancel batch {index}.txt", $"/home/qa/cancel batch {index}.txt"))
            .ToArray();
        var fixture = await CreateRemoteDownloadFixtureAsync(entries);
        var vm = fixture.Model;
        var target = Path.Combine(_workspace, "canceled remote batch");
        Directory.CreateDirectory(target);
        try
        {
            vm.ToggleTransferQueueCommand.Execute(null);
            vm.UpdateSelectedRemoteSelection(entries.Take(2));
            fixture.Interactions.DownloadFolderHandler = _ =>
            {
                Require(vm.CancelAllTransfersCommand.CanExecute(null), "Cancel all was disabled while the destination picker owned a pending batch.");
                vm.CancelAllTransfersCommand.Execute(null);
                return target;
            };
            await vm.DownloadCommand.ExecuteAsync();
            Require(vm.TransferJobs.Count == 0 && fixture.Files.DownloadCalls == 0,
                "Returning from a picker enqueued a batch after cancel all invalidated it.");

            var cancelScheduled = false;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler cancelDuringInsertion = (_, _) =>
            {
                if (cancelScheduled || vm.TransferJobs.Count == 0) return;
                cancelScheduled = true;
                application.Dispatcher.BeginInvoke(DispatcherPriority.Normal,
                    new Action(() => vm.CancelAllTransfersCommand.Execute(null)));
            };
            fixture.Interactions.DownloadFolderHandler = _ => target;
            vm.UpdateSelectedRemoteSelection(entries);
            vm.TransferJobs.CollectionChanged += cancelDuringInsertion;
            try { await vm.DownloadCommand.ExecuteAsync(); }
            finally { vm.TransferJobs.CollectionChanged -= cancelDuringInsertion; }
            Require(cancelScheduled && vm.TransferJobs.Count > 0 && vm.TransferJobs.Count < entries.Length
                && vm.TransferJobs.All(job => job.State == TransferState.Canceled) && fixture.Files.DownloadCalls == 0,
                "Cancel all failed to stop later insertion chunks or retained live work from the canceled batch.");

            var priorCount = vm.TransferJobs.Count;
            vm.UpdateSelectedRemoteSelection(entries.Take(2));
            fixture.Interactions.DownloadFolderHandler = _ =>
            {
                RunInsideDownloadPicker(() => vm.DisconnectCommand.ExecuteAsync());
                return target;
            };
            await vm.DownloadCommand.ExecuteAsync();
            Require(!vm.HasActiveSession && vm.SelectedRemoteEntries.Count == 0 && vm.SelectedRemoteEntry is null
                && vm.TransferJobs.Count == priorCount && fixture.Files.DownloadCalls == 0,
                "A disconnected modal picker published stale work or retained remote selection.");

            await vm.ConnectCommand.ExecuteAsync(fixture.Profile);
            await vm.RemoteInitialization;
            vm.UpdateSelectedRemoteSelection(entries.Take(2));
            fixture.Interactions.DownloadFolderHandler = _ =>
            {
                RunInsideDownloadPicker(async () =>
                {
                    await vm.DisconnectCommand.ExecuteAsync();
                    await vm.ConnectCommand.ExecuteAsync(fixture.OtherProfile);
                    await vm.RemoteInitialization;
                });
                return target;
            };
            await vm.DownloadCommand.ExecuteAsync();
            Require(vm.HasActiveSession && vm.ActiveConnection?.Id == fixture.OtherProfile.Id
                && vm.TransferJobs.Count == priorCount && fixture.Files.DownloadCalls == 0,
                "A retired picker enqueued old sources into a replacement VPS session.");
        }
        finally { await vm.ShutdownAsync(); }
    }

    private async Task<(MainViewModel Model, QaRemoteFiles Files, RecordingInteractions Interactions,
        ConnectionProfile Profile, ConnectionProfile OtherProfile)> CreateRemoteDownloadFixtureAsync(IReadOnlyList<FileEntry> entries)
    {
        var profile = CreateProfile();
        var other = profile with { Id = Guid.NewGuid(), Host = "other-download.example.invalid" };
        var interactions = new RecordingInteractions();
        var files = new QaRemoteFiles { ListHandler = (_, _) => Task.FromResult(entries) };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile, other]), interactions, service);
        try
        {
            await vm.InitializeAsync();
            await vm.NavigateToAsync(_workspace);
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            return (vm, files, interactions, profile, other);
        }
        catch { await vm.ShutdownAsync(); throw; }
    }

    // Native modal pickers pump the dispatcher; this seam permits the same lifecycle changes without opening one.
    private static void RunInsideDownloadPicker(Func<Task> action)
    {
        var frame = new DispatcherFrame();
        Exception? failure = null;
        _ = RunAsync();
        Dispatcher.PushFrame(frame);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        async Task RunAsync()
        {
            try { await action(); }
            catch (Exception exception) { failure = exception; }
            finally { frame.Continue = false; }
        }
    }
}
