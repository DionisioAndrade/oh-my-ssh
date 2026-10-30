using System.IO;
using System.Windows.Threading;
using OhMySsh.Core.Localization;
using OhMySsh.Core.Models;
using RemotePaths = OhMySsh.Core.Models.RemotePath;

namespace OhMySsh.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private IReadOnlyList<FileEntry> _selectedRemoteEntries = [];
    private UiMessage[] _batchDownloadNotices = [];
    private long _downloadBatchVersion;
    private bool _isPreparingDownloadBatch;

    public IReadOnlyList<FileEntry> SelectedRemoteEntries => _selectedRemoteEntries;
    public string BatchDownloadNotice => string.Join(' ', _batchDownloadNotices.Select(message => message.Text));
    public bool HasBatchDownloadNotice => _batchDownloadNotices.Length > 0;

    private bool HasDownloadableRemoteSelection => _selectedRemoteEntries.Count > 0
        ? _selectedRemoteEntries.Any(entry => !entry.IsDirectory)
        : SelectedRemoteEntry is { IsDirectory: false };

    /// <summary>Snapshots remote selection; successful listing replacement and disconnect clear it.</summary>
    public void UpdateSelectedRemoteSelection(IEnumerable<FileEntry> entries)
    {
        _selectedRemoteEntries = Array.AsReadOnly(entries.ToArray());
        if (_selectedRemoteEntries.Count == 0) SelectedRemoteEntry = null;
        else if (SelectedRemoteEntry is null || !_selectedRemoteEntries.Contains(SelectedRemoteEntry))
            SelectedRemoteEntry = _selectedRemoteEntries[0];
        OnPropertyChanged(nameof(SelectedRemoteEntries));
        DownloadCommand?.NotifyCanExecuteChanged();
    }

    private void SetBatchDownloadNotice(params UiMessage[] notices)
    {
        _batchDownloadNotices = notices;
        OnPropertyChanged(nameof(BatchDownloadNotice));
        OnPropertyChanged(nameof(HasBatchDownloadNotice));
    }

    private Task QueueDownloadAsync()
    {
        if (_remoteContext is not { } context || !CanQueueTransfer()) return Task.CompletedTask;
        var entries = _selectedRemoteEntries.Count > 0 ? _selectedRemoteEntries.ToArray()
            : SelectedRemoteEntry is { } selected ? [selected] : [];
        if (!entries.Any(entry => !entry.IsDirectory)) return Task.CompletedTask;

        // Capture every source, size hint and connection before a native dialog can run a nested message loop.
        var profile = context.Session.Profile with { };
        var initialDirectory = _loadedLocalPath;
        var batchVersion = _downloadBatchVersion;
        return QueueCapturedDownloadAsync(context, profile, entries, initialDirectory, batchVersion);
    }

    private async Task QueueCapturedDownloadAsync(RemoteSessionContext context, ConnectionProfile profile,
        IReadOnlyList<FileEntry> entries, string initialDirectory, long batchVersion)
    {
        _isPreparingDownloadBatch = true;
        SetBatchUploadNotice();
        SetBatchDownloadNotice();
        RefreshTransferCommandStates();
        try
        {
            if (!IsCurrentDownloadBatch(context, batchVersion)) return;
            var token = context.Lifetime.Token;
            var validation = Task.Run(() => PrepareDownloadSelection(entries, token), token);
            ObserveLateFailure(validation);
            TrackDownloadValidation(context, validation);
            DownloadBatch batch;
            try { batch = await validation.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            if (!IsCurrentDownloadBatch(context, batchVersion)) return;
            if (batch.Files.Count == 0)
            {
                PublishDownloadBatchNotice(batch);
                return;
            }

            if (entries.Count == 1)
            {
                var file = batch.Files[0];
                var destination = _interactions.PickDownloadFile(file.Name, initialDirectory);
                if (string.IsNullOrWhiteSpace(destination) || !IsCurrentDownloadBatch(context, batchVersion)) return;
                var job = CreateDownloadJob(context, profile, file, Path.GetFullPath(destination));
                EnqueueTransfer(context, job);
                if (IsCurrentDownloadBatch(context, batchVersion)) PublishDownloadBatchNotice(batch);
                return;
            }

            var folder = _interactions.PickDownloadFolder(initialDirectory);
            if (string.IsNullOrWhiteSpace(folder) || !IsCurrentDownloadBatch(context, batchVersion)) return;
            var destinationValidation = Task.Run(() => PrepareDownloadDestinations(batch, folder, token), token);
            ObserveLateFailure(destinationValidation);
            TrackDownloadValidation(context, destinationValidation);
            DownloadDestinationBatch destinations;
            try { destinations = await destinationValidation.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            if (!IsCurrentDownloadBatch(context, batchVersion)) return;

            // Bound UI work without introducing another worker: all jobs use the existing sequential queue.
            for (var start = 0; start < destinations.Files.Count; start += 64)
            {
                if (!IsCurrentDownloadBatch(context, batchVersion)) return;
                var previousSuppression = _suppressTransferQueueStart;
                var previousBatchAdding = _batchAddingTransfers;
                _suppressTransferQueueStart = true;
                _batchAddingTransfers = true;
                try
                {
                    foreach (var file in destinations.Files.Skip(start).Take(64))
                    {
                        if (!IsCurrentDownloadBatch(context, batchVersion)) return;
                        EnqueueTransfer(context, CreateDownloadJob(context, profile, file.Source, file.Destination));
                    }
                }
                finally
                {
                    _batchAddingTransfers = previousBatchAdding;
                    _suppressTransferQueueStart = previousSuppression;
                    NotifyTransferState(refreshView: true);
                    StartTransferQueue();
                }
                if (start + 64 < destinations.Files.Count) await Dispatcher.Yield(DispatcherPriority.Background);
            }

            if (IsCurrentDownloadBatch(context, batchVersion))
                PublishDownloadBatchNotice(new DownloadBatch(destinations.Files.Select(file => file.Source).ToArray(),
                    batch.DirectoryCount, destinations.InvalidCount, destinations.DuplicateCount));
        }
        finally
        {
            _isPreparingDownloadBatch = false;
            RefreshTransferCommandStates();
        }
    }

    private bool IsCurrentDownloadBatch(RemoteSessionContext context, long version)
        => IsCurrent(context) && version == _downloadBatchVersion;

    private static void TrackDownloadValidation(RemoteSessionContext context, Task validation)
    {
        // A native picker may itself await disconnect. Retirement only waits for background validation,
        // never the picker-spanning operation, so that nested message loop cannot create a circular wait.
        context.Operations.RemoveAll(task => task.IsCompleted);
        context.Operations.Add(validation);
    }

    private static TransferItemViewModel CreateDownloadJob(RemoteSessionContext context, ConnectionProfile profile,
        DownloadCandidate file, string destination)
    {
        var job = new TransferItemViewModel(profile, file.Path, destination, file.Name,
            TransferDirection.Download, context.Lifetime.Token);
        job.SetExpectedSize(Math.Max(0, file.Size));
        return job;
    }

    private void PublishDownloadBatchNotice(DownloadBatch batch)
    {
        var notices = new List<UiMessage>();
        if (batch.Files.Count > 0) notices.Add(new UiMessage(batch.Files.Count == 1 ? "Vm.BatchDownload.File" : "Vm.BatchDownload.Files", batch.Files.Count));
        if (batch.DirectoryCount > 0) notices.Add(new UiMessage(batch.DirectoryCount == 1 ? "Vm.BatchDownload.Directory" : "Vm.BatchDownload.Directories", batch.DirectoryCount));
        if (batch.InvalidCount > 0) notices.Add(new UiMessage(batch.InvalidCount == 1 ? "Vm.BatchDownload.Invalid" : "Vm.BatchDownload.Invalids", batch.InvalidCount));
        if (batch.DuplicateCount > 0) notices.Add(new UiMessage(batch.DuplicateCount == 1 ? "Vm.BatchDownload.Duplicate" : "Vm.BatchDownload.Duplicates", batch.DuplicateCount));
        SetBatchDownloadNotice(notices.ToArray());
        SetStatus("Vm.Status.BatchDownloadQueued", batch.DirectoryCount + batch.InvalidCount + batch.DuplicateCount > 0 ? "Info" : "Success",
            batch.Files.Count, batch.DirectoryCount, batch.InvalidCount, batch.DuplicateCount);
    }

    private static DownloadBatch PrepareDownloadSelection(IReadOnlyList<FileEntry> entries, CancellationToken token)
    {
        var files = new List<DownloadCandidate>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = 0;
        var invalid = 0;
        var duplicates = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory) { directories++; continue; }
            try
            {
                var name = RemotePaths.GetSafeLocalFileName(entry.Name);
                // Validate remote syntax while retaining the exact, case-sensitive listed source path.
                _ = RemotePaths.Normalize(entry.FullPath);
                if (sources.Contains(entry.FullPath) || names.Contains(name)) { duplicates++; continue; }
                sources.Add(entry.FullPath);
                names.Add(name);
                files.Add(new DownloadCandidate(entry.FullPath, name, entry.Size));
            }
            catch (RemoteFileException exception) when (exception.Code == RemoteFileErrorCode.UnsafePath)
            {
                invalid++;
            }
        }
        return new DownloadBatch(files, directories, invalid, duplicates);
    }

    private static DownloadDestinationBatch PrepareDownloadDestinations(DownloadBatch batch, string folder, CancellationToken token)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var files = new List<DownloadDestination>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = batch.InvalidCount;
        var duplicates = batch.DuplicateCount;
        foreach (var file in batch.Files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Validate the single component before combining it with a user-selected local directory.
                var destination = Path.GetFullPath(Path.Combine(directory, RemotePaths.GetSafeLocalFileName(file.Name)));
                if (!string.Equals(Path.GetDirectoryName(destination), directory, StringComparison.OrdinalIgnoreCase))
                {
                    invalid++;
                    continue;
                }
                if (!destinations.Add(destination)) { duplicates++; continue; }
                files.Add(new DownloadDestination(file, destination));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException
                or RemoteFileException { Code: RemoteFileErrorCode.UnsafePath })
            {
                invalid++;
            }
        }
        return new DownloadDestinationBatch(files, invalid, duplicates);
    }

    private sealed record DownloadCandidate(string Path, string Name, long Size);
    private sealed record DownloadBatch(IReadOnlyList<DownloadCandidate> Files, int DirectoryCount, int InvalidCount, int DuplicateCount);
    private sealed record DownloadDestination(DownloadCandidate Source, string Destination);
    private sealed record DownloadDestinationBatch(IReadOnlyList<DownloadDestination> Files, int InvalidCount, int DuplicateCount);
}
