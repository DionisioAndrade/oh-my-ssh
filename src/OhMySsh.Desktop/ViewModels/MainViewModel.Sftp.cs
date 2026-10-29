using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Infrastructure.Persistence;
using RemotePaths = OhMySsh.Core.Models.RemotePath;

namespace OhMySsh.Desktop.ViewModels;

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

    public ObservableCollection<FileEntry> RemoteEntries => _remoteEntries;
    public ObservableCollection<TransferItemViewModel> TransferJobs { get; } = [];
    public AsyncRelayCommand RefreshRemoteCommand { get; private set; } = null!;
    public AsyncRelayCommand GoUpRemoteCommand { get; private set; } = null!;
    public AsyncRelayCommand NavigateRemoteCommand { get; private set; } = null!;
    public AsyncRelayCommand OpenRemoteEntryCommand { get; private set; } = null!;
    public AsyncRelayCommand UploadCommand { get; private set; } = null!;
    public AsyncRelayCommand DownloadCommand { get; private set; } = null!;
    public RelayCommand CancelTransferCommand { get; private set; } = null!;
    public RelayCommand ToggleTransferQueueCommand { get; private set; } = null!;
    public RelayCommand CancelAllTransfersCommand { get; private set; } = null!;
    public RelayCommand ClearCompletedTransfersCommand { get; private set; } = null!;
    public RelayCommand RetryTransferCommand { get; private set; } = null!;

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
        RefreshRemoteCommand = new AsyncRelayCommand(RefreshRemoteAsync, CanBrowseRemote, ReportRemoteFailure);
        GoUpRemoteCommand = new AsyncRelayCommand(() => NavigateRemoteToAsync(RemotePaths.GetParent(_loadedRemotePath)),
            () => CanBrowseRemote() && _hasRemoteDirectory && _loadedRemotePath != "/", ReportRemoteFailure);
        NavigateRemoteCommand = new AsyncRelayCommand(() => NavigateRemoteToAsync(RemotePath), CanBrowseRemote, ReportRemoteFailure);
        OpenRemoteEntryCommand = new AsyncRelayCommand(parameter => NavigateRemoteToAsync(((FileEntry)parameter!).FullPath),
            parameter => CanBrowseRemote() && parameter is FileEntry { IsDirectory: true }, ReportRemoteFailure);
        UploadCommand = new AsyncRelayCommand(QueueUploadAsync, CanQueueTransfer, ReportRemoteFailure);
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
        RemoteEntries.CollectionChanged += (_, _) => NotifyRemoteState();
        TransferJobs.CollectionChanged += (_, _) => NotifyTransferState();
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
        var localPath = SelectedLocalEntry is { IsDirectory: false } selected
            ? selected.FullPath : _interactions.PickUploadFile(_loadedLocalPath);
        if (localPath is null || !IsCurrent(context)) return;
        var fileName = Path.GetFileName(localPath);
        var remoteDestination = RemotePaths.Combine(remoteDirectory, fileName);
        EnqueueTransfer(context, new TransferItemViewModel(context.Session.Profile, Path.GetFullPath(localPath),
            remoteDestination, fileName, TransferDirection.Upload, context.Lifetime.Token));
        await Task.CompletedTask;
    }

    private async Task QueueDownloadAsync()
    {
        if (_remoteContext is not { } context || SelectedRemoteEntry is not { IsDirectory: false } selected || !CanQueueTransfer()) return;
        var fileName = RemotePaths.GetSafeLocalFileName(selected.Name);
        var remoteSource = selected.FullPath;
        var destination = _interactions.PickDownloadFile(fileName, _loadedLocalPath);
        if (destination is null || !IsCurrent(context)) return;
        EnqueueTransfer(context, new TransferItemViewModel(context.Session.Profile, remoteSource,
            Path.GetFullPath(destination), fileName, TransferDirection.Download, context.Lifetime.Token));
        await Task.CompletedTask;
    }

    private void EnqueueTransfer(RemoteSessionContext context, TransferItemViewModel job)
    {
        _transferContexts.Add(job, context);
        job.PropertyChanged += TransferChanged;
        TransferJobs.Add(job);
        SetStatus($"Transfer queued: {job.Direction}", "Info");
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
                    if (job.Direction == TransferDirection.Upload && !File.Exists(job.SourcePath))
                        throw new FileNotFoundException("The selected local upload file was not found.");
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
                        var message = GetRemoteError(exception);
                        ReportRemoteFailure(exception);
                        job.SetState(TransferState.Failed, message);
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
        EnqueueTransfer(context, new TransferItemViewModel(previous.Profile, previous.SourcePath,
            previous.DestinationPath, previous.FileName, previous.Direction, context.Lifetime.Token));
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
        var context = _remoteContext;
        _remoteContext = null;
        _remoteRequestVersion++;
        StopTransferTiming();
        IsTransferQueuePaused = false;
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
        if (args.PropertyName is not (nameof(TransferItemViewModel.State) or nameof(TransferItemViewModel.CanCancel)
            or nameof(TransferItemViewModel.CanRetry))) return;
        if (args.PropertyName == nameof(TransferItemViewModel.State)
            && sender is TransferItemViewModel { State: not TransferState.Running } job
            && ReferenceEquals(job, _timedTransfer)) StopTransferTiming();
        NotifyTransferState();
    }
    private void NotifyTransferState()
    {
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(TransferCount));
        OnPropertyChanged(nameof(TransferSummary));
        RefreshTransferCommandStates();
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
        RemoteFileException safe => safe.Message,
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
