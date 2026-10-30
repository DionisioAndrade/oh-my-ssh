using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Core.Localization;
using OhMySsh.Infrastructure.Connections;

namespace OhMySsh.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConnectionProfileStore _store;
    private readonly ILocalFileService _localFiles;
    private readonly IDesktopInteractionService _interactions;
    private readonly IRemoteConnectionService _remoteConnections;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Action<string, string>? _log;
    private readonly ReplaceableObservableCollection<FileEntry> _localEntries = [];
    private string _searchText = string.Empty;
    private ConnectionProfile? _selectedConnection;
    private FileEntry? _selectedLocalEntry;
    private string _localPath;
    private string _loadedLocalPath;
    private UiMessage _statusMessage = new("Vm.Status.Ready");
    private string _statusKind = "Info";
    private bool _isBusy;
    private bool _favoritesOnly;
    private bool _initialized;
    private bool _profilesLoadedSuccessfully;
    private string? _profileLoadFailure;
    private IRemoteConnectionSession? _activeSession;
    private ConnectionProfile? _activeConnection;
    private CancellationTokenSource? _connectionCancellation;
    private bool _isConnecting;
    private bool _isDisconnecting;
    private UiMessage? _connectionError;
    private string _manualName = string.Empty;
    private string _manualHost = string.Empty;
    private string _manualPort = "22";
    private string _manualUsername = string.Empty;
    private string _manualPrivateKeyPath = string.Empty;
    private string _manualConnectionTimeoutSeconds = "30";
    private ConnectionProtocol _manualProtocol = ConnectionProtocol.Ssh;
    private bool _isShuttingDown;
    private Task _connectionSettled = Task.CompletedTask;
    private Task _sessionCleanupTask = Task.CompletedTask;
    private readonly List<Task> _abandonedSessionCleanupTasks = [];
    private Task? _shutdownTask;

    public MainViewModel(
        IConnectionProfileStore store,
        ILocalFileService localFiles,
        IDesktopInteractionService interactions,
        IRemoteConnectionService? remoteConnections = null,
        Action<string, string>? log = null)
    {
        _store = store;
        _localFiles = localFiles;
        _interactions = interactions;
        _remoteConnections = remoteConnections ?? new UnavailableRemoteConnectionService();
        _log = log;
        _localPath = GetInitialLocalPath();
        _loadedLocalPath = _localPath;

        FilteredConnections = CollectionViewSource.GetDefaultView(Connections);
        FilteredConnections.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConnectionProfile.Group)));
        FilteredConnections.Filter = MatchesConnection;
        FilteredConnections.CollectionChanged += FilteredConnectionsChanged;
        Connections.CollectionChanged += ConnectionsChanged;
        LocalEntries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(LocalSummary));
            OnPropertyChanged(nameof(HasLocalEntries));
        };

        AddConnectionCommand = new AsyncRelayCommand(AddConnectionAsync, CanEditProfiles, ReportCommandFailure);
        EditConnectionCommand = new AsyncRelayCommand(EditConnectionAsync, CanEditSelectedProfile, ReportCommandFailure);
        DeleteConnectionCommand = new AsyncRelayCommand(DeleteConnectionAsync, CanEditSelectedProfile, ReportCommandFailure);
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync, CanEditSelectedProfile, ReportCommandFailure);
        SelectConnectionCommand = new RelayCommand(SelectConnection, CanSelectConnection);
        CopySshCommand = new RelayCommand(CopySsh, CanCopySsh);
        ConnectCommand = new AsyncRelayCommand(ConnectProfileAsync, CanConnectProfile, ReportConnectionFailure);
        ManualConnectCommand = new AsyncRelayCommand(ConnectManualAsync, CanStartConnection, ReportConnectionFailure);
        SaveManualConnectionCommand = new AsyncRelayCommand(SaveManualConnectionAsync, CanEditProfiles, ReportCommandFailure);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnect, ReportConnectionFailure);
        CancelConnectionCommand = new RelayCommand(CancelConnection, CanCancelConnection);
        RefreshLocalCommand = new AsyncRelayCommand(() => NavigateToAsync(_loadedLocalPath), () => !IsBusy, ReportCommandFailure);
        GoUpCommand = new AsyncRelayCommand(GoUpAsync, CanGoUp, ReportCommandFailure);
        BrowseLocalCommand = new AsyncRelayCommand(BrowseLocalAsync, () => !IsBusy, ReportCommandFailure);
        NavigateLocalCommand = new AsyncRelayCommand(() => NavigateToAsync(LocalPath), () => !IsBusy, ReportCommandFailure);
        OpenLocalEntryCommand = new AsyncRelayCommand(
            parameter => NavigateToAsync(((FileEntry)parameter!).FullPath),
            parameter => !IsBusy && parameter is FileEntry { IsDirectory: true },
            ReportCommandFailure);
        CopyEndpointCommand = new RelayCommand(CopyEndpoint, CanUseSelectedConnection);
        FilterFavoritesCommand = new RelayCommand(() => FavoritesOnly = !FavoritesOnly);
        InitializeRemoteCommands();
    }

    public ObservableCollection<ConnectionProfile> Connections { get; } = [];
    public ICollectionView FilteredConnections { get; }
    public ObservableCollection<FileEntry> LocalEntries => _localEntries;

    /// <summary>Raised only after a new or edited profile was persisted successfully.</summary>
    public event EventHandler? ProfileSaved;

    /// <summary>Raised when an adapter-confirmed active session is installed or removed.</summary>
    public event EventHandler? ActiveSessionChanged;

    public IReadOnlyList<ConnectionProtocol> ProtocolOptions { get; } = Enum.GetValues<ConnectionProtocol>();

    public string ManualName
    {
        get => _manualName;
        set => SetManualField(ref _manualName, value);
    }

    public string ManualHost
    {
        get => _manualHost;
        set => SetManualField(ref _manualHost, value);
    }

    public string ManualPort
    {
        get => _manualPort;
        set => SetManualField(ref _manualPort, value);
    }

    public string ManualUsername
    {
        get => _manualUsername;
        set => SetManualField(ref _manualUsername, value);
    }

    public string ManualPrivateKeyPath
    {
        get => _manualPrivateKeyPath;
        set => SetManualField(ref _manualPrivateKeyPath, value);
    }

    public string ManualConnectionTimeoutSeconds
    {
        get => _manualConnectionTimeoutSeconds;
        set => SetManualField(ref _manualConnectionTimeoutSeconds, value);
    }

    public ConnectionProtocol ManualProtocol
    {
        get => _manualProtocol;
        set
        {
            var previousDefaultPort = _manualProtocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp ? "22" : "21";
            if (SetProperty(ref _manualProtocol, value))
            {
                if (ManualPort == previousDefaultPort)
                {
                    ManualPort = value is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp ? "22" : "21";
                }

                SetConnectionError(null);
            }
        }
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public bool IsDisconnecting
    {
        get => _isDisconnecting;
        private set
        {
            if (SetProperty(ref _isDisconnecting, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public string ConnectionError => _connectionError?.Text ?? string.Empty;

    private void SetConnectionError(UiMessage? message)
    {
        _connectionError = message;
        OnPropertyChanged(nameof(ConnectionError));
        OnPropertyChanged(nameof(HasConnectionError));
    }

    public bool HasConnectionError => ConnectionError.Length > 0;
    public bool HasActiveSession => _activeSession?.IsConnected == true;
    public ConnectionProfile? ActiveConnection => HasActiveSession ? _activeConnection : null;
    public string ActiveConnectionEndpoint => ActiveConnection is { } profile ? GetEndpoint(profile) : UiText.Get("Vm.NoActiveConnection");

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                FilteredConnections.Refresh();
            }
        }
    }

    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (SetProperty(ref _favoritesOnly, value))
            {
                FilteredConnections.Refresh();
            }
        }
    }

    public ConnectionProfile? SelectedConnection
    {
        get => _selectedConnection;
        set
        {
            if (!SetProperty(ref _selectedConnection, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedConnection));
            OnPropertyChanged(nameof(SelectedConnectionTitle));
            OnPropertyChanged(nameof(SelectedConnectionEndpoint));
            OnPropertyChanged(nameof(SelectedProtocolLabel));
            OnPropertyChanged(nameof(SessionSummary));
            OnPropertyChanged(nameof(TerminalMessage));
            OnPropertyChanged(nameof(RemoteHint));
            RefreshCommandStates();
        }
    }

    public FileEntry? SelectedLocalEntry
    {
        get => _selectedLocalEntry;
        set
        {
            if (SetProperty(ref _selectedLocalEntry, value))
            {
                UploadCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string LocalPath
    {
        get => _localPath;
        set => SetProperty(ref _localPath, value ?? string.Empty);
    }

    public string LocalSummary
    {
        get
        {
            var directories = LocalEntries.Count(entry => entry.IsDirectory);
            var files = LocalEntries.Count - directories;
            return UiText.Format("Vm.FileSummary", LocalEntries.Count, directories, files);
        }
    }

    public string StatusMessage
    {
        get => _profileLoadFailure is null ? _statusMessage.Text : UiText.Get(_profileLoadFailure);
    }

    public string StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public int ConnectionCount => Connections.Count;
    public int FavoriteCount => Connections.Count(connection => connection.IsFavorite);
    public bool ProfilesAvailable
    {
        get => _profilesLoadedSuccessfully;
        private set
        {
            if (SetProperty(ref _profilesLoadedSuccessfully, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public bool HasConnections => Connections.Count > 0;
    public bool HasFilteredConnections => !FilteredConnections.IsEmpty;
    public int FilteredConnectionCount => FilteredConnections.Cast<ConnectionProfile>().Count();
    public bool HasSelectedConnection => SelectedConnection is not null;
    public bool HasLocalEntries => LocalEntries.Count > 0;
    public string SelectedConnectionTitle => (ActiveConnection ?? SelectedConnection)?.Name ?? UiText.Get("Vm.Workspace");
    public string SelectedConnectionEndpoint => (ActiveConnection ?? SelectedConnection) is { } profile ? GetEndpoint(profile) : UiText.Get("Vm.NoSelectedConnection");
    public string SelectedProtocolLabel => (ActiveConnection ?? SelectedConnection) is { } profile ? GetProtocolLabel(profile.Protocol) : "SSH / SFTP";
    public string SessionSummary => (ActiveConnection ?? SelectedConnection) is { } profile
        ? UiText.Format("Vm.SessionSummary", GetProtocolLabel(profile.Protocol), GetEndpoint(profile), UiText.Get(HasActiveSession ? "Vm.Connected" : "Vm.Disconnected"))
        : UiText.Get("Vm.SessionWelcome");
    public string TerminalMessage => ActiveConnection is { } active
        ? UiText.Format("Vm.TerminalConnected", active.Name, GetEndpoint(active))
        : SelectedConnection is { } profile
            ? UiText.Format("Vm.TerminalSelected", profile.Name, GetEndpoint(profile))
            : UiText.Get("Vm.TerminalWelcome");
    public string RemoteHint => (ActiveConnection ?? SelectedConnection) is { } profile
        ? UiText.Format("Vm.RemoteHint", profile.Host, GetProtocolLabel(profile.Protocol))
        : UiText.Get("Vm.RemoteHintWelcome");

    public AsyncRelayCommand AddConnectionCommand { get; }
    public AsyncRelayCommand EditConnectionCommand { get; }
    public AsyncRelayCommand DeleteConnectionCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand SelectConnectionCommand { get; }
    public RelayCommand CopySshCommand { get; }
    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand ManualConnectCommand { get; }
    public AsyncRelayCommand SaveManualConnectionCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public RelayCommand CancelConnectionCommand { get; }
    public AsyncRelayCommand RefreshLocalCommand { get; }
    public AsyncRelayCommand GoUpCommand { get; }
    public AsyncRelayCommand BrowseLocalCommand { get; }
    public AsyncRelayCommand NavigateLocalCommand { get; }
    public AsyncRelayCommand OpenLocalEntryCommand { get; }
    public RelayCommand CopyEndpointCommand { get; }
    public RelayCommand FilterFavoritesCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        IsBusy = true;
        try
        {
            var profiles = await _store.LoadAsync();
            ReplaceConnections(profiles, profiles.FirstOrDefault()?.Id);
            ProfilesAvailable = true;
        }
        catch (Exception exception)
        {
            _profileLoadFailure = "Vm.Status.ProfileLoadFailure";
            ReportFailure("Vm.Failure.LoadConnections", exception);
        }
        finally
        {
            IsBusy = false;
        }

        await NavigateToAsync(_loadedLocalPath);
        if (StatusKind != "Error" && !HasConnectionError && !HasActiveSession)
        {
            SetStatus("Vm.Status.SelectConnection", "Info");
        }
    }

    /// <summary>Changes the listing only after the requested folder was read successfully.</summary>
    public async Task NavigateToAsync(string path)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Enter a folder path.");
            }

            var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
            var fullPath = Path.GetFullPath(expandedPath, _loadedLocalPath);
            var entries = await _localFiles.GetEntriesAsync(fullPath);

            _localEntries.ReplaceAll(entries);

            _loadedLocalPath = fullPath;
            LocalPath = fullPath;
            SelectedLocalEntry = null;
            SetStatus("Vm.Status.LocalRefreshed", "Success", LocalEntries.Count);
        }
        catch (Exception exception)
        {
            LocalPath = _loadedLocalPath;
            ReportFailure("Vm.Failure.OpenFolder", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddConnectionAsync()
    {
        var profile = _interactions.EditConnection(null);
        if (profile is null)
        {
            return;
        }

        var updated = Connections.Append(profile).ToArray();
        if (await SaveConnectionsAsync(updated, profile.Id, new UiMessage("Vm.Status.ProfileSaved", profile.Name), clearFilters: true))
        {
            ProfileSaved?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EditConnectionAsync(object? parameter)
    {
        if (ResolveProfile(parameter) is not { } existing)
        {
            return;
        }

        var edited = _interactions.EditConnection(existing);
        if (edited is null)
        {
            return;
        }

        edited = edited with { Id = existing.Id };
        var updated = Connections.Select(profile => profile.Id == existing.Id ? edited : profile).ToArray();
        if (await SaveConnectionsAsync(updated, edited.Id, new UiMessage("Vm.Status.ProfileUpdated", edited.Name), clearFilters: true))
        {
            ProfileSaved?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task DeleteConnectionAsync(object? parameter)
    {
        if (ResolveProfile(parameter) is not { } profile || !_interactions.ConfirmDelete(profile))
        {
            return;
        }

        var updated = Connections.Where(connection => connection.Id != profile.Id).ToArray();
        var selectedId = SelectedConnection?.Id == profile.Id ? updated.FirstOrDefault()?.Id : SelectedConnection?.Id;
        await SaveConnectionsAsync(updated, selectedId, new UiMessage("Vm.Status.ProfileRemoved", profile.Name));
    }

    private async Task ToggleFavoriteAsync(object? parameter)
    {
        if (ResolveProfile(parameter) is not { } profile)
        {
            return;
        }

        var selectedId = SelectedConnection?.Id ?? profile.Id;
        var updated = Connections.Select(connection => connection.Id == profile.Id
            ? connection with { IsFavorite = !connection.IsFavorite }
            : connection).ToArray();
        var message = profile.IsFavorite
            ? new UiMessage("Vm.Status.FavoriteRemoved", profile.Name)
            : new UiMessage("Vm.Status.FavoriteAdded", profile.Name);
        await SaveConnectionsAsync(updated, selectedId, message);
    }

    private async Task<bool> SaveConnectionsAsync(ConnectionProfile[] profiles, Guid? selectedId, UiMessage successMessage, bool clearFilters = false)
    {
        if (!ProfilesAvailable)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await _store.SaveAsync(profiles);
            if (clearFilters)
            {
                SearchText = string.Empty;
                FavoritesOnly = false;
            }

            ReplaceConnections(profiles, selectedId);
            SetStatus(successMessage, "Success");
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReplaceConnections(System.Collections.Generic.IEnumerable<ConnectionProfile> profiles, Guid? selectedId)
    {
        Connections.Clear();
        foreach (var profile in profiles.OrderBy(profile => profile.Group, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Connections.Add(profile);
        }

        SelectedConnection = Connections.FirstOrDefault(profile => profile.Id == selectedId);
    }

    private Task GoUpAsync()
    {
        var parent = Directory.GetParent(_loadedLocalPath);
        return parent is not null ? NavigateToAsync(parent.FullName) : Task.CompletedTask;
    }

    private async Task BrowseLocalAsync()
    {
        var folder = _interactions.PickLocalFolder(_loadedLocalPath);
        if (folder is not null)
        {
            await NavigateToAsync(folder);
        }
    }

    private async Task ConnectProfileAsync(object? parameter)
    {
        if (ResolveProfile(parameter) is { } profile)
        {
            await ConnectToProfileAsync(profile);
        }
    }

    private async Task ConnectManualAsync()
    {
        if (TryCreateManualProfile("Conexão rápida", out var profile) && profile is not null)
        {
            await ConnectToProfileAsync(profile);
        }
    }

    private async Task SaveManualConnectionAsync()
    {
        if (!TryCreateManualProfile("VPS", out var profile) || profile is null)
        {
            return;
        }

        var updated = Connections.Append(profile).ToArray();
        if (await SaveConnectionsAsync(updated, profile.Id, new UiMessage("Vm.Status.ProfileSaved", profile.Name), clearFilters: true))
        {
            ProfileSaved?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryCreateManualProfile(string group, out ConnectionProfile? profile)
    {
        var valid = ConnectionDraftValidator.TryCreateWithErrorKey(
            ManualName, ManualHost, ManualPort, ManualUsername, ManualPrivateKeyPath, ManualProtocol, group,
            out profile, out var error, ManualConnectionTimeoutSeconds);
        SetConnectionError(valid ? null : new UiMessage(error));
        if (!valid)
        {
            SetStatus(error, "Error");
        }

        return valid;
    }

    private async Task ConnectToProfileAsync(ConnectionProfile profile)
    {
        if (!CanStartConnection())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connectionSettled = settled.Task;
        _connectionCancellation = cancellation;
        SetConnectionError(null);
        IsConnecting = true;
        SetStatus("Vm.Status.Connecting", "Info", profile.Host);
        IRemoteConnectionSession? candidate = null;
        Task<IRemoteConnectionSession>? connectionTask = null;
        var activated = false;
        try
        {
            // An adapter may report a connection loss between UI actions. Retire
            // that disconnected handle before requesting a replacement.
            if (_activeSession is { } previous)
            {
                var remoteCleanup = StopRemoteSession();
                previous.Disconnected -= OnSessionDisconnected;
                _activeSession = null;
                _activeConnection = null;
                NotifyActiveSessionChanged();
                _sessionCleanupTask = DisposeQuietlyAsync(previous);
                await _sessionCleanupTask.WaitAsync(cancellation.Token);
                await remoteCleanup.WaitAsync(cancellation.Token);
            }

            connectionTask = _remoteConnections.ConnectAsync(profile with { }, cancellation.Token);
            candidate = await connectionTask.WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (candidate is null || !candidate.IsConnected)
            {
                throw new InvalidOperationException("The server did not confirm the connection. Try again.");
            }

            var connectedProfile = candidate.Profile
                ?? throw new InvalidOperationException("The session returned an invalid profile.");
            candidate.Disconnected += OnSessionDisconnected;
            if (!candidate.IsConnected)
            {
                candidate.Disconnected -= OnSessionDisconnected;
                throw new InvalidOperationException("The connection closed before the session became available.");
            }
            _activeConnection = connectedProfile with { };
            _activeSession = candidate;
            candidate = null;
            activated = true;
            NotifySessionProperties();
            SetStatus("Vm.Status.Connected", "Success", _activeConnection.Host);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (candidate is null && connectionTask is not null)
            {
                // WaitAsync releases the UI even if an adapter ignores its
                // token. A late result must never activate a canceled session.
                _abandonedSessionCleanupTasks.RemoveAll(task => task.IsCompleted);
                _abandonedSessionCleanupTasks.Add(DisposeAbandonedSessionAsync(connectionTask));
            }

            SetConnectionError(new UiMessage("Vm.Status.Canceled"));
            SetStatus(_connectionError!, "Info");
        }
        catch (Exception exception)
        {
            ReportConnectionFailure(exception);
        }
        finally
        {
            if (candidate is not null)
            {
                await DisposeQuietlyAsync(candidate);
            }

            if (ReferenceEquals(_connectionCancellation, cancellation))
            {
                _connectionCancellation = null;
            }

            IsConnecting = false;
            settled.TrySetResult();
        }

        if (activated && HasActiveSession && !_isShuttingDown)
        {
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
            StartRemoteSession(_activeSession!);
        }
    }

    private async Task DisconnectAsync()
    {
        CancelConnection();
        if (_activeSession is not { } session)
        {
            return;
        }

        IsDisconnecting = true;
        var remoteCleanup = StopRemoteSession();
        session.Disconnected -= OnSessionDisconnected;
        _activeSession = null;
        _activeConnection = null;
        SetConnectionError(null);
        NotifyActiveSessionChanged();
        try
        {
            await (_sessionCleanupTask = session.DisposeAsync().AsTask());
            await remoteCleanup;
            SetStatus("Vm.Status.Disconnected", "Info");
        }
        catch (Exception exception)
        {
            ReportConnectionFailure(exception);
        }
        finally
        {
            IsDisconnecting = false;
        }
    }

    /// <summary>Cancels pending prompts/transport attempts and disposes the active session before exit.</summary>
    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _isShuttingDown = true;
        CancelConnection();
        var remoteCleanup = StopRemoteSession();
        RefreshCommandStates();
        await _connectionSettled;
        // A canceled UI await can finish before the adapter disposes its socket.
        // Keep the dispatcher alive until those transport attempts have settled.
        await Task.WhenAll(_abandonedSessionCleanupTasks);
        _abandonedSessionCleanupTasks.Clear();
        try
        {
            await _sessionCleanupTask;
        }
        catch (Exception)
        {
            // A prior disconnect failure must not prevent application shutdown.
        }
        if (_activeSession is { } session)
        {
            session.Disconnected -= OnSessionDisconnected;
            _activeSession = null;
            _activeConnection = null;
            NotifyActiveSessionChanged();
            await (_sessionCleanupTask = DisposeQuietlyAsync(session));
        }
        await remoteCleanup;

        // A canceled SSH.NET handshake can outlive the visible attempt. Keep
        // the dispatcher and process alive until the adapter retires its workers.
        var transportShutdown = _remoteConnections.ShutdownAsync();
        if (!transportShutdown.IsCompleted)
        {
            SetStatus("Vm.Status.Closing", "Info");
        }
        await transportShutdown;
    }

    private void OnSessionDisconnected(object? sender, EventArgs e)
    {
        if (sender is IRemoteConnectionSession session && !_dispatcher.HasShutdownStarted)
        {
            _dispatcher.BeginInvoke(new Action(() => _ = HandleSessionDisconnectedAsync(session)));
        }
    }

    private async Task HandleSessionDisconnectedAsync(IRemoteConnectionSession session)
    {
        if (_isShuttingDown || !ReferenceEquals(_activeSession, session))
        {
            return;
        }

        IsDisconnecting = true;
        var remoteCleanup = StopRemoteSession();
        session.Disconnected -= OnSessionDisconnected;
        _activeSession = null;
        _activeConnection = null;
        SetConnectionError(new UiMessage("Vm.Status.ConnectionLost"));
        SetStatus(_connectionError!, "Error");
        NotifyActiveSessionChanged();
        try
        {
            await (_sessionCleanupTask = DisposeQuietlyAsync(session));
            await remoteCleanup;
        }
        finally
        {
            IsDisconnecting = false;
        }
    }

    private void CancelConnection()
    {
        if (_connectionCancellation is { IsCancellationRequested: false } cancellation)
        {
            cancellation.Cancel();
            CancelConnectionCommand.NotifyCanExecuteChanged();
        }
    }

    private void NotifyActiveSessionChanged()
    {
        NotifySessionProperties();
        RefreshCommandStates();
        ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifySessionProperties()
    {
        OnPropertyChanged(nameof(HasActiveSession));
        OnPropertyChanged(nameof(ActiveConnection));
        OnPropertyChanged(nameof(ActiveConnectionEndpoint));
        OnPropertyChanged(nameof(SelectedConnectionTitle));
        OnPropertyChanged(nameof(SelectedConnectionEndpoint));
        OnPropertyChanged(nameof(SelectedProtocolLabel));
        OnPropertyChanged(nameof(SessionSummary));
        OnPropertyChanged(nameof(TerminalMessage));
        OnPropertyChanged(nameof(RemoteHint));
    }

    private static async Task DisposeQuietlyAsync(IRemoteConnectionSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception)
        {
            // Cleanup must not mask the original failed/canceled connection.
        }
    }

    private static async Task DisposeAbandonedSessionAsync(Task<IRemoteConnectionSession> connectionTask)
    {
        try
        {
            var session = await connectionTask.ConfigureAwait(false);
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Observe both late adapter errors and best-effort disposal errors.
        }
    }

    private void SetManualField(ref string field, string? value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value ?? string.Empty, propertyName))
        {
            SetConnectionError(null);
        }
    }

    private void CopyEndpoint()
    {
        try
        {
            _interactions.CopyToClipboard(SelectedConnectionEndpoint);
            SetStatus("Vm.Status.EndpointCopied", "Success");
        }
        catch (Exception exception)
        {
            ReportFailure("Vm.Failure.CopyEndpoint", exception);
        }
    }

    private void SelectConnection(object? parameter)
    {
        if (parameter is ConnectionProfile && ResolveProfile(parameter) is { } profile)
        {
            SelectedConnection = profile;
        }
    }

    private void CopySsh(object? parameter)
    {
        if (ResolveProfile(parameter) is not { } profile)
        {
            return;
        }

        try
        {
            _interactions.CopyToClipboard(SshCommandBuilder.BuildPowerShell(profile));
            SetStatus("Vm.Status.SshCopied", "Success");
        }
        catch (Exception exception)
        {
            ReportFailure("Vm.Failure.CopySsh", exception);
        }
    }

    /// <summary>Uses the live record for an ID rather than a stale card snapshot.</summary>
    private ConnectionProfile? ResolveProfile(object? parameter)
    {
        var candidate = parameter switch
        {
            null => SelectedConnection,
            ConnectionProfile profile => profile,
            _ => null
        };
        return candidate is null ? null : Connections.FirstOrDefault(profile => profile.Id == candidate.Id);
    }

    private bool MatchesConnection(object item)
    {
        if (item is not ConnectionProfile profile || (FavoritesOnly && !profile.IsFavorite))
        {
            return false;
        }

        var search = SearchText.Trim();
        return search.Length == 0
            || profile.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || profile.Host.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || profile.Group.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || profile.Username.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }

    private bool CanUseSelectedConnection() => !IsBusy && (ActiveConnection ?? SelectedConnection) is not null;

    private bool CanStartConnection() => !_isShuttingDown && !IsBusy && !IsConnecting && !IsDisconnecting && !HasActiveSession;

    private bool CanConnectProfile(object? parameter) => CanStartConnection() && ResolveProfile(parameter) is not null;

    private bool CanDisconnect() => !IsDisconnecting && (IsConnecting || _activeSession is not null);

    private bool CanCancelConnection() => IsConnecting && _connectionCancellation is { IsCancellationRequested: false };

    private bool CanEditProfiles() => !IsBusy && ProfilesAvailable;

    private bool CanEditSelectedProfile(object? parameter) => CanEditProfiles() && ResolveProfile(parameter) is not null;

    private bool CanSelectConnection(object? parameter)
        => !IsBusy && parameter is ConnectionProfile && ResolveProfile(parameter) is not null;

    private bool CanCopySsh(object? parameter)
        => !IsBusy && ResolveProfile(parameter)?.Protocol is ConnectionProtocol.Ssh or ConnectionProtocol.Sftp;

    private bool CanGoUp() => !IsBusy && Directory.GetParent(_loadedLocalPath) is not null;

    private void ConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(ConnectionCount));
        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(HasConnections));
    }

    private void FilteredConnectionsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(HasFilteredConnections));
        OnPropertyChanged(nameof(FilteredConnectionCount));
    }

    private void RefreshCommandStates()
    {
        AddConnectionCommand.NotifyCanExecuteChanged();
        EditConnectionCommand.NotifyCanExecuteChanged();
        DeleteConnectionCommand.NotifyCanExecuteChanged();
        ToggleFavoriteCommand.NotifyCanExecuteChanged();
        SelectConnectionCommand.NotifyCanExecuteChanged();
        CopySshCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        ManualConnectCommand.NotifyCanExecuteChanged();
        SaveManualConnectionCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        CancelConnectionCommand.NotifyCanExecuteChanged();
        RefreshLocalCommand.NotifyCanExecuteChanged();
        GoUpCommand.NotifyCanExecuteChanged();
        BrowseLocalCommand.NotifyCanExecuteChanged();
        NavigateLocalCommand.NotifyCanExecuteChanged();
        OpenLocalEntryCommand.NotifyCanExecuteChanged();
        CopyEndpointCommand.NotifyCanExecuteChanged();
        RefreshRemoteCommandStates();
    }

    private void ReportCommandFailure(Exception exception)
        => ReportFailure("Vm.Failure.Operation", exception);

    private void ReportConnectionFailure(Exception exception)
    {
        var key = exception switch
        {
            OperationCanceledException => "Vm.Status.Canceled",
            TimeoutException => "Vm.Connect.Timeout",
            RemoteConnectionException remote => remote.Code switch
            {
                RemoteConnectionErrorCode.UnsupportedProtocol => "Vm.Connect.Unsupported",
                RemoteConnectionErrorCode.InvalidSettings => "Vm.Connect.InvalidSettings",
                RemoteConnectionErrorCode.AuthenticationFailed => "Vm.Connect.Authentication",
                RemoteConnectionErrorCode.PrivateKeyInvalid => "Vm.Connect.PrivateKey",
                RemoteConnectionErrorCode.HostKeyRejected => "Vm.Connect.HostRejected",
                RemoteConnectionErrorCode.HostKeyChanged => "Vm.Connect.HostChanged",
                RemoteConnectionErrorCode.HostTrustStorageFailed => "Vm.Connect.TrustStorage",
                RemoteConnectionErrorCode.CredentialStorageFailed => "Vm.Connect.CredentialStorage",
                RemoteConnectionErrorCode.NameResolutionFailed => "Vm.Connect.NameResolution",
                RemoteConnectionErrorCode.ConnectionRefused => "Vm.Connect.Refused",
                RemoteConnectionErrorCode.Timeout => "Vm.Connect.Timeout",
                RemoteConnectionErrorCode.ConnectionLost => "Vm.Status.ConnectionLost",
                _ => "Vm.Connect.Failed"
            },
            NotSupportedException => "Vm.Connect.Unsupported",
            _ => "Vm.Connect.Failed"
        };
        SetConnectionError(new UiMessage(key));
        SetStatus(_connectionError!, exception is OperationCanceledException ? "Info" : "Error");
    }

    private void ReportFailure(string titleKey, Exception exception)
    {
        var detailKey = exception switch
        {
            UnauthorizedAccessException => "Vm.Failure.Permission",
            DirectoryNotFoundException => "Vm.Failure.Directory",
            FileNotFoundException => "Vm.Failure.File",
            ArgumentException => "Vm.Failure.Values",
            _ => "Vm.Failure.Generic"
        };
        SetStatus(new UiMessage("Vm.Failure.Composite", new UiMessage(titleKey), new UiMessage(detailKey)), "Error");
        _interactions.ShowInformation(UiText.Get(titleKey), UiText.Get(detailKey));
    }

    private void SetStatus(string key, string kind, params object?[] arguments)
        => SetStatus(new UiMessage(key, arguments), kind);

    private void SetStatus(UiMessage message, string kind)
    {
        // A successful folder refresh must not hide a profile-store failure, nor
        // invite a new save that could overwrite profiles the app could not read.
        _statusMessage = message;
        OnPropertyChanged(nameof(StatusMessage));
        StatusKind = _profileLoadFailure is null ? kind : "Error";
        _log?.Invoke(StatusKind, _profileLoadFailure is null ? message.English : UiText.English(_profileLoadFailure));
    }

    private static string GetProtocolLabel(ConnectionProtocol protocol) => protocol switch
    {
        ConnectionProtocol.Ssh => "SSH / SFTP",
        ConnectionProtocol.Sftp => "SFTP",
        ConnectionProtocol.Ftp => "FTP",
        ConnectionProtocol.Ftps => "FTPS",
        _ => protocol.ToString()
    };

    private static string GetEndpoint(ConnectionProfile profile)
    {
        var host = profile.Host.Contains(':') && !profile.Host.StartsWith('[')
            ? $"[{profile.Host}]"
            : profile.Host;
        var user = string.IsNullOrWhiteSpace(profile.Username) ? string.Empty : $"{profile.Username}@";
        return $"{user}{host}:{profile.Port}";
    }

    private static string GetInitialLocalPath()
    {
        var userFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(userFolder) ? userFolder : Environment.CurrentDirectory;
    }
}
