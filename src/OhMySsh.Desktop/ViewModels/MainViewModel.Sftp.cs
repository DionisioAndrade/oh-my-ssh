using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Infrastructure.Persistence;
using RemotePaths = OhMySsh.Core.Models.RemotePath;

namespace OhMySsh.Desktop.ViewModels;

public enum TransferFilter { All, Active, Completed, Failed, Canceled }
public sealed record TransferFilterOption(TransferFilter Filter, string Label);

public sealed partial class MainViewModel
{
    private readonly ReplaceableObservableCollection<FileEntry> _remoteEntries = [];
    private readonly Dictionary<TransferItemViewModel, RemoteSessionContext> _transferContexts = [];
    private RemoteSessionContext? _remoteContext;
    private string _remotePath = string.Empty;
    private string _loadedRemotePath = string.Empty;
    private string _remoteError = string.Empty;
    private FileEntry? _selectedRemoteEntry;
    private bool _isRemoteLoading;
    private bool _remoteRequiresReconnect;
    private bool _hasRemoteDirectory;
    private long _remoteRequestVersion;
    private Task _remoteInitializationTask = Task.CompletedTask;
    private Task _remoteRetirementTask = Task.CompletedTask;
    private Task _queueWorkerTask = Task.CompletedTask;
    private bool _isProcessingTransferQueue;
    private bool _isTransferQueuePaused;
    private bool _suppressTransferQueueStart;
    private DispatcherTimer? _transferTimingTimer;
    private TransferItemViewModel? _timedTransfer;
    private IReadOnlyList<FileEntry> _selectedLocalEntries = [];
    private TransferFilter _selectedTransferFilter;
    private string _batchUploadNotice = string.Empty;
    private bool _refreshingTransferView;
    private bool _batchAddingTransfers;
    private long _uploadBatchVersion;

