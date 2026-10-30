using System.IO;
using System.Reflection;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private Task VerifyTransferMetricsAsync()
    {
        const long mebibyte = 1024 * 1024;
        var time = new QaMonotonicTimeProvider();
        var job = CreateTimedTransfer(time);
        Require(job.BytesPerSecond == 0 && job.EstimatedRemaining is null && job.Elapsed == TimeSpan.Zero,
            "Queued work exposed a speed, estimate, or elapsed transfer time.");
        SetTransferState(job, TransferState.Running);
        PublishTransferProgress(job, new TransferProgress(0, 10 * mebibyte));
        time.Advance(TimeSpan.FromSeconds(1));
        PublishTransferProgress(job, new TransferProgress(mebibyte, 10 * mebibyte));
        Require(job.BytesPerSecond > 0 && double.IsFinite(job.BytesPerSecond)
            && job.EstimatedRemaining is { TotalSeconds: > 0 and < 30 }
            && job.Elapsed == TimeSpan.FromSeconds(1),
            "Actual byte samples did not produce a bounded speed and remaining-time estimate.");
        Require(Math.Abs(job.BytesPerSecond - mebibyte) < 0.5
            && Math.Abs(job.EstimatedRemaining!.Value.TotalSeconds - 9) < 0.01,
            "The one-second sample did not report 1 MiB/s and nine seconds remaining.");
        Require(job.SpeedLabel.Contains("/s", StringComparison.Ordinal) && job.RemainingTimeLabel != "—",
            "Transfer metrics did not expose readable speed and remaining-time labels.");
        time.Advance(TimeSpan.FromSeconds(1));
        PublishTransferProgress(job, new TransferProgress(3 * mebibyte, 10 * mebibyte));
        Require(job.BytesPerSecond > 0 && job.EstimatedRemaining is { TotalSeconds: > 0 and < 15 },
            "The estimate did not adapt to additional transferred bytes.");
        Require(Math.Abs(job.BytesPerSecond - 1.5 * mebibyte) < 0.5
            && Math.Abs(job.EstimatedRemaining!.Value.TotalSeconds - 14d / 3) < 0.01,
            "The second sample did not report the actual recent byte/time ratio and remaining duration.");
        time.Advance(TimeSpan.FromSeconds(8));
        RefreshTransferTiming(job);
        Require(job.BytesPerSecond == 0 && job.EstimatedRemaining is null && job.Elapsed == TimeSpan.FromSeconds(10),
            "A stalled transfer retained a stale speed or a misleading remaining-time estimate.");
        PublishTransferProgress(job, new TransferProgress(10 * mebibyte, 10 * mebibyte));
        Require(job.CanCancel && job.EstimatedRemaining is null && job.ProgressPercent < 100,
            "Copying all bytes disabled cancellation or claimed completion before finalization.");
        PublishTransferProgress(job, new TransferProgress(10 * mebibyte, 10 * mebibyte, IsFinalizing: true));
        Require(!job.CanCancel && job.EstimatedRemaining is null && job.SpeedLabel == "—",
            "The actual finalization phase retained an active-transfer estimate or cancellation action.");
        SetTransferState(job, TransferState.Completed);
        var elapsed = job.Elapsed;
        time.Advance(TimeSpan.FromMinutes(2));
        RefreshTransferTiming(job);
        Require(job.Elapsed == elapsed && job.ProgressPercent == 100 && job.EstimatedRemaining is null,
            "A completed transfer's timing kept advancing or completion regressed.");

        var empty = CreateTimedTransfer(new QaMonotonicTimeProvider());
        SetTransferState(empty, TransferState.Running);
        PublishTransferProgress(empty, new TransferProgress(0, 0));
        Require(empty.BytesPerSecond == 0 && empty.EstimatedRemaining is null && empty.CanCancel,
            "An empty file produced an invalid estimate or skipped cancellation before commit.");
        SetTransferState(empty, TransferState.Canceled);
        Require(empty.SpeedLabel == "—" && empty.EstimatedRemaining is null && empty.CanRetry,
            "Canceled work retained live metrics or lost its retry affordance.");
        return Task.CompletedTask;
    }

    private async Task VerifyTransferQueueControlsAsync()
    {
        var profile = CreateProfile();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new QaRemoteFiles();
        files.UploadHandler = async (_, _, progress, token) =>
        {
            progress?.Report(new TransferProgress(10, 100));
            if (files.UploadCalls == 1) await firstRelease.Task.WaitAsync(token);
            else await Task.Delay(Timeout.Infinite, token);
        };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile]), new RecordingInteractions(), service);
        try
        {
            await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
            await vm.UploadCommand.ExecuteAsync();
            await WaitUntilAsync(() => files.UploadCalls == 1);
            await vm.UploadCommand.ExecuteAsync();
            vm.ToggleTransferQueueCommand.Execute(null);
            Require(vm.IsTransferQueuePaused && vm.TransferJobs.First().State == TransferState.Running,
                "Pausing the queue canceled or paused the already running transfer.");
            await vm.UploadCommand.ExecuteAsync();
            firstRelease.SetResult();
            await vm.WaitForTransfersAsync();
            Require(files.UploadCalls == 1 && vm.TransferJobs.First().State == TransferState.Completed
                && vm.TransferJobs.Skip(1).All(job => job.State == TransferState.Queued),
                "The paused queue started another job or failed to let its active job finish.");
            vm.ClearCompletedTransfersCommand.Execute(null);
            Require(vm.TransferJobs.Count == 2 && vm.TransferJobs.All(job => job.State == TransferState.Queued),
                "Clearing completed transfers removed pending queue entries.");
            vm.ClearCompletedTransfersCommand.Execute(null);
            vm.CancelAllTransfersCommand.Execute(null);
            Require(vm.TransferJobs.All(job => job.State == TransferState.Canceled) && files.UploadCalls == 1,
                "Cancel all executed a paused job or left queued work pending.");
            vm.CancelAllTransfersCommand.Execute(null);
            await vm.UploadCommand.ExecuteAsync();
            Require(vm.TransferJobs.Last().State == TransferState.Queued && files.UploadCalls == 1,
                "Enqueuing into a paused queue started work unexpectedly.");
            vm.ToggleTransferQueueCommand.Execute(null);
            await WaitUntilAsync(() => files.UploadCalls == 2);
            await vm.UploadCommand.ExecuteAsync();
            vm.CancelAllTransfersCommand.Execute(null);
            await vm.WaitForTransfersAsync();
            Require(!vm.IsTransferQueuePaused && vm.TransferJobs.All(job => job.State == TransferState.Canceled)
                && files.UploadCalls == 2,
                "Cancel all failed to stop active and queued jobs without advancing the queue.");

            var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            files.UploadHandler = async (_, _, progress, _) =>
            {
                progress?.Report(new TransferProgress(100, 100, IsFinalizing: true));
                await commit.Task;
            };
            await vm.UploadCommand.ExecuteAsync();
            var finalizing = vm.TransferJobs.Last();
            await WaitUntilAsync(() => !finalizing.CanCancel);
            await vm.UploadCommand.ExecuteAsync();
            var neverStarted = vm.TransferJobs.Last();
            vm.CancelAllTransfersCommand.Execute(null);
            commit.SetResult();
            await vm.WaitForTransfersAsync();
            Require(finalizing.State == TransferState.Completed && neverStarted.State == TransferState.Canceled,
                "Cancel all reversed an already committed outcome or started a pending job.");
            vm.ClearCompletedTransfersCommand.Execute(null);
            Require(!vm.TransferJobs.Contains(finalizing) && vm.TransferJobs.Contains(neverStarted)
                && vm.TransferJobs.All(job => job.State == TransferState.Canceled),
                "Clearing completed work removed canceled history or retained successful rows.");
            files.UploadHandler = (_, _, _, token) => Task.Delay(Timeout.Infinite, token);
            await vm.UploadCommand.ExecuteAsync();
            vm.ToggleTransferQueueCommand.Execute(null);
            Require(vm.IsTransferQueuePaused, "The queue could not pause before disconnect cleanup.");
            await vm.DisconnectCommand.ExecuteAsync();
            Require(!vm.IsTransferQueuePaused, "Disconnect did not reset the session's queue pause state.");
        }
        finally { await vm.ShutdownAsync(); }
    }

    private async Task VerifyTransferRetryAsync()
    {
        var profile = CreateProfile();
        var other = profile with { Id = Guid.NewGuid(), Host = "other.example.invalid" };
        var otherAccount = profile with { Id = Guid.NewGuid(), Username = "other-account" };
        var files = new QaRemoteFiles { ExistsHandler = (_, _) => Task.FromResult(true) };
        var interactions = new RecordingInteractions { AllowOverwrite = true };
        files.UploadHandler = (_, _, progress, _) =>
        {
            if (files.UploadCalls == 1) throw new RemoteFileException(RemoteFileErrorCode.Failed, "The QA transfer failed before finalization.");
            progress?.Report(new TransferProgress(100, 100, IsFinalizing: true));
            return Task.CompletedTask;
        };
        var service = new QaRemoteConnectionService { ConnectHandler = (snapshot, _) =>
            Task.FromResult<IRemoteConnectionSession>(new QaRemoteConnectionSession(snapshot) { Files = files }) };
        var vm = CreateViewModel(new MemoryProfileStore([profile, other, otherAccount]), interactions, service);
        try
        {
            await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("deploy.ps1", Path.Combine(_workspace, "deploy.ps1"));
            await vm.UploadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            var failed = vm.TransferJobs.Single();
            Require(failed.State == TransferState.Failed && vm.RetryTransferCommand.CanExecute(failed),
                "A failed transfer did not allow a manual retry on its active VPS.");
            vm.SelectedConnection = other;
            await vm.NavigateRemoteToAsync("/new-directory");
            vm.ToggleTransferQueueCommand.Execute(null);
            vm.RetryTransferCommand.Execute(failed);
            var retry = vm.TransferJobs.Last();
            Require(retry.Id != failed.Id && retry.State == TransferState.Queued && retry.BytesTransferred == 0
                && retry.SourcePath == failed.SourcePath && retry.DestinationPath == failed.DestinationPath
                && retry.Profile.Host == profile.Host && retry.Direction == failed.Direction
                && failed.State == TransferState.Failed,
                "Retry modified the failed row, reused progress, or redirected its captured destination/profile.");
            vm.RetryTransferCommand.Execute(failed);
            Require(vm.TransferJobs.Count == 2, "Repeated retry clicks created duplicate pending transfers.");
            vm.ToggleTransferQueueCommand.Execute(null);
            await vm.WaitForTransfersAsync();
            Require(retry.State == TransferState.Completed && interactions.OverwriteCalls == 2
                && files.UploadRequests.Last().Destination == failed.DestinationPath && files.UploadRequests.Last().Overwrite,
                "Retry skipped overwrite confirmation or wrote into the newly navigated directory.");
            vm.ClearCompletedTransfersCommand.Execute(null);
            Require(vm.TransferJobs.Count == 1 && vm.TransferJobs.Single() == failed,
                "Clearing completed work erased the original failed transfer.");

            await vm.DisconnectCommand.ExecuteAsync();
            Require(!vm.RetryTransferCommand.CanExecute(failed), "Retry remained enabled without an active session.");
            await vm.ConnectCommand.ExecuteAsync(other);
            await vm.RemoteInitialization;
            Require(!vm.RetryTransferCommand.CanExecute(failed), "Retry targeted an unrelated connected VPS.");
            vm.RetryTransferCommand.Execute(failed);
            Require(vm.TransferJobs.Count == 1, "A forbidden retry created a job for another VPS.");
            await vm.DisconnectCommand.ExecuteAsync();
            await vm.ConnectCommand.ExecuteAsync(otherAccount);
            await vm.RemoteInitialization;
            Require(!vm.RetryTransferCommand.CanExecute(failed), "Retry crossed accounts on the same server.");
            await vm.DisconnectCommand.ExecuteAsync();
            await vm.ConnectCommand.ExecuteAsync(profile);
            await vm.RemoteInitialization;
            Require(vm.RetryTransferCommand.CanExecute(failed), "Reconnect to the original VPS did not restore manual retry.");
            interactions.AllowOverwrite = false;
            vm.RetryTransferCommand.Execute(failed);
            await vm.WaitForTransfersAsync();
            var declined = vm.TransferJobs.Last();
            Require(declined.State == TransferState.Canceled && files.UploadCalls == 2,
                "Retry reused an earlier overwrite decision after reconnecting.");
            interactions.AllowOverwrite = true;
            vm.RetryTransferCommand.Execute(declined);
            await vm.WaitForTransfersAsync();
            Require(vm.TransferJobs.Last().State == TransferState.Completed && vm.TransferJobs.Last().Id != declined.Id,
                "A canceled transfer could not be retried as a fresh job.");

            var missingPath = Path.Combine(_workspace, "removed-before-retry.bin");
            await File.WriteAllTextAsync(missingPath, "temporary QA source");
            files.UploadHandler = (_, _, _, _) => throw new RemoteFileException(RemoteFileErrorCode.Failed, "The QA transfer failed.");
            vm.SelectedLocalEntry = QaRemoteFiles.Entry("removed-before-retry.bin", missingPath);
            await vm.UploadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            var missingSource = vm.TransferJobs.Last();
            File.Delete(missingPath);
            files.UploadHandler = (source, _, _, _) =>
            {
                using var input = File.OpenRead(source);
                return Task.CompletedTask;
            };
            vm.RetryTransferCommand.Execute(missingSource);
            await vm.WaitForTransfersAsync();
            Require(vm.TransferJobs.Last().State == TransferState.Failed && vm.TransferJobs.Last().Error.Length > 0,
                "Retry of a removed local source silently succeeded or lost its actionable error.");

            var downloadTarget = Path.Combine(_workspace, "retry-download.txt");
            await File.WriteAllTextAsync(downloadTarget, "previous local contents");
            interactions.DownloadFiles.Enqueue(downloadTarget);
            files.DownloadHandler = (_, _, _, _) => throw new RemoteFileException(RemoteFileErrorCode.Failed, "The QA download failed.");
            vm.SelectedRemoteEntry = vm.RemoteEntries.First(entry => !entry.IsDirectory);
            await vm.DownloadCommand.ExecuteAsync();
            await vm.WaitForTransfersAsync();
            var failedDownload = vm.TransferJobs.Last();
            var confirmations = interactions.OverwriteCalls;
            files.DownloadHandler = async (_, destination, progress, _) =>
            {
                await File.WriteAllTextAsync(destination, "retried download contents");
                progress?.Report(new TransferProgress(25, 25, IsFinalizing: true));
            };
            await vm.NavigateRemoteToAsync("/another-directory");
            vm.RetryTransferCommand.Execute(failedDownload);
            await vm.WaitForTransfersAsync();
            Require(vm.TransferJobs.Last().State == TransferState.Completed
                && files.DownloadRequests.Last().Source == failedDownload.SourcePath
                && files.DownloadRequests.Last().Destination == downloadTarget
                && files.DownloadRequests.Last().Overwrite && interactions.OverwriteCalls == confirmations + 1
                && await File.ReadAllTextAsync(downloadTarget) == "retried download contents",
                "Download retry chose a different source/destination or skipped renewed local overwrite approval.");
        }
        finally { await vm.ShutdownAsync(); }
    }

    private static TransferItemViewModel CreateTimedTransfer(TimeProvider time)
        => (TransferItemViewModel)Activator.CreateInstance(typeof(TransferItemViewModel), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [CreateProfile(), @"C:\QA\source.bin", "/home/qa/source.bin", "source.bin", TransferDirection.Upload,
                CancellationToken.None, time], culture: null)!;

    private static void SetTransferState(TransferItemViewModel job, TransferState state)
        => typeof(TransferItemViewModel).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, [state, string.Empty]);
    private static void PublishTransferProgress(TransferItemViewModel job, TransferProgress progress)
        => typeof(TransferItemViewModel).GetMethod("UpdateProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, [progress]);
    private static void RefreshTransferTiming(TransferItemViewModel job)
        => typeof(TransferItemViewModel).GetMethod("RefreshTiming", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(job, null);
}

internal sealed class QaMonotonicTimeProvider : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
}
