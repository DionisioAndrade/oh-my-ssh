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
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Infrastructure.Connections;

namespace OhMySsh.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IConnectionProfileStore _store;
    private readonly ILocalFileService _localFiles;
    private readonly IDesktopInteractionService _interactions;
    private readonly IRemoteConnectionService _remoteConnections;
    private readonly ReplaceableObservableCollection<FileEntry> _localEntries = [];
    private string _searchText = string.Empty;
    private ConnectionProfile? _selectedConnection;
    private FileEntry? _selectedLocalEntry;
    private string _localPath;
    private string _loadedLocalPath;
    private string _statusMessage = "Pronto para começar";
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
    private string _connectionError = string.Empty;
    private string _manualName = string.Empty;
    private string _manualHost = string.Empty;
    private string _manualPort = "22";
    private string _manualUsername = string.Empty;
    private string _manualPrivateKeyPath = string.Empty;
    private ConnectionProtocol _manualProtocol = ConnectionProtocol.Ssh;

    public MainViewModel(
        IConnectionProfileStore store,
        ILocalFileService localFiles,
        IDesktopInteractionService interactions,
        IRemoteConnectionService? remoteConnections = null)
    {
        _store = store;
        _localFiles = localFiles;
        _interactions = interactions;
        _remoteConnections = remoteConnections ?? new UnavailableRemoteConnectionService();
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

                ConnectionError = string.Empty;
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

    public string ConnectionError
    {
        get => _connectionError;
        private set
        {
            if (SetProperty(ref _connectionError, value))
            {
                OnPropertyChanged(nameof(HasConnectionError));
            }
        }
    }

    public bool HasConnectionError => ConnectionError.Length > 0;
    public bool HasActiveSession => _activeSession?.IsConnected == true;
    public ConnectionProfile? ActiveConnection => HasActiveSession ? _activeConnection : null;
    public string ActiveConnectionEndpoint => ActiveConnection is { } profile ? GetEndpoint(profile) : "Nenhuma conexão ativa";

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
        set => SetProperty(ref _selectedLocalEntry, value);
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
            return $"{LocalEntries.Count} itens · {directories} pastas · {files} arquivos";
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
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
    public string SelectedConnectionTitle => (ActiveConnection ?? SelectedConnection)?.Name ?? "Área de trabalho";
    public string SelectedConnectionEndpoint => (ActiveConnection ?? SelectedConnection) is { } profile ? GetEndpoint(profile) : "Nenhuma conexão selecionada";
    public string SelectedProtocolLabel => (ActiveConnection ?? SelectedConnection) is { } profile ? GetProtocolLabel(profile.Protocol) : "SSH / SFTP";
    public string SessionSummary => (ActiveConnection ?? SelectedConnection) is { } profile
        ? $"{GetProtocolLabel(profile.Protocol)} · {GetEndpoint(profile)} · {(HasActiveSession ? "conectado" : "desconectado")}"
        : "Organize suas conexões e arquivos em um só lugar";
    public string TerminalMessage => ActiveConnection is { } active
        ? $"{active.Name}\n{GetEndpoint(active)}\n\nSessão conectada. O terminal interativo ainda não está disponível nesta versão."
        : SelectedConnection is { } profile
            ? $"{profile.Name}\n{GetEndpoint(profile)}\n\nNenhuma sessão aberta. O terminal interativo ainda não está disponível nesta versão."
        : "Nenhuma sessão aberta.\n\nCadastre uma conexão para organizar seus servidores.\nO terminal interativo ainda não está disponível nesta versão.";
    public string RemoteHint => (ActiveConnection ?? SelectedConnection) is { } profile
        ? $"Os arquivos de {profile.Host} aparecerão aqui quando a integração de {GetProtocolLabel(profile.Protocol)} estiver disponível."
        : "Selecione uma conexão para preparar o acesso aos arquivos do servidor.";

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
            _profileLoadFailure = "Conexões não carregadas. O cadastro está bloqueado para preservar seus perfis salvos.";
            ReportFailure("Não foi possível carregar as conexões", exception);
        }
        finally
        {
            IsBusy = false;
        }

        await NavigateToAsync(_loadedLocalPath);
        if (StatusKind != "Error" && !HasConnectionError && !HasActiveSession)
        {
            SetStatus("Selecione uma VPS salva ou informe os dados de conexão", "Info");
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
                throw new ArgumentException("Informe o caminho de uma pasta.");
            }

            var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
            var fullPath = Path.GetFullPath(expandedPath, _loadedLocalPath);
            var entries = await _localFiles.GetEntriesAsync(fullPath);

            _localEntries.ReplaceAll(entries);

            _loadedLocalPath = fullPath;
            LocalPath = fullPath;
            SelectedLocalEntry = null;
            SetStatus($"Pasta local atualizada · {LocalEntries.Count} itens", "Success");
        }
        catch (Exception exception)
        {
            LocalPath = _loadedLocalPath;
            ReportFailure("Não foi possível abrir a pasta", exception);
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
        if (await SaveConnectionsAsync(updated, profile.Id, $"VPS “{profile.Name}” salva", clearFilters: true))
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
        if (await SaveConnectionsAsync(updated, edited.Id, $"VPS “{edited.Name}” atualizada", clearFilters: true))
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
        await SaveConnectionsAsync(updated, selectedId, $"VPS “{profile.Name}” removida");
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
            ? $"“{profile.Name}” removida dos favoritos"
            : $"“{profile.Name}” adicionada aos favoritos";
        await SaveConnectionsAsync(updated, selectedId, message);
    }

    private async Task<bool> SaveConnectionsAsync(ConnectionProfile[] profiles, Guid? selectedId, string successMessage, bool clearFilters = false)
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
        if (await SaveConnectionsAsync(updated, profile.Id, $"VPS “{profile.Name}” salva", clearFilters: true))
        {
            ProfileSaved?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryCreateManualProfile(string group, out ConnectionProfile? profile)
    {
        var valid = ConnectionDraftValidator.TryCreate(
            ManualName, ManualHost, ManualPort, ManualUsername, ManualPrivateKeyPath, ManualProtocol, group,
            out profile, out var error);
        ConnectionError = error;
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
        _connectionCancellation = cancellation;
        ConnectionError = string.Empty;
        IsConnecting = true;
        SetStatus($"Conectando a {profile.Host}…", "Info");
        IRemoteConnectionSession? candidate = null;
        Task<IRemoteConnectionSession>? connectionTask = null;
        var activated = false;
        try
        {
            // An adapter may report a connection loss between UI actions. Retire
            // that disconnected handle before requesting a replacement.
            if (_activeSession is { } previous)
            {
                _activeSession = null;
                _activeConnection = null;
                NotifyActiveSessionChanged();
                await DisposeQuietlyAsync(previous).WaitAsync(cancellation.Token);
            }

            connectionTask = _remoteConnections.ConnectAsync(profile with { }, cancellation.Token);
            candidate = await connectionTask.WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (candidate is null || !candidate.IsConnected)
            {
                throw new InvalidOperationException("O servidor não confirmou a conexão. Tente novamente.");
            }

            var connectedProfile = candidate.Profile
                ?? throw new InvalidOperationException("A sessão retornada não possui um perfil válido.");
            _activeConnection = connectedProfile with { };
            _activeSession = candidate;
            candidate = null;
            activated = true;
            NotifySessionProperties();
            SetStatus($"Conectado a {_activeConnection.Host}", "Success");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (candidate is null && connectionTask is not null)
            {
                // WaitAsync releases the UI even if an adapter ignores its
                // token. A late result must never activate a canceled session.
                _ = DisposeAbandonedSessionAsync(connectionTask);
            }

            ConnectionError = "Conexão cancelada.";
            SetStatus(ConnectionError, "Info");
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
        }

        if (activated)
        {
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
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
        _activeSession = null;
        _activeConnection = null;
        ConnectionError = string.Empty;
        NotifyActiveSessionChanged();
        try
        {
            await session.DisposeAsync();
            SetStatus("Desconectado. Selecione uma VPS salva ou informe os dados de conexão", "Info");
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
            ConnectionError = string.Empty;
        }
    }

    private void CopyEndpoint()
    {
        try
        {
            _interactions.CopyToClipboard(SelectedConnectionEndpoint);
            SetStatus("Endereço da conexão copiado", "Success");
        }
        catch (Exception exception)
        {
            ReportFailure("Não foi possível copiar o endereço", exception);
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
            SetStatus("Comando SSH copiado para o PowerShell", "Success");
        }
        catch (Exception exception)
        {
            ReportFailure("Não foi possível copiar o comando SSH", exception);
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

    private bool CanStartConnection() => !IsBusy && !IsConnecting && !IsDisconnecting && !HasActiveSession;

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
    }

    private void ReportCommandFailure(Exception exception)
        => ReportFailure("Não foi possível concluir a operação", exception);

    private void ReportConnectionFailure(Exception exception)
    {
        ConnectionError = exception switch
        {
            OperationCanceledException => "Conexão cancelada.",
            TimeoutException => "O servidor não respondeu a tempo. Confira os dados e tente novamente.",
            _ => exception.Message
        };
        SetStatus(ConnectionError, "Error");
    }

    private void ReportFailure(string title, Exception exception)
    {
        var detail = exception switch
        {
            UnauthorizedAccessException => "Você não tem permissão para acessar esse local.",
            DirectoryNotFoundException => "A pasta não foi encontrada. Confira o caminho e tente novamente.",
            FileNotFoundException => "O arquivo não foi encontrado. Atualize a pasta e tente novamente.",
            _ => exception.Message
        };

        SetStatus($"{title}: {detail}", "Error");
        _interactions.ShowInformation(title, detail);
    }

    private void SetStatus(string message, string kind)
    {
        // A successful folder refresh must not hide a profile-store failure, nor
        // invite a new save that could overwrite profiles the app could not read.
        StatusMessage = _profileLoadFailure ?? message;
        StatusKind = _profileLoadFailure is null ? kind : "Error";
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
