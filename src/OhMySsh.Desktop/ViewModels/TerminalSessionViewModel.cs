using System.Buffers;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.Services;

namespace OhMySsh.Desktop.ViewModels;

/// <summary>Owns one explicitly opened shell, independently of the selected saved profile.</summary>
public sealed class TerminalSessionViewModel : ObservableObject, IAsyncDisposable
{
    // Leave space for bracketed-paste framing and protocol replies around a 1 MiB paste.
    private const int InputBudget = 2 * 1024 * 1024;
    private readonly Dispatcher _dispatcher;
    private readonly Action<string, string>? _log;
    private readonly List<Task> _retiring = [];
    private IRemoteConnectionSession? _session;
    private ConnectionProfile? _profile;
    private TerminalRun? _run;
    private TerminalEmulator? _emulator;
    private long _generation;
    private int _columns = 80;
    private int _rows = 24;
    private bool _opening;
    private bool _open;
    private bool _disposed;
    private string _statusKey = "Terminal.Disconnected";
    private readonly int _number;
    private string? _customName;

    public TerminalSessionViewModel(Action<string, string>? log = null, Dispatcher? dispatcher = null, int number = 1)
    {
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _log = log;
        _number = number;
        WeakEventManager<TerminalPreferencesService, EventArgs>.AddHandler(TerminalPreferencesService.Current,
            nameof(TerminalPreferencesService.Changed), Preferences_Changed);
        OpenCommand = new AsyncRelayCommand(OpenAsync, CanOpen, _ => ReportFailure("Terminal.Failed"));
        CloseCommand = new AsyncRelayCommand(CloseAsync,
            () => _run is not null || IsOpening || Emulator is not null, _ => ReportFailure("Terminal.Failed"));
    }

    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand CloseCommand { get; }
    public string Name
    {
        get => _customName ?? UiText.Format("TerminalTabs.DefaultName", _number);
        set
        {
            var cleaned = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (cleaned.Length > 64) cleaned = cleaned[..(char.IsHighSurrogate(cleaned[63]) ? 63 : 64)];
            var custom = cleaned.Length == 0 ? null : cleaned;
            if (SetProperty(ref _customName, custom, nameof(Name))) OnPropertyChanged(nameof(HasCustomName));
        }
    }
    public bool HasCustomName => _customName is not null;
    public TerminalEmulator? Emulator { get => _emulator; private set => SetProperty(ref _emulator, value); }
    public bool IsOpening { get => _opening; private set { if (SetProperty(ref _opening, value)) NotifyState(); } }
    public bool IsOpen { get => _open; private set { if (SetProperty(ref _open, value)) NotifyState(); } }
    public bool HasEmulator => Emulator is not null;
    public string Status => UiText.Get(_statusKey);
    public string Dimensions => UiText.Format("Terminal.Size", _columns, _rows);
    public string Endpoint => _profile is { } profile
        ? $"{profile.Username}@{(profile.Host.Contains(':') ? $"[{profile.Host.Trim('[', ']')}]" : profile.Host)}:{profile.Port}"
        : UiText.Get("Terminal.NoConnection");

