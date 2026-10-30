using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Services;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Owns independent explicitly opened shell tabs for one immutable VPS session.</summary>
public sealed class TerminalWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    public const int MaximumTerminals = 8;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string, string>? _log;
    private readonly List<Task> _retiring = [];
    private IRemoteConnectionSession? _session;
    private TerminalSessionViewModel _selectedTerminal = null!;
    private int _nextNumber;
    private long _generation;
    private bool _disposed;
    private string? _errorKey;

    public TerminalWorkspaceViewModel(Action<string, string>? log = null, Dispatcher? dispatcher = null)
    {
        _log = log;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        NewTerminalCommand = new AsyncRelayCommand(NewTerminalAsync, CanCreateTerminal, _ => SetError("TerminalTabs.Failed"));
        SelectTerminalCommand = new RelayCommand(parameter => SelectedTerminal = (TerminalSessionViewModel)parameter!,
            parameter => !_disposed && parameter is TerminalSessionViewModel tab && Terminals.Contains(tab));
        CloseTerminalCommand = new AsyncRelayCommand(CloseTerminalAsync,
            parameter => !_disposed && parameter is TerminalSessionViewModel tab && Terminals.Contains(tab),
            _ => SetError("TerminalTabs.Failed"));
        _selectedTerminal = AddReadyTerminal();
    }

    public ObservableCollection<TerminalSessionViewModel> Terminals { get; } = [];
    /// <summary>Requests focus only after an explicit new-tab action opens the still-selected shell.</summary>
    public event Action<TerminalSessionViewModel>? TerminalOpened;
    public TerminalSessionViewModel SelectedTerminal
    {
        get => _selectedTerminal;
        set
        {
            if (value is null || !Terminals.Contains(value)) return;
            SetProperty(ref _selectedTerminal, value);
        }
    }
    public AsyncRelayCommand NewTerminalCommand { get; }
    public RelayCommand SelectTerminalCommand { get; }
    public AsyncRelayCommand CloseTerminalCommand { get; }
    public bool HasError => _errorKey is not null || Terminals.Count >= MaximumTerminals;
    public string Error => _errorKey is not null ? UiText.Get(_errorKey)
        : Terminals.Count >= MaximumTerminals ? UiText.Get("TerminalTabs.Limit") : string.Empty;

    public void AttachSession(IRemoteConnectionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_disposed || ReferenceEquals(_session, session)) return;
        _ = DetachAsync();
        _session = session;
        foreach (var terminal in Terminals) terminal.AttachSession(session);
        NotifyState();
    }

    public Task DetachAsync()
    {
        if (_disposed) return Task.WhenAll(_retiring.ToArray());
        _session = null;
        ++_generation;
        _errorKey = null;
        var old = Terminals.ToArray();
        foreach (var terminal in old) terminal.PropertyChanged -= Terminal_PropertyChanged;
        Terminals.Clear();
        _nextNumber = 0;
        SelectedTerminal = AddReadyTerminal();
        foreach (var terminal in old) TrackRetirement(terminal.DisposeAsync().AsTask());
        NotifyState();
        return Task.WhenAll(_retiring.ToArray());
    }

    private TerminalSessionViewModel AddReadyTerminal()
    {
        var tab = new TerminalSessionViewModel(_log, _dispatcher, ++_nextNumber);
        if (_session is { } session) tab.AttachSession(session);
        tab.PropertyChanged += Terminal_PropertyChanged;
        Terminals.Add(tab);
        return tab;
    }

    private bool CanCreateTerminal() => !_disposed && Terminals.Count < MaximumTerminals
        && _session is { IsConnected: true, Shells: not null };

    private async Task NewTerminalAsync()
    {
        if (!CanCreateTerminal()) return;
        SetError(null);
        var generation = _generation;
        var tab = AddReadyTerminal();
        SelectedTerminal = tab;
        NotifyState();
        await tab.OpenCommand.ExecuteAsync();
        if (_disposed || generation != _generation || !Terminals.Contains(tab)) return;
        if (!tab.IsOpen) SetError("TerminalTabs.Failed");
        else if (ReferenceEquals(SelectedTerminal, tab)) TerminalOpened?.Invoke(tab);
    }

    private async Task CloseTerminalAsync(object? parameter)
    {
        if (parameter is not TerminalSessionViewModel tab || !Terminals.Contains(tab)) return;
        var index = Terminals.IndexOf(tab);
        var selected = ReferenceEquals(SelectedTerminal, tab);
        tab.PropertyChanged -= Terminal_PropertyChanged;
        Terminals.Remove(tab);
        if (Terminals.Count == 0) SelectedTerminal = AddReadyTerminal();
        else if (selected) SelectedTerminal = Terminals[Math.Min(index, Terminals.Count - 1)];
        SetError(null);
        var retirement = tab.DisposeAsync().AsTask();
        TrackRetirement(retirement);
        NotifyState();
        await retirement;
    }

    private void TrackRetirement(Task retirement)
    {
        _retiring.RemoveAll(task => task.IsCompletedSuccessfully);
        _retiring.Add(retirement);
        _ = retirement.ContinueWith(_ =>
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(NotifyState));
        }, TaskScheduler.Default);
    }

    private void Terminal_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalSessionViewModel.IsOpening) or nameof(TerminalSessionViewModel.IsOpen)) NotifyState();
    }

    private void SetError(string? key)
    {
        _errorKey = key;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    private void NotifyState()
    {
        _retiring.RemoveAll(task => task.IsCompletedSuccessfully);
        NewTerminalCommand.NotifyCanExecuteChanged();
        SelectTerminalCommand.NotifyCanExecuteChanged();
        CloseTerminalCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        var retirement = DetachAsync();
        _disposed = true;
        foreach (var terminal in Terminals)
        {
            terminal.PropertyChanged -= Terminal_PropertyChanged;
            await terminal.DisposeAsync();
        }
        await retirement;
        try { await TerminalPreferencesService.Current.FlushAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A previously reported appearance save failure cannot block SSH shutdown.
        }
        NotifyState();
    }
}