    public ObservableCollection<FileEntry> RemoteEntries => _remoteEntries;
    public ObservableCollection<TransferItemViewModel> TransferJobs { get; } = [];
    public ICollectionView FilteredTransferJobs { get; private set; } = null!;
    public IReadOnlyList<TransferFilterOption> TransferFilters { get; } =
    [
        new(TransferFilter.All, "Todas"), new(TransferFilter.Active, "Em andamento"),
        new(TransferFilter.Completed, "Concluídas"), new(TransferFilter.Failed, "Falhas"),
        new(TransferFilter.Canceled, "Canceladas")
    ];
    public TransferFilter SelectedTransferFilter
    {
        get => _selectedTransferFilter;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref _selectedTransferFilter, value)) return;
            RefreshTransferView();
        }
    }
    public bool HasVisibleTransfers => FilteredTransferJobs is { IsEmpty: false };
    public string TransferEmptyMessage => HasTransfers
        ? "Nenhuma transferência neste filtro."
        : "Nenhuma transferência na fila. Envie ou arraste arquivos para começar.";
    public IReadOnlyList<FileEntry> SelectedLocalEntries => _selectedLocalEntries;
    public string BatchUploadNotice
    {
        get => _batchUploadNotice;
        private set
        {
            if (SetProperty(ref _batchUploadNotice, value)) OnPropertyChanged(nameof(HasBatchUploadNotice));
        }
    }
    public bool HasBatchUploadNotice => BatchUploadNotice.Length > 0;
    public AsyncRelayCommand RefreshRemoteCommand { get; private set; } = null!;
    public AsyncRelayCommand GoUpRemoteCommand { get; private set; } = null!;
    public AsyncRelayCommand NavigateRemoteCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenRemoteEntryCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadFilesCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadCommand { get; private set; } = null!;
    public RelayCommand CancelTransferCommand { get; private set; } = null!;
    public RelayCommand ToggleTransferQueueCommand { get; private set; } = null!;
    public RelayCommand CancelAllTransfersCommand { get; private set; } = null!;
    public RelayCommand ClearCompletedTransfersCommand { get; private set; } = null!;
    public RelayCommand RetryTransferCommand { get; private set; } = null!;
    public RelayCommand ShowTransferDetailsCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenTransferFolderCommand { get; private set; } = null!;
    public AsyncRelayCommand RevealDownloadedFileCommand { get; private set; } = null!;
    public RelayCommand CopyRemotePathCommand { get; private set; } = null!;

    public string RemotePath { get => _remotePath; set => SetProperty(ref _remotePath, value ?? string.Empty); }
    public FileEntry? SelectedRemoteEntry
    {
        get => _selectedRemoteEntry;
        set
        {
            if (SetProperty(ref _selectedRemoteEntry, value)) DownloadCommand?.NotifyCanExecuteChanged();
        }
    }
    public bool IsRemoteLoading
    {
        get => _isRemoteLoading;
        private set
        {
            if (SetProperty(ref _isRemoteLoading, value)) NotifyRemoteState();
        }
    }
    public string RemoteError
    {
        get => _remoteError;
        private set
        {
            if (SetProperty(ref _remoteError, value)) NotifyRemoteState();
        }
    }
    public bool HasRemoteError => RemoteError.Length > 0;
    public bool HasRemoteEntries => RemoteEntries.Count > 0;
    public bool HasRemoteFileService => _remoteContext is not null && HasActiveSession && !_remoteRequiresReconnect;
    public bool IsRemoteEmpty => _hasRemoteDirectory && !IsRemoteLoading && !HasRemoteError && !HasRemoteEntries;
    public string RemoteSummary => _hasRemoteDirectory
        ? $"{RemoteEntries.Count} itens · {RemoteEntries.Count(item => item.IsDirectory)} pastas · {RemoteEntries.Count(item => !item.IsDirectory)} arquivos"
        : IsRemoteLoading ? "Carregando arquivos remotos…" : "SFTP";
    public string RemoteStateMessage => IsRemoteLoading ? "Carregando arquivos remotos…"
        : HasRemoteError ? RemoteError : IsRemoteEmpty ? "Esta pasta está vazia."
        : HasActiveSession ? "Os arquivos remotos aparecerão aqui." : "Conecte uma VPS para explorar seus arquivos.";
    public bool HasTransfers => TransferJobs.Count > 0;
    public int TransferCount => TransferJobs.Count;
    public int PendingTransferCount => TransferJobs.Count(IsPendingTransfer);
    public bool HasUnknownPendingSize => TransferJobs.Any(job => IsPendingTransfer(job) && !job.HasKnownSize);
    public long PendingBytes
    {
        get
        {
            long sum = 0;
            foreach (var job in TransferJobs.Where(job => IsPendingTransfer(job) && job.HasKnownSize))
            {
                var remaining = Math.Max(0, job.TotalBytes - job.BytesTransferred);
                if (remaining > long.MaxValue - sum) return long.MaxValue;
                sum += remaining;
            }
            return sum;
        }
    }
    public string PendingBytesLabel => HasUnknownPendingSize
        ? PendingBytes > 0 ? $"{FormatPendingBytes(PendingBytes)} + tamanho desconhecido" : "Tamanho a calcular"
        : FormatPendingBytes(PendingBytes);
    public string PendingSummary => PendingTransferCount == 0 ? "Nenhum arquivo pendente"
        : $"{PendingTransferCount} {(PendingTransferCount == 1 ? "arquivo pendente" : "arquivos pendentes")} · {PendingBytesLabel}";
    public bool IsTransferQueuePaused
    {
        get => _isTransferQueuePaused;
        private set
        {
            if (!SetProperty(ref _isTransferQueuePaused, value)) return;
            OnPropertyChanged(nameof(QueuePauseLabel));
            NotifyTransferState();
        }
    }
    public string QueuePauseLabel => IsTransferQueuePaused ? "Continuar fila" : "Pausar fila";
    public string TransferSummary => $"{(IsTransferQueuePaused ? "Fila pausada · " : string.Empty)}{TransferJobs.Count(item => item.State == TransferState.Queued)} na fila · {TransferJobs.Count(item => item.State == TransferState.Running)} em andamento";
    public Task RemoteInitialization => _remoteInitializationTask;
    public Task WaitForTransfersAsync() => _queueWorkerTask;

    private void InitializeRemoteCommands()
    {
        FilteredTransferJobs = new ListCollectionView(TransferJobs) { Filter = MatchesTransferFilter };
        RefreshRemoteCommand = new AsyncRelayCommand(RefreshRemoteAsync, CanBrowseRemote, ReportRemoteFailure);
        GoUpRemoteCommand = new AsyncRelayCommand(() => NavigateRemoteToAsync(RemotePaths.GetParent(_loadedRemotePath)),
            () => CanBrowseRemote() && _hasRemoteDirectory && _loadedRemotePath != "/", ReportRemoteFailure);
        NavigateRemoteCommand = new AsyncRelayCommand(() => NavigateRemoteToAsync(RemotePath), CanBrowseRemote, ReportRemoteFailure);
        OpenRemoteEntryCommand = new AsyncRelayCommand(parameter => NavigateRemoteToAsync(((FileEntry)parameter!).FullPath),
            parameter => CanBrowseRemote() && parameter is FileEntry { IsDirectory: true }, ReportRemoteFailure);
        UploadCommand = new AsyncRelayCommand(QueueUploadAsync, CanQueueTransfer, ReportRemoteFailure);
        UploadFilesCommand = new AsyncRelayCommand(parameter => QueueUploadFilesAsync((IEnumerable<string>)parameter!),
            parameter => parameter is IEnumerable<string> && CanQueueTransfer(), ReportRemoteFailure);
        DownloadCommand = new AsyncRelayCommand(QueueDownloadAsync,
            () => CanQueueTransfer() && SelectedRemoteEntry is { IsDirectory: false }, ReportRemoteFailure);
        CancelTransferCommand = new RelayCommand(parameter => CancelTransfer((TransferItemViewModel)parameter!),
            parameter => parameter is TransferItemViewModel { CanCancel: true } job && TransferJobs.Contains(job));
        ToggleTransferQueueCommand = new RelayCommand(ToggleTransferQueue, CanBrowseRemote);
        CancelAllTransfersCommand = new RelayCommand(CancelAllTransfers,
            () => TransferJobs.Any(job => job.CanCancel));
        ClearCompletedTransfersCommand = new RelayCommand(ClearCompletedTransfers,
            () => TransferJobs.Any(job => job.State == TransferState.Completed));
        RetryTransferCommand = new RelayCommand(parameter => RetryTransfer((TransferItemViewModel)parameter!),
            parameter => parameter is TransferItemViewModel job && CanRetryTransfer(job));
        ShowTransferDetailsCommand = new RelayCommand(parameter => RunTransferAction(() => _interactions.ShowTransferDetails((TransferItemViewModel)parameter!)),
            parameter => parameter is TransferItemViewModel { State: TransferState.Failed, HasFailureDetails: true } job && TransferJobs.Contains(job));
        OpenTransferFolderCommand = new AsyncRelayCommand(parameter => OpenTransferFolderAsync((TransferItemViewModel)parameter!),
            parameter => parameter is TransferItemViewModel { IsCompleted: true } job && TransferJobs.Contains(job), ReportTransferActionFailure);
        RevealDownloadedFileCommand = new AsyncRelayCommand(parameter => RevealDownloadedFileAsync((TransferItemViewModel)parameter!),
            parameter => parameter is TransferItemViewModel { IsCompletedDownload: true } job && TransferJobs.Contains(job), ReportTransferActionFailure);
        CopyRemotePathCommand = new RelayCommand(parameter => RunTransferAction(() =>
            _interactions.CopyToClipboard(((TransferItemViewModel)parameter!).RemotePath)),
            parameter => parameter is TransferItemViewModel { IsCompleted: true } job && TransferJobs.Contains(job));
        RemoteEntries.CollectionChanged += (_, _) => NotifyRemoteState();
        TransferJobs.CollectionChanged += (_, _) =>
        {
            if (!_batchAddingTransfers) NotifyTransferState(refreshView: true);
        };
        LocalEntries.CollectionChanged += (_, _) => UpdateSelectedLocalSelection([]);
    }

    private void StartRemoteSession(IRemoteConnectionSession session)
    {
        _remoteRequiresReconnect = false;
        if (session.Files is not { } files)
        {
            RemoteError = "SFTP is unavailable for this connection.";
            NotifyRemoteState();
            return;
        }

        var context = new RemoteSessionContext(session, files);
        _remoteContext = context;
        NotifyRemoteState();
        _remoteInitializationTask = InitializeRemoteDirectoryAsync(context);
        context.Operations.Add(_remoteInitializationTask);
    }

    private async Task InitializeRemoteDirectoryAsync(RemoteSessionContext context)
    {
        var version = ++_remoteRequestVersion;
        IsRemoteLoading = true;
        RemoteError = string.Empty;
        try
        {
            var homeTask = context.Files.GetHomeDirectoryAsync(context.Lifetime.Token);
            ObserveLateFailure(homeTask);
            var home = await homeTask.WaitAsync(context.Lifetime.Token);
            if (IsCurrent(context) && version == _remoteRequestVersion)
                await NavigateRemoteForContextAsync(context, home);
        }
        catch (OperationCanceledException) when (context.Lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrent(context) && version == _remoteRequestVersion) ReportRemoteFailure(exception);
        }
        finally
        {
            if (IsCurrent(context) && version == _remoteRequestVersion) IsRemoteLoading = false;
        }
    }

    private Task RefreshRemoteAsync()
    {
        if (_remoteContext is not { } context) return Task.CompletedTask;
        return _hasRemoteDirectory ? NavigateRemoteToAsync(_loadedRemotePath) : RestartRemoteInitialization(context);
    }

    private Task RestartRemoteInitialization(RemoteSessionContext context)
    {
        context.Operations.RemoveAll(task => task.IsCompleted);
        _remoteInitializationTask = InitializeRemoteDirectoryAsync(context);
        context.Operations.Add(_remoteInitializationTask);
        return _remoteInitializationTask;
    }

    public Task NavigateRemoteToAsync(string path)
    {
        if (_remoteContext is not { } context || !IsCurrent(context)) return Task.CompletedTask;
        var operation = NavigateRemoteForContextAsync(context, path);
        context.Operations.RemoveAll(task => task.IsCompleted);
        context.Operations.Add(operation);
        return operation;
    }

    private async Task NavigateRemoteForContextAsync(RemoteSessionContext context, string path)
    {
        context.NavigationCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Lifetime.Token);
        context.NavigationCancellation = cancellation;
        var version = ++_remoteRequestVersion;
        IsRemoteLoading = true;
        RemoteError = string.Empty;
        Task<IReadOnlyList<FileEntry>>? listingTask = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Enter a remote directory path.");
            var resolved = RemotePaths.Normalize(path.StartsWith('/') ? path : $"{(_loadedRemotePath.Length == 0 ? "/" : _loadedRemotePath).TrimEnd('/')}/{path}");
            listingTask = context.Files.GetEntriesAsync(resolved, cancellation.Token);
            var entries = await listingTask.WaitAsync(cancellation.Token);
            if (!IsCurrent(context) || version != _remoteRequestVersion || cancellation.IsCancellationRequested) return;
            _remoteEntries.ReplaceAll(entries);
            _loadedRemotePath = resolved;
            RemotePath = resolved;
            _hasRemoteDirectory = true;
            SelectedRemoteEntry = null;
            NotifyRemoteState();
            SetStatus($"Remote folder refreshed · {entries.Count} items", "Success");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrent(context) && version == _remoteRequestVersion)
            {
                RemotePath = _loadedRemotePath;
                ReportRemoteFailure(exception);
            }
        }
        finally
        {
            if (listingTask is not null) ObserveLateFailure(listingTask);
            if (ReferenceEquals(context.NavigationCancellation, cancellation)) context.NavigationCancellation = null;
            if (IsCurrent(context) && version == _remoteRequestVersion) IsRemoteLoading = false;
        }
    }

    private async Task QueueUploadAsync()
    {
        if (_remoteContext is not { } context || !CanQueueTransfer()) return;
        var remoteDirectory = _loadedRemotePath;
        string[] paths = SelectedLocalEntries.Count > 0 ? SelectedLocalEntries.Select(entry => entry.FullPath).ToArray()
            : SelectedLocalEntry is { IsDirectory: false } selected ? [selected.FullPath]
            : _interactions.PickUploadFiles(_loadedLocalPath).ToArray();
        if (paths.Length == 0 || !IsCurrent(context)) return;
        await QueueUploadFilesForContextAsync(context, remoteDirectory, paths);
    }

    /// <summary>Snapshots local selection; replacing the local listing clears it independently of the view.</summary>
    public void UpdateSelectedLocalSelection(IEnumerable<FileEntry> entries)
    {
        _selectedLocalEntries = entries.ToArray();
        OnPropertyChanged(nameof(SelectedLocalEntries));
    }

    /// <summary>Queues one batch against the active session and loaded directory captured before validation.</summary>
    public Task QueueUploadFilesAsync(IEnumerable<string> paths)
    {
        if (_remoteContext is not { } context || !CanQueueTransfer()) return Task.CompletedTask;
        return QueueUploadFilesForContextAsync(context, _loadedRemotePath, paths.ToArray());
    }

    private Task QueueUploadFilesForContextAsync(RemoteSessionContext context, string remoteDirectory, IReadOnlyList<string> paths)
    {
        context.Operations.RemoveAll(task => task.IsCompleted);
        var operation = ValidateAndQueueUploadFilesAsync(context, remoteDirectory, paths);
        context.Operations.Add(operation);
        return operation;
    }

    private async Task ValidateAndQueueUploadFilesAsync(RemoteSessionContext context, string remoteDirectory, IReadOnlyList<string> paths)
    {
        var batchVersion = _uploadBatchVersion;
        BatchUploadNotice = string.Empty;
        var validation = Task.Run(() => PrepareUploadBatch(paths, context.Lifetime.Token), context.Lifetime.Token);
        ObserveLateFailure(validation);
        UploadBatch batch;
        try { batch = await validation.WaitAsync(context.Lifetime.Token); }
        catch (OperationCanceledException) when (context.Lifetime.IsCancellationRequested) { return; }
        if (!IsCurrent(context) || batchVersion != _uploadBatchVersion) return;

        // Metadata work stays off the UI thread; bounded insertion chunks also keep large drops responsive.
        for (var start = 0; start < batch.Files.Count; start += 64)
        {
            if (!IsCurrent(context) || batchVersion != _uploadBatchVersion) return;
            var previousSuppression = _suppressTransferQueueStart;
            _suppressTransferQueueStart = true;
            _batchAddingTransfers = true;
            try
            {
                foreach (var file in batch.Files.Skip(start).Take(64))
                {
                    var job = new TransferItemViewModel(context.Session.Profile, file.Path,
                        RemotePaths.Combine(remoteDirectory, file.Name), file.Name, TransferDirection.Upload, context.Lifetime.Token);
                    if (file.Size is { } size) job.SetExpectedSize(size);
                    EnqueueTransfer(context, job);
                }
            }
            finally
            {
                _batchAddingTransfers = false;
                _suppressTransferQueueStart = previousSuppression;
                NotifyTransferState(refreshView: true);
                StartTransferQueue();
            }
            if (start + 64 < batch.Files.Count) await Dispatcher.Yield(DispatcherPriority.Background);
        }

        if (!IsCurrent(context) || batchVersion != _uploadBatchVersion) return;

        var notices = new List<string>();
        if (batch.Files.Count > 0) notices.Add($"{batch.Files.Count} {(batch.Files.Count == 1 ? "arquivo adicionado à fila" : "arquivos adicionados à fila")}.");
        if (batch.DirectoryCount > 0) notices.Add($"{batch.DirectoryCount} {(batch.DirectoryCount == 1 ? "pasta ignorada" : "pastas ignoradas")}; o envio de pastas ainda não está disponível.");
        if (batch.InvalidCount > 0) notices.Add($"{batch.InvalidCount} {(batch.InvalidCount == 1 ? "caminho inválido ignorado" : "caminhos inválidos ignorados")}.");
        if (batch.DuplicateCount > 0) notices.Add($"{batch.DuplicateCount} {(batch.DuplicateCount == 1 ? "arquivo repetido ignorado" : "arquivos repetidos ignorados")}.");
        BatchUploadNotice = string.Join(' ', notices);
        SetStatus($"Upload batch queued: {batch.Files.Count} files; {batch.DirectoryCount} directories, {batch.InvalidCount} invalid paths, and {batch.DuplicateCount} duplicates skipped.",
            batch.DirectoryCount + batch.InvalidCount > 0 ? "Info" : "Success");
    }

    private static UploadBatch PrepareUploadBatch(IReadOnlyList<string> paths, CancellationToken token)
    {
        var files = new List<UploadCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = 0;
        var invalid = 0;
        var duplicates = 0;
        foreach (var value in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(value)) { invalid++; continue; }
                var path = Path.GetFullPath(value);
                if (!seen.Add(path)) { duplicates++; continue; }
                var name = Path.GetFileName(path);
                if (name.Length == 0) { directories++; continue; }
                long? size = null;
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) { directories++; continue; }
                    size = new FileInfo(path).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Keep a valid individual path in the queue so a missing/inaccessible source has a per-file result.
                }
                files.Add(new UploadCandidate(path, name, size));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                invalid++;
            }
        }
        return new UploadBatch(files, directories, invalid, duplicates);
    }

    private async Task QueueDownloadAsync()
    {
        if (_remoteContext is not { } context || SelectedRemoteEntry is not { IsDirectory: false } selected || !CanQueueTransfer()) return;
        var fileName = RemotePaths.GetSafeLocalFileName(selected.Name);
        var remoteSource = selected.FullPath;
        var destination = _interactions.PickDownloadFile(fileName, _loadedLocalPath);
        if (destination is null || !IsCurrent(context)) return;
        var job = new TransferItemViewModel(context.Session.Profile, remoteSource,
            Path.GetFullPath(destination), fileName, TransferDirection.Download, context.Lifetime.Token);
        job.SetExpectedSize(Math.Max(0, selected.Size));
        EnqueueTransfer(context, job);
        await Task.CompletedTask;
    }

    private void EnqueueTransfer(RemoteSessionContext context, TransferItemViewModel job)
    {
        _transferContexts.Add(job, context);
        job.PropertyChanged += TransferChanged;
        TransferJobs.Add(job);
        if (!_batchAddingTransfers) SetStatus($"Transfer queued: {job.Direction}", "Info");
        StartTransferQueue();
    }

    private void StartTransferQueue()
    {
        if (_isProcessingTransferQueue || IsTransferQueuePaused || _suppressTransferQueueStart
            || !CanBrowseRemote() || !TransferJobs.Any(job => job.State == TransferState.Queued)) return;
        _isProcessingTransferQueue = true;
        _queueWorkerTask = ProcessTransferQueueAsync();
    }

    private async Task ProcessTransferQueueAsync()
    {
        // Publish the worker task before any synchronous adapter or event callback can finish it.
        await Task.Yield();
        try
        {
            while (!IsTransferQueuePaused && !_suppressTransferQueueStart && !_remoteRequiresReconnect
                && TransferJobs.FirstOrDefault(item => item.State == TransferState.Queued) is { } job)
            {
                if (!_transferContexts.TryGetValue(job, out var context)) { job.SetState(TransferState.Canceled); continue; }
                var token = job.Cancellation.Token;
                var progress = new ThrottledTransferProgress(_dispatcher, job, () => IsCurrent(context));
                job.SetState(TransferState.Running);
                StartTransferTiming(job);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrent(context)) throw new OperationCanceledException(token);
                    if (job.Direction == TransferDirection.Upload)
                    {
                        var sourceSize = await Task.Run(() => new FileInfo(job.SourcePath).Length, token);
                        token.ThrowIfCancellationRequested();
                        job.SetExpectedSize(sourceSize);
                    }
                    var exists = job.Direction == TransferDirection.Upload
                        ? await context.Files.ExistsAsync(job.DestinationPath, token)
                        : File.Exists(job.DestinationPath);
                    var overwrite = exists && await _interactions.ConfirmOverwriteAsync(job.DestinationPath,
                        job.Direction == TransferDirection.Upload, token);
                    token.ThrowIfCancellationRequested();
                    if (exists && !overwrite) { job.SetState(TransferState.Canceled); continue; }
                    if (job.Direction == TransferDirection.Upload)
                        await context.Files.UploadAsync(job.SourcePath, job.DestinationPath, overwrite, progress, token);
                    else
                        await context.Files.DownloadAsync(job.SourcePath, job.DestinationPath, overwrite, progress, token);

                    // Atomic finalization wins once it begins. A successful service task means the
                    // destination is committed; a late cancellation cannot truthfully undo it.
                    if (progress.Last is { } finalProgress) job.UpdateProgress(finalProgress);
                    job.SetState(TransferState.Completed);
                    if (IsCurrent(context))
                    {
                        SetStatus($"Transfer completed: {job.Direction}", "Success");
                        if (job.Direction == TransferDirection.Upload
                            && !IsRemoteLoading
                            && RemotePaths.GetParent(job.DestinationPath) == _loadedRemotePath)
                            await NavigateRemoteToAsync(_loadedRemotePath);
                        if (job.Direction == TransferDirection.Download
                            && string.Equals(Path.GetDirectoryName(job.DestinationPath), _loadedLocalPath, StringComparison.OrdinalIgnoreCase))
                            await NavigateToAsync(_loadedLocalPath);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || !IsCurrent(context))
                {
                    if (progress.Last is { } canceledProgress) job.UpdateProgress(canceledProgress);
                    job.SetState(TransferState.Canceled);
                    if (IsCurrent(context)) SetStatus("Transfer canceled.", "Info");
                }
                catch (Exception exception)
                {
                    if (token.IsCancellationRequested || !IsCurrent(context))
                    {
                        if (progress.Last is { } canceledProgress) job.UpdateProgress(canceledProgress);
                        job.SetState(TransferState.Canceled);
                    }
                    else
                    {
                        job.SetFailure(exception);
                        ReportRemoteFailure(exception);
                    }
                }
                finally
                {
                    StopTransferTiming();
                    _transferContexts.Remove(job);
                    job.Cancellation.Dispose();
                    NotifyTransferState();
                }
            }
        }
        finally
        {
            _isProcessingTransferQueue = false;
            StopTransferTiming();
            NotifyTransferState();
        }
    }

    private void ToggleTransferQueue()
    {
        IsTransferQueuePaused = !IsTransferQueuePaused;
        SetStatus(IsTransferQueuePaused
            ? "Transfer queue paused. The current file will finish before the queue stops."
            : "Transfer queue resumed.", "Info");
        if (!IsTransferQueuePaused) StartTransferQueue();
    }

    private void CancelAllTransfers()
    {
        _uploadBatchVersion++;
        _suppressTransferQueueStart = true;
        try
        {
            // Cancel waiting jobs first, so synchronous cancellation callbacks cannot start them.
            foreach (var job in TransferJobs.Where(job => job.CanCancel)
                .OrderBy(job => job.State == TransferState.Running ? 1 : 0).ToArray())
                CancelTransfer(job);
            SetStatus("Pending transfers canceled. A destination commit already in progress will finish.", "Info");
        }
        finally
        {
            _suppressTransferQueueStart = false;
            NotifyTransferState();
        }
    }

    private void ClearCompletedTransfers()
    {
        foreach (var job in TransferJobs.Where(job => job.State == TransferState.Completed).ToArray())
        {
            job.PropertyChanged -= TransferChanged;
            TransferJobs.Remove(job);
        }
        SetStatus("Completed transfers cleared from the queue.", "Info");
    }

    private async Task OpenTransferFolderAsync(TransferItemViewModel job)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(job.LocalPath));
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException();
        await _interactions.OpenLocalFolderAsync(directory);
    }

    private async Task RevealDownloadedFileAsync(TransferItemViewModel job)
    {
        var path = Path.GetFullPath(job.LocalPath);
        if (!File.Exists(path)) throw new FileNotFoundException();
        await _interactions.RevealLocalFileAsync(path);
    }

    private void RunTransferAction(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ReportTransferActionFailure(exception); }
    }

    private void ReportTransferActionFailure(Exception exception)
    {
        // User-provided paths and native Shell messages never enter error logs.
        SetStatus("The file action could not be completed. Check that the local file or folder still exists and is accessible.", "Error");
        _interactions.ShowInformation("Não foi possível abrir o arquivo",
            "Verifique se o arquivo ou a pasta ainda existe e se você tem permissão de acesso. Para copiar um caminho, tente novamente após fechar aplicativos que estejam usando a área de transferência.");
    }

    public bool CanRetryTransfer(TransferItemViewModel job)
        => !_suppressTransferQueueStart && TransferJobs.Contains(job) && job.CanRetry && CanBrowseRemote()
           && _remoteContext is { } context && SameTransferEndpoint(job.Profile, context.Session.Profile)
           && !TransferJobs.Any(pending => pending.State is TransferState.Queued or TransferState.Running
               && pending.Direction == job.Direction && SameTransferEndpoint(pending.Profile, job.Profile)
               && string.Equals(pending.SourcePath, job.SourcePath, job.Direction == TransferDirection.Upload
                   ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
               && string.Equals(pending.DestinationPath, job.DestinationPath, job.Direction == TransferDirection.Download
                   ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private void RetryTransfer(TransferItemViewModel previous)
    {
        if (!CanRetryTransfer(previous) || _remoteContext is not { } context) return;
        var job = new TransferItemViewModel(previous.Profile, previous.SourcePath,
            previous.DestinationPath, previous.FileName, previous.Direction, context.Lifetime.Token);
        if (previous.HasKnownSize) job.SetExpectedSize(previous.TotalBytes);
        EnqueueTransfer(context, job);
    }

    private static bool SameTransferEndpoint(ConnectionProfile first, ConnectionProfile second)
    {
        if (first.Port != second.Port || first.Protocol != second.Protocol
            || !string.Equals(first.Username, second.Username, StringComparison.Ordinal)
            || !string.Equals(JsonKnownHostStore.NormalizeHost(first.Host), JsonKnownHostStore.NormalizeHost(second.Host), StringComparison.Ordinal))
            return false;
        try
        {
            static string KeyPath(ConnectionProfile profile) => string.IsNullOrWhiteSpace(profile.PrivateKeyPath)
                ? string.Empty : Path.GetFullPath(profile.PrivateKeyPath);
            return string.Equals(KeyPath(first), KeyPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void StartTransferTiming(TransferItemViewModel job)
    {
        _timedTransfer = job;
        if (_transferTimingTimer is null)
        {
            _transferTimingTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _transferTimingTimer.Tick += (_, _) => _timedTransfer?.RefreshTiming();
        }
        _transferTimingTimer.Start();
    }

    private void StopTransferTiming()
    {
        _transferTimingTimer?.Stop();
        _timedTransfer = null;
    }

    private void CancelTransfer(TransferItemViewModel job)
    {
        var wasQueued = job.State == TransferState.Queued;
        job.RequestCancellation();
        if (wasQueued)
        {
            _transferContexts.Remove(job);
            job.Cancellation.Dispose();
        }
        NotifyTransferState();
    }

    private Task StopRemoteSession()
    {
        _uploadBatchVersion++;
        var context = _remoteContext;
        _remoteContext = null;
        _remoteRequestVersion++;
        StopTransferTiming();
        IsTransferQueuePaused = false;
        BatchUploadNotice = string.Empty;
        if (context is not null)
        {
            context.Lifetime.Cancel();
            context.NavigationCancellation?.Cancel();
            foreach (var pair in _transferContexts.Where(pair => ReferenceEquals(pair.Value, context)).ToArray()) CancelTransfer(pair.Key);
            _remoteRetirementTask = RetireRemoteContextAsync(context,
                Task.WhenAll(context.Operations.Append(_queueWorkerTask).Append(_remoteRetirementTask)));
        }
        _remoteEntries.ReplaceAll([]);
        _loadedRemotePath = string.Empty;
        RemotePath = string.Empty;
        _hasRemoteDirectory = false;
        SelectedRemoteEntry = null;
        IsRemoteLoading = false;
        RemoteError = string.Empty;
        _remoteRequiresReconnect = false;
        NotifyRemoteState();
        return _remoteRetirementTask;
    }

    private static async Task RetireRemoteContextAsync(RemoteSessionContext context, Task work)
    {
        try { await work; }
        catch (Exception) { /* All operation errors are surfaced by the owning operation. */ }
        finally { context.Lifetime.Dispose(); }
    }

    private bool IsCurrent(RemoteSessionContext context)
        => ReferenceEquals(_remoteContext, context) && !context.Lifetime.IsCancellationRequested && !_isShuttingDown
           && ReferenceEquals(_activeSession, context.Session) && context.Session.IsConnected;
    private bool CanBrowseRemote() => _remoteContext is { } context && IsCurrent(context) && !_remoteRequiresReconnect && !IsDisconnecting;
    private bool CanQueueTransfer() => CanBrowseRemote() && _hasRemoteDirectory && !IsRemoteLoading;
    private void RefreshRemoteCommandStates()
    {
        RefreshRemoteCommand?.NotifyCanExecuteChanged();
        GoUpRemoteCommand?.NotifyCanExecuteChanged();
        NavigateRemoteCommand?.NotifyCanExecuteChanged();
        OpenRemoteEntryCommand?.NotifyCanExecuteChanged();
        UploadCommand?.NotifyCanExecuteChanged();
        UploadFilesCommand?.NotifyCanExecuteChanged();
        DownloadCommand?.NotifyCanExecuteChanged();
        RefreshTransferCommandStates();
    }
    private void RefreshTransferCommandStates()
    {
        CancelTransferCommand?.NotifyCanExecuteChanged();
        ToggleTransferQueueCommand?.NotifyCanExecuteChanged();
        CancelAllTransfersCommand?.NotifyCanExecuteChanged();
        ClearCompletedTransfersCommand?.NotifyCanExecuteChanged();
        RetryTransferCommand?.NotifyCanExecuteChanged();
        ShowTransferDetailsCommand?.NotifyCanExecuteChanged();
        OpenTransferFolderCommand?.NotifyCanExecuteChanged();
        RevealDownloadedFileCommand?.NotifyCanExecuteChanged();
        CopyRemotePathCommand?.NotifyCanExecuteChanged();
    }
    private void NotifyRemoteState()
    {
        OnPropertyChanged(nameof(HasRemoteEntries));
        OnPropertyChanged(nameof(HasRemoteError));
        OnPropertyChanged(nameof(IsRemoteEmpty));
        OnPropertyChanged(nameof(HasRemoteFileService));
        OnPropertyChanged(nameof(RemoteSummary));
        OnPropertyChanged(nameof(RemoteStateMessage));
        RefreshRemoteCommandStates();
    }
    private void TransferChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TransferItemViewModel.BytesTransferred) or nameof(TransferItemViewModel.TotalBytes)
            or nameof(TransferItemViewModel.HasKnownSize))
        {
            NotifyPendingSummary();
            return;
        }
        if (args.PropertyName is not (nameof(TransferItemViewModel.State) or nameof(TransferItemViewModel.CanCancel)
            or nameof(TransferItemViewModel.CanRetry) or nameof(TransferItemViewModel.HasFailureDetails))) return;
        if (args.PropertyName == nameof(TransferItemViewModel.State)
            && sender is TransferItemViewModel { State: not TransferState.Running } job
            && ReferenceEquals(job, _timedTransfer)) StopTransferTiming();
        NotifyTransferState(refreshView: args.PropertyName == nameof(TransferItemViewModel.State));
    }
    private void NotifyTransferState(bool refreshView = false)
    {
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(TransferCount));
        OnPropertyChanged(nameof(TransferSummary));
        NotifyPendingSummary();
        if (refreshView) RefreshTransferView();
        RefreshTransferCommandStates();
    }

    private void NotifyPendingSummary()
    {
        OnPropertyChanged(nameof(PendingTransferCount));
        OnPropertyChanged(nameof(PendingBytes));
        OnPropertyChanged(nameof(HasUnknownPendingSize));
        OnPropertyChanged(nameof(PendingBytesLabel));
        OnPropertyChanged(nameof(PendingSummary));
    }

    private static bool IsPendingTransfer(TransferItemViewModel job)
        => job.State is TransferState.Queued or TransferState.Running;

    private bool MatchesTransferFilter(object value) => value is TransferItemViewModel job && (SelectedTransferFilter switch
    {
        TransferFilter.Active => IsPendingTransfer(job),
        TransferFilter.Completed => job.State == TransferState.Completed,
        TransferFilter.Failed => job.State == TransferState.Failed,
        TransferFilter.Canceled => job.State == TransferState.Canceled,
        _ => true
    });

    private void RefreshTransferView()
    {
        if (_refreshingTransferView || FilteredTransferJobs is null) return;
        _refreshingTransferView = true;
        try { FilteredTransferJobs.Refresh(); }
        finally { _refreshingTransferView = false; }
        OnPropertyChanged(nameof(HasVisibleTransfers));
        OnPropertyChanged(nameof(TransferEmptyMessage));
    }

    private static string FormatPendingBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }
    private void ReportRemoteFailure(Exception exception)
    {
        if (exception is RemoteFileException { RequiresReconnect: true }) _remoteRequiresReconnect = true;
        RemoteError = GetRemoteError(exception);
        SetStatus(RemoteError, "Error");
        NotifyRemoteState();
    }
    private static string GetRemoteError(Exception exception) => exception switch
    {
        RemoteFileException { RequiresReconnect: true } => "SFTP cannot continue on this connection. Reconnect the VPS after checking the connection and server identity.",
        RemoteFileException { Code: RemoteFileErrorCode.PermissionDenied } => "Permission was denied. Check the account and file permissions.",
        RemoteFileException { Code: RemoteFileErrorCode.NotFound } => "The requested file or directory was not found. Refresh the listing and check the path.",
        RemoteFileException { Code: RemoteFileErrorCode.AlreadyExists } => "The destination already exists. Confirm replacement before retrying.",
        RemoteFileException { Code: RemoteFileErrorCode.UnsafePath } => "The selected path or filename is not safe for this operation.",
        RemoteFileException { Code: RemoteFileErrorCode.UnsupportedReplacement } => "The server cannot replace the destination atomically. Choose another filename.",
        RemoteFileException { Code: RemoteFileErrorCode.Unavailable } => "SFTP is unavailable for this connection. Check that the server enables SFTP.",
        RemoteFileException { Code: RemoteFileErrorCode.ConnectionLost } => "The SFTP connection was lost. Reconnect the VPS before continuing.",
        UnauthorizedAccessException => "Access to the selected local file or folder was denied.",
        FileNotFoundException or DirectoryNotFoundException => "The selected local file or destination folder was not found.",
        ArgumentException => "Check the selected file and remote path, then try again.",
        _ => "The SFTP operation failed. Check the connection and file permissions, then try again."
    };
    private static void ObserveLateFailure(Task task)
        => _ = task.ContinueWith(failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private sealed class RemoteSessionContext(IRemoteConnectionSession session, IRemoteFileService files)
    {
        public IRemoteConnectionSession Session { get; } = session;
        public IRemoteFileService Files { get; } = files;
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource? NavigationCancellation { get; set; }
        public List<Task> Operations { get; } = [];
    }

    private sealed record UploadCandidate(string Path, string Name, long? Size);
    private sealed record UploadBatch(IReadOnlyList<UploadCandidate> Files, int DirectoryCount, int InvalidCount, int DuplicateCount);

    private sealed class ThrottledTransferProgress(Dispatcher dispatcher, TransferItemViewModel job, Func<bool> isCurrent)
        : IProgress<TransferProgress>
    {
        private TransferProgress? _last;
        private long _lastPublished;
        public TransferProgress? Last => Volatile.Read(ref _last);
        public void Report(TransferProgress value)
        {
            Volatile.Write(ref _last, value);
            var tick = Environment.TickCount64;
            if ((!value.IsFinalizing && tick - Interlocked.Read(ref _lastPublished) < 100) || dispatcher.HasShutdownStarted) return;
            Interlocked.Exchange(ref _lastPublished, tick);
            dispatcher.BeginInvoke(value.IsFinalizing ? DispatcherPriority.Normal : DispatcherPriority.Background, new Action(() =>
            {
                if (job.State == TransferState.Running && isCurrent() && !job.Cancellation.IsCancellationRequested)
                    job.UpdateProgress(value);
            }));
        }
    }
}