    public void AttachSession(IRemoteConnectionSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_disposed) return;
        if (ReferenceEquals(_session, session)) return;
        _ = StopCurrent();
        _session = session;
        _profile = session.Profile with { };
        SetStatus(session.Shells is null ? "Terminal.Unavailable" : "Terminal.Ready");
        OnPropertyChanged(nameof(Endpoint));
        NotifyState();
    }

    public Task DetachAsync()
    {
        _session = null;
        _profile = null;
        var retirement = StopCurrent();
        SetStatus("Terminal.Disconnected");
        OnPropertyChanged(nameof(Endpoint));
        NotifyState();
        return retirement;
    }

    public void RequestResize(int columns, int rows)
    {
        if (_disposed || columns < 2 || rows < 2 || columns > TerminalEmulator.MaximumColumns || rows > TerminalEmulator.MaximumRows) return;
        if (_columns == columns && _rows == rows) return;
        _columns = columns;
        _rows = rows;
        OnPropertyChanged(nameof(Dimensions));
        if (_run is not { } run || !IsOpen || !IsCurrent(run)) return;
        run.DesiredColumns = columns;
        run.DesiredRows = rows;
        SignalResize(run);
    }

    private bool CanOpen() => !_disposed && !IsOpening && !IsOpen && _run is null
        && _session is { IsConnected: true, Shells: not null } && _retiring.All(task => task.IsCompleted);

    private async Task OpenAsync()
    {
        if (_session is not { IsConnected: true, Shells: { } service } session || !CanOpen()) return;
        var preferences = TerminalPreferencesService.Current.Preferences;
        var emulator = new TerminalEmulator(_columns, _rows, preferences.ScrollbackLines);
        emulator.ApplyPreferences(preferences);
        var run = new TerminalRun(++_generation, session, emulator, _columns, _rows, new CancellationTokenSource());
        _run = run;
        run.InputHandler = (_, args) => EnqueueInput(run, args.Data);
        run.OwnerFinish = key => QueueFinish(run, key);
        run.Emulator.InputGenerated += run.InputHandler;
        Emulator = run.Emulator;
        OnPropertyChanged(nameof(HasEmulator));
        IsOpening = true;
        SetStatus("Terminal.Opening");
        try
        {
            Task<IRemoteShell> opening;
            try
            {
                opening = service.OpenAsync(new RemoteTerminalOptions(TerminalEmulator.TerminalType, _columns, _rows), run.Token);
                _ = opening.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully) run.OpenResult.TrySetResult(task.Result);
                    else { _ = task.Exception; run.OpenResult.TrySetResult(null); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch { run.OpenResult.TrySetResult(null); throw; }
            var shell = await opening.WaitAsync(run.Token);
            if (!IsCurrent(run) || run.Token.IsCancellationRequested) return;
            run.Shell = shell;
            run.AcceptInput = true;
            IsOpening = false;
            IsOpen = true;
            SetStatus("Terminal.Running");
            run.Writer = Task.Run(() => WriteLoopAsync(run));
            run.Reader = Task.Run(() => ReadLoopAsync(run));
            run.Resize = Task.Run(() => ResizeLoopAsync(run));
            run.DesiredColumns = _columns;
            run.DesiredRows = _rows;
            if (run.Columns != _columns || run.Rows != _rows) SignalResize(run);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrent(run))
            {
                _ = StopCurrent();
                SetStatus(GetErrorKey(exception));
            }
        }
        finally
        {
            if (ReferenceEquals(_run, run)) IsOpening = false;
            NotifyState();
        }
    }

    private void EnqueueInput(TerminalRun run, byte[] bytes)
    {
        if (!run.AcceptInput || run.Token.IsCancellationRequested || bytes.Length == 0)
        {
            Array.Clear(bytes);
            return;
        }
        var queued = Interlocked.Add(ref run.QueuedBytes, bytes.Length);
        if (bytes.Length > InputBudget || queued > InputBudget || !run.Input.Writer.TryWrite(bytes))
        {
            Interlocked.Add(ref run.QueuedBytes, -bytes.Length);
            Array.Clear(bytes);
            QueueFinish(run, "Terminal.InputOverflow");
        }
    }

    private async Task ReadLoopAsync(TerminalRun run)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (!run.Token.IsCancellationRequested)
            {
                var count = await run.Shell!.ReadAsync(buffer.AsMemory(0, 8192), run.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (run.Token.IsCancellationRequested) return;
                run.Emulator.Feed(buffer.AsSpan(0, count));
            }
            if (!run.Token.IsCancellationRequested)
            {
                await run.Shell!.Completion.WaitAsync(run.Token).ConfigureAwait(false);
                QueueFinish(run, "Terminal.Exited");
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (Exception exception) { if (!run.Token.IsCancellationRequested) QueueFinish(run, GetErrorKey(exception)); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private async Task WriteLoopAsync(TerminalRun run)
    {
        try
        {
            await foreach (var bytes in run.Input.Reader.ReadAllAsync(run.Token).ConfigureAwait(false))
            {
                Interlocked.Add(ref run.QueuedBytes, -bytes.Length);
                try { await run.Shell!.WriteAsync(bytes, run.Token).ConfigureAwait(false); }
                finally { Array.Clear(bytes); }
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (Exception exception) { if (!run.Token.IsCancellationRequested) QueueFinish(run, GetErrorKey(exception)); }
    }

    private static async Task ResizeLoopAsync(TerminalRun run)
    {
        try
        {
            while (!run.Token.IsCancellationRequested)
            {
                await run.ResizeRequested.WaitAsync(run.Token).ConfigureAwait(false);
                await Task.Delay(100, run.Token).ConfigureAwait(false);
                while (run.ResizeRequested.Wait(0)) { }
                var columns = Volatile.Read(ref run.DesiredColumns);
                var rows = Volatile.Read(ref run.DesiredRows);
                if (columns == run.Columns && rows == run.Rows) continue;
                run.Emulator.Resize(columns, rows);
                await run.Shell!.ResizeAsync(columns, rows, run.Token).ConfigureAwait(false);
                run.Columns = columns;
                run.Rows = rows;
            }
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested) { }
        catch (Exception exception) { if (!run.Token.IsCancellationRequested) run.OwnerFinish?.Invoke(GetErrorKey(exception)); }
    }

    private static void SignalResize(TerminalRun run)
    {
        try { run.ResizeRequested.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void QueueFinish(TerminalRun run, string key)
    {
        run.AcceptInput = false;
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsCurrent(run)) return;
            _ = StopCurrent();
            SetStatus(key);
        }));
    }

    private async Task CloseAsync()
    {
        var retirement = StopCurrent();
        var generation = _generation;
        SetStatus("Terminal.Closing");
        await retirement;
        if (_generation == generation && _session is { IsConnected: true }) SetStatus("Terminal.Closed");
        NotifyState();
    }

    private Task StopCurrent()
    {
        ++_generation;
        var run = _run;
        _run = null;
        IsOpening = false;
        IsOpen = false;
        Emulator = null;
        OnPropertyChanged(nameof(HasEmulator));
        if (run is not null)
        {
            run.AcceptInput = false;
            run.Cancellation.Cancel();
            run.Input.Writer.TryComplete();
            run.Emulator.InputGenerated -= run.InputHandler;
            _retiring.RemoveAll(task => task.IsCompleted);
            var retirement = RetireAsync(run);
            _retiring.Add(retirement);
            _ = retirement.ContinueWith(_ =>
            {
                if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(NotifyState));
            }, TaskScheduler.Default);
        }
        NotifyState();
        return Task.WhenAll(_retiring.ToArray());
    }

    private static async Task RetireAsync(TerminalRun run)
    {
        try
        {
            var shell = await run.OpenResult.Task.ConfigureAwait(false);
            if (shell is not null)
            {
                try { await shell.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { /* Parent session disposal still owns the transport. */ }
            }
            await Task.WhenAll(run.Reader, run.Writer, run.Resize).ConfigureAwait(false);
        }
        catch (Exception) { /* Owning loops report only sanitized categories. */ }
        finally
        {
            while (run.Input.Reader.TryRead(out var bytes)) Array.Clear(bytes);
            run.Emulator.Dispose();
            run.ResizeRequested.Dispose();
            run.Cancellation.Dispose();
        }
    }

    private bool IsCurrent(TerminalRun run) => ReferenceEquals(_run, run) && run.Generation == _generation
        && ReferenceEquals(_session, run.Session) && run.Session.IsConnected && !_disposed;

    private void NotifyState()
    {
        _retiring.RemoveAll(task => task.IsCompleted);
        OpenCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasEmulator));
    }

    private void SetStatus(string key)
    {
        _statusKey = key;
        OnPropertyChanged(nameof(Status));
        _log?.Invoke(key is "Terminal.Failed" or "Terminal.InputOverflow" or "Terminal.OutputOverflow" ? "Error" : "Info", UiText.English(key));
    }

    private void ReportFailure(string key) { _ = StopCurrent(); SetStatus(key); }

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(new Action(() => Preferences_Changed(sender, e)));
            return;
        }
        Emulator?.ApplyPreferences(TerminalPreferencesService.Current.Preferences);
    }

    private static string GetErrorKey(Exception exception) => exception is RemoteTerminalException { Code: RemoteTerminalErrorCode.BufferLimitExceeded }
        ? "Terminal.OutputOverflow" : "Terminal.Failed";

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        var retirement = DetachAsync();
        _disposed = true;
        WeakEventManager<TerminalPreferencesService, EventArgs>.RemoveHandler(TerminalPreferencesService.Current,
            nameof(TerminalPreferencesService.Changed), Preferences_Changed);
        await retirement;
    }

    private sealed class TerminalRun(long generation, IRemoteConnectionSession session, TerminalEmulator emulator, int columns, int rows,
        CancellationTokenSource cancellation)
    {
        public long Generation { get; } = generation;
        public IRemoteConnectionSession Session { get; } = session;
        public TerminalEmulator Emulator { get; } = emulator;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public CancellationToken Token { get; } = cancellation.Token;
        public SemaphoreSlim ResizeRequested { get; } = new(0, 1);
        public TaskCompletionSource<IRemoteShell?> OpenResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRemoteShell? Shell { get; set; }
        public EventHandler<TerminalInputEventArgs>? InputHandler { get; set; }
        public Channel<byte[]> Input { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        public Task Reader { get; set; } = Task.CompletedTask;
        public Task Writer { get; set; } = Task.CompletedTask;
        public Task Resize { get; set; } = Task.CompletedTask;
        public int Columns = columns;
        public int Rows = rows;
        public int DesiredColumns = columns;
        public int DesiredRows = rows;
        public int QueuedBytes;
        public volatile bool AcceptInput;
        public Action<string>? OwnerFinish { get; set; }
    }
}
