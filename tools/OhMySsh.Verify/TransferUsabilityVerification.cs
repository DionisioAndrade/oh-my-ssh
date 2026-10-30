using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyTransferFailureDetailsAsync()
    {
        const string secret = "QA_PASSWORD_DO_NOT_EXPOSE; passphrase=QA_SECRET; private-key=QA_KEY_CONTENT";
        var logs = new List<string>();
        var profile = CreateProfile();
        var interactions = new RecordingInteractions();
        var files = new QaRemoteFiles { UploadHandler = (_, _, _, _) =>
            throw new RemoteFileException(RemoteFileErrorCode.PermissionDenied, secret) };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = new MainViewModel(new MemoryProfileStore([profile]), new RestrictedLocalFileService(outputDirectory),
            interactions, service, (kind, message) => logs.Add(kind + ": " + message));
        try
        {
            await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
            await vm.UploadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            var failed = vm.TransferJobs.Single();
            Require(failed.State == TransferState.Failed && failed.HasFailureDetails
                && failed.FailureSummary.Length > 0 && failed.FailureReason.Length > 0 && failed.SuggestedAction.Length > 0,
                "The failed file did not retain an actionable per-file cause and suggested action.");
            Require(!string.Join(" ", logs.Append(failed.Error).Append(failed.FailureSummary)
                    .Append(failed.FailureReason).Append(failed.SuggestedAction).Append(vm.RemoteError))
                .Contains("QA_PASSWORD", StringComparison.Ordinal),
                "A raw failure exposed credentials in logs or user-visible error details.");
            Require(vm.ShowTransferDetailsCommand.CanExecute(failed), "A failed row did not expose its details action.");
            vm.ShowTransferDetailsCommand.Execute(failed);
            Require(interactions.TransferDetailsRequests.SequenceEqual([failed]), "Details targeted another transfer row.");

            foreach (var exception in new Exception[] {
                new UnauthorizedAccessException(secret), new IOException(secret),
                new FileNotFoundException(secret), new ArgumentException(secret),
                new RemoteFileException(RemoteFileErrorCode.ConnectionLost, secret, true),
                new RemoteFileException(RemoteFileErrorCode.UnsafePath, secret),
                new InvalidOperationException(secret) })
            {
                var sample = CreateTimedTransfer(new QaMonotonicTimeProvider());
                typeof(TransferItemViewModel).GetMethod("SetFailure", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(sample, [exception]);
                Require(sample.State == TransferState.Failed && sample.HasFailureDetails && sample.SuggestedAction.Length > 0
                    && !string.Join(" ", sample.Error, sample.FailureSummary, sample.FailureReason, sample.SuggestedAction)
                        .Contains("QA_", StringComparison.Ordinal),
                    "A typed or unexpected failure leaked raw diagnostic text or lost its recovery action.");
            }
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Current.Apply(theme);
                var window = new TransferDetailsWindow(failed);
                PrepareHiddenWindow(window);
                window.Show();
                try
                {
                    await SettleLayoutAsync(window);
                    Require(((TextBlock)window.FindName("FailureReasonText")).Text == failed.FailureReason
                        && ((TextBlock)window.FindName("SuggestedActionText")).Text == failed.SuggestedAction,
                        "The native details dialog did not bind the selected file's reason and action.");
                    Capture(window, theme == AppTheme.Light ? "transfer-details-light.png" : "transfer-details-dark.png");
                }
                finally { window.Close(); }
            }
        }
        finally { await vm.ShutdownAsync(); ThemeService.Current.Apply(AppTheme.Light); }
    }

    private async Task VerifyCompletedTransferActionsAsync()
    {
        var profile = CreateProfile();
        var interactions = new RecordingInteractions();
        var files = new QaRemoteFiles { UploadHandler = (_, _, progress, _) =>
        {
            progress?.Report(new TransferProgress(28, 28, IsFinalizing: true));
            return Task.CompletedTask;
        } };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile]), interactions, service);
        try
        {
            await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            var source = Path.Combine(_workspace, "deploy.ps1");
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", source);
            vm.ToggleTransferQueueCommand.Execute(null);
            await vm.UploadCommand.ExecuteAsync();
            var upload = vm.TransferJobs.Single();
            Require(!vm.OpenTransferFolderCommand.CanExecute(upload) && !vm.CopyRemotePathCommand.CanExecute(upload)
                && !vm.RevealDownloadedFileCommand.CanExecute(upload), "Pending work exposed completion-only shortcuts.");
            vm.ToggleTransferQueueCommand.Execute(null);
            await vm.WaitForTransfersAsync();
            await vm.OpenTransferFolderCommand.ExecuteAsync(upload);
            vm.CopyRemotePathCommand.Execute(upload);
            Require(interactions.OpenedTransferFolders.SequenceEqual([Path.GetDirectoryName(source)!])
                && interactions.ClipboardText == upload.DestinationPath
                && !vm.RevealDownloadedFileCommand.CanExecute(upload),
                "Completed upload shortcuts did not use its captured local folder and remote destination.");

            var destination = Path.Combine(_workspace, "download café, 空.txt");
            interactions.DownloadFiles.Enqueue(destination);
            files.DownloadHandler = async (_, local, progress, _) =>
            {
                await File.WriteAllTextAsync(local, "completed QA download");
                progress?.Report(new TransferProgress(21, 21, IsFinalizing: true));
            };
            vm.SelectedRemoteEntry = vm.RemoteEntries.First(entry => !entry.IsDirectory);
            await vm.DownloadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            var download = vm.TransferJobs.Last();
            vm.SelectedConnection = profile with { Id = Guid.NewGuid(), Host = "other.example.invalid" };
            await vm.NavigateRemoteToAsync("/different-current-directory");
            await vm.OpenTransferFolderCommand.ExecuteAsync(download);
            await vm.RevealDownloadedFileCommand.ExecuteAsync(download);
            vm.CopyRemotePathCommand.Execute(download);
            Require(interactions.OpenedTransferFolders.Last() == Path.GetDirectoryName(destination)
                && interactions.RevealedTransferFiles.SequenceEqual([destination])
                && interactions.ClipboardText == download.SourcePath,
                "Completed download shortcuts used the current selection or lost spaces, commas, or Unicode in its destination.");
            File.Delete(destination);
            var messagesBeforeMissing = interactions.Messages.Count;
            vm.RevealDownloadedFileCommand.Execute(download);
            await WaitUntilAsync(() => interactions.Messages.Count > messagesBeforeMissing);
            Require(interactions.RevealedTransferFiles.Count == 1, "Reveal attempted to select a downloaded file that no longer exists.");
            vm.ClearCompletedTransfersCommand.Execute(null);
            Require(!vm.OpenTransferFolderCommand.CanExecute(upload) && !vm.CopyRemotePathCommand.CanExecute(download)
                && !vm.RevealDownloadedFileCommand.CanExecute(download), "A removed row retained executable file shortcuts.");
        }
        finally { await vm.ShutdownAsync(); }
    }

    private async Task VerifyTransferFiltersAndSummaryAsync()
    {
        var vm = CreateViewModel(new MemoryProfileStore(), new RecordingInteractions());
        var samples = new List<TransferItemViewModel>();
        TransferItemViewModel Add(long? size, TransferState state, long transferred = 0)
        {
            var job = CreateTimedTransfer(new QaMonotonicTimeProvider());
            if (size is { } known) SetTransferExpectedSize(job, known);
            if (transferred > 0) PublishTransferProgress(job, new TransferProgress(transferred, size!.Value));
            SetTransferState(job, state);
            samples.Add(job);
            vm.TransferJobs.Add(job);
            return job;
        }
        try
        {
            await vm.InitializeAsync();
            Add(20, TransferState.Completed);
            Add(30, TransferState.Failed);
            Add(40, TransferState.Canceled);
            var running = Add(100, TransferState.Running, 25);
            Add(50, TransferState.Queued);
            var unknown = Add(null, TransferState.Queued);
            Add(0, TransferState.Queued);
            Require(vm.PendingTransferCount == 4 && vm.PendingBytes == 125 && vm.HasUnknownPendingSize
                && vm.PendingSummary.Length > 0 && vm.PendingBytesLabel.Length > 0,
                "Pending volume did not exclude terminal jobs, subtract copied bytes, or distinguish unknown and empty sources.");
            foreach (var (filter, count) in new[] {
                (TransferFilter.Active, 4), (TransferFilter.Completed, 1), (TransferFilter.Failed, 1),
                (TransferFilter.Canceled, 1), (TransferFilter.All, 7) })
            {
                vm.SelectedTransferFilter = filter;
                Require(vm.FilteredTransferJobs.Cast<TransferItemViewModel>().Count() == count
                    && vm.PendingTransferCount == 4 && vm.PendingBytes == 125,
                    "A state filter included wrong rows or incorrectly changed the full queue's pending summary.");
            }
            vm.SelectedTransferFilter = TransferFilter.Active;
            SetTransferState(running, TransferState.Completed);
            // Detached arithmetic fixtures have no queue-owner subscription; real live updates are tested below.
            vm.FilteredTransferJobs.Refresh();
            Require(vm.FilteredTransferJobs.Cast<TransferItemViewModel>().Count() == 3
                && vm.PendingTransferCount == 3 && vm.PendingBytes == 50,
                "A completed row remained in the live active filter or retained its remaining bytes.");
            vm.TransferJobs.Remove(unknown);
            Require(!vm.HasUnknownPendingSize && vm.PendingTransferCount == 2 && vm.PendingBytes == 50,
                "Removing the unknown-size row did not restore an exact pending-volume summary.");
            var largeOne = Add(long.MaxValue, TransferState.Queued);
            var largeTwo = Add(long.MaxValue, TransferState.Queued);
            Require(vm.PendingBytes == long.MaxValue && !vm.PendingBytesLabel.Contains('-', StringComparison.Ordinal),
                "Pending bytes overflowed into a negative or misleading value.");
            vm.TransferJobs.Remove(largeOne);
            vm.TransferJobs.Remove(largeTwo);
            vm.SelectedTransferFilter = TransferFilter.Completed;
            vm.ClearCompletedTransfersCommand.Execute(null);
            Require(!vm.FilteredTransferJobs.Cast<TransferItemViewModel>().Any() && !vm.HasVisibleTransfers
                && vm.TransferJobs.Any(job => job.State == TransferState.Failed)
                && vm.TransferJobs.Any(job => job.State == TransferState.Canceled),
                "Clearing successes under a filter erased failure history or retained a completed row.");

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var liveProfile = CreateProfile();
            var liveFiles = new QaRemoteFiles { UploadHandler = async (_, _, progress, token) =>
            {
                await release.Task.WaitAsync(token);
                progress?.Report(new TransferProgress(28, 28, IsFinalizing: true));
            } };
            var liveService = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
                Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = liveFiles }) };
            var other = CreateViewModel(new MemoryProfileStore([liveProfile]), new RecordingInteractions(), liveService);
            try
            {
                await other.InitializeAsync();
                Require(!other.FilteredTransferJobs.Cast<TransferItemViewModel>().Any(),
                    "Two independent windows shared a transfer-filter collection view.");
                await other.ConnectCommand.ExecuteAsync(liveProfile);
                await other.RemoteInitialization;
                other.SelectedTransferFilter = TransferFilter.Active;
                other.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
                await other.UploadCommand.ExecuteAsync();
                await WaitUntilAsync(() => liveFiles.UploadCalls == 1);
                Require(other.FilteredTransferJobs.Cast<TransferItemViewModel>().Count() == 1 && other.PendingTransferCount == 1,
                    "The active filter did not receive a newly started real queue job.");
                release.SetResult();
                await other.WaitForTransfersAsync();
                Require(!other.FilteredTransferJobs.Cast<TransferItemViewModel>().Any()
                    && !other.HasVisibleTransfers && other.PendingTransferCount == 0,
                    "A real queue state change did not refresh its active filter and pending summary.");
            }
            finally { await other.ShutdownAsync(); }
        }
        finally
        {
            await vm.ShutdownAsync();
            foreach (var sample in samples)
                ((CancellationTokenSource)typeof(TransferItemViewModel).GetProperty("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(sample)!).Dispose();
        }
    }

    private async Task VerifyTransferBatchUploadsAsync()
    {
        var profile = CreateProfile();
        var other = profile with { Id = Guid.NewGuid(), Host = "unrelated.example.invalid" };
        var interactions = new RecordingInteractions();
        var files = new QaRemoteFiles { UploadHandler = (_, _, progress, _) =>
        {
            progress?.Report(new TransferProgress(1, 1, IsFinalizing: true));
            return Task.CompletedTask;
        } };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile, other]), interactions, service);
        var first = Path.Combine(_workspace, "batch café 空 one.bin");
        var second = Path.Combine(_workspace, "batch space two.bin");
        var empty = Path.Combine(_workspace, "batch empty.bin");
        await File.WriteAllBytesAsync(first, new byte[123]);
        await File.WriteAllBytesAsync(second, new byte[77]);
        await File.WriteAllBytesAsync(empty, []);
        try
        {
            await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            vm.ToggleTransferQueueCommand.Execute(null);
            await vm.UploadFilesCommand.ExecuteAsync(new[] { first, second, first, _workspace, Path.Combine(_workspace, "missing-source.bin"), "invalid\0source" });
            Require(vm.TransferJobs.Count == 3 && vm.TransferJobs.All(job => job.State == TransferState.Queued)
                && vm.PendingTransferCount == 3 && vm.PendingBytes == 200 && vm.HasUnknownPendingSize
                && vm.HasBatchUploadNotice && vm.BatchUploadNotice.Length > 0 && files.UploadCalls == 0,
                "A batch duplicated a path, queued unsupported inputs, lost file sizes, or silently skipped rejected inputs.");
            vm.SelectedConnection = other;
            await vm.NavigateRemoteToAsync("/different-batch-target");
            vm.ToggleTransferQueueCommand.Execute(null);
            await vm.WaitForTransfersAsync();
            Require(vm.TransferJobs.Count(job => job.State == TransferState.Completed) == 2
                && vm.TransferJobs.Count(job => job.State == TransferState.Failed) == 1
                && vm.TransferJobs.All(job => job.Profile.Host == profile.Host)
                && files.UploadRequests.Select(request => request.Destination).SequenceEqual(new[] {
                    "/home/qa/" + Path.GetFileName(first), "/home/qa/" + Path.GetFileName(second) }),
                "A queued batch changed its active VPS or remote directory when selection and navigation changed.");

            vm.UpdateSelectedLocalSelection(new[] { QaRemoteFiles.Entry(Path.GetFileName(first), first),
                QaRemoteFiles.Entry(Path.GetFileName(second), second), QaRemoteFiles.Entry("deploy", Path.Combine(_workspace, "deploy"), true) });
            await vm.UploadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            Require(files.UploadCalls == 4, "Extended local selection did not enqueue each selected file exactly once.");
            vm.UpdateSelectedLocalSelection([]);
            vm.SelectedLocalEntry = null;
            interactions.UploadFileBatches.Enqueue([first, empty]);
            await vm.UploadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            Require(files.UploadCalls == 6 && vm.TransferJobs.Last().HasKnownSize,
                "The native multiple-file selection path lost a file or classified an empty file as unknown.");

            await vm.NavigateToAsync(_workspace);
            var window = new MainWindow(vm, initializeOnLoad: false);
            PrepareHiddenWindow(window);
            window.Show();
            try
            {
                window.NavigateTo("files");
                await SettleLayoutAsync(window);
                var grid = (DataGrid)window.FindName("LocalFilesGrid");
                Require(grid.SelectionMode == DataGridSelectionMode.Extended, "The local grid does not permit selecting several files.");
                grid.SelectedItems.Add(vm.LocalEntries.Single(entry => entry.FullPath == first));
                grid.SelectedItems.Add(vm.LocalEntries.Single(entry => entry.FullPath == second));
                Require(vm.SelectedLocalEntries.Count == 2, "The native extended selection did not reach the upload queue's selection snapshot.");
                await vm.UploadCommand.ExecuteAsync();
                await vm.WaitForTransfersAsync();
                Require(files.UploadCalls == 8, "Uploading the native multi-selection omitted or duplicated selected files.");
                var target = (UIElement)window.FindName("RemoteFilesPanel");
                var countBeforeDrop = vm.TransferJobs.Count;
                var callsBeforeDrop = files.UploadCalls;
                var data = new DataObject(DataFormats.FileDrop, new[] { first, second });
                var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null,
                    args: [data, DragDropKeyStates.None, DragDropEffects.Copy, target, new Point(20, 20)], culture: null)!;
                drop.RoutedEvent = DragDrop.PreviewDropEvent;
                target.RaiseEvent(drop);
                await WaitUntilAsync(() => vm.TransferJobs.Count == countBeforeDrop + 2);
                await vm.WaitForTransfersAsync();
                Require(drop.Handled && files.UploadCalls == callsBeforeDrop + 2,
                    "A native file-drop payload did not enqueue both files through the sequential queue.");
            }
            finally { window.Close(); }
            var countBeforeDisconnect = vm.TransferJobs.Count;
            var pendingValidation = vm.QueueUploadFilesAsync(Enumerable.Repeat(first, 200).ToArray());
            await vm.DisconnectCommand.ExecuteAsync();
            await pendingValidation;
            Require(!vm.HasActiveSession && vm.TransferJobs.Count == countBeforeDisconnect,
                "A stale batch validation enqueued new work after its owning VPS session disconnected.");
        }
        finally { await vm.ShutdownAsync(); }
    }

    private static void SetTransferExpectedSize(TransferItemViewModel job, long size)
        => typeof(TransferItemViewModel).GetMethod("SetExpectedSize", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, [size]);
}
