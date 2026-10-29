using System.Globalization;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.ViewModels;

public enum TransferState { Queued, Running, Completed, Failed, Canceled }
public enum TransferDirection { Upload, Download }

/// <summary>One in-memory transfer bound to the connection and paths captured when queued.</summary>
public sealed class TransferItemViewModel : ObservableObject
{
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumRateInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    private readonly TimeProvider _timeProvider;
    private readonly List<RateSample> _rateSamples = [];
    private TransferState _state;
    private long _bytesTransferred;
    private long _totalBytes;
    private string _error = string.Empty;
    private bool _finalizing;
    private bool _cancelRequested;
    private long? _startedAt;
    private TimeSpan _elapsed;
    private TimeSpan _lastByteProgress;
    private double _bytesPerSecond;
    private TimeSpan? _estimatedRemaining;

    internal TransferItemViewModel(ConnectionProfile profile, string source, string destination,
        string fileName, TransferDirection direction, CancellationToken lifetime, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Profile = profile with { };
        SourcePath = source;
        DestinationPath = destination;
        FileName = fileName;
        Direction = direction;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    public Guid Id { get; } = Guid.NewGuid();
    public ConnectionProfile Profile { get; }
    public string ConnectionName => Profile.Name;
    public string SourcePath { get; }
    public string DestinationPath { get; }
    public string FileName { get; }
    public TransferDirection Direction { get; }
    public string DirectionLabel => Direction == TransferDirection.Upload ? "Envio" : "Download";
    public TransferState State => _state;
    public long BytesTransferred => _bytesTransferred;
    public long TotalBytes => _totalBytes;
    public double ProgressPercent => State == TransferState.Completed ? 100
        : TotalBytes <= 0 ? 0 : Math.Min(99.9, 100d * BytesTransferred / TotalBytes);
    public string BytesLabel => $"{FormatBytes(BytesTransferred)} / {FormatBytes(TotalBytes)}";
    public double BytesPerSecond => _bytesPerSecond;
    public TimeSpan? EstimatedRemaining => _estimatedRemaining;
    public TimeSpan Elapsed => _elapsed;
    public string SpeedLabel => State != TransferState.Running || _finalizing || Elapsed < MinimumRateInterval
        ? "—" : $"{FormatBytes(BytesPerSecond)}/s";
    public string RemainingTimeLabel => EstimatedRemaining is { } remaining ? FormatDuration(remaining, roundUp: true) : "—";
    public string ElapsedTimeLabel => State == TransferState.Queued ? "—" : FormatDuration(Elapsed);
    public string Error => _error;
    public bool CanCancel => !_cancelRequested && !_finalizing && State is TransferState.Queued or TransferState.Running;
    public bool CanRetry => State is TransferState.Failed or TransferState.Canceled;
    public string StatusLabel => _cancelRequested && State == TransferState.Running ? "Cancelando…" : State switch
    {
        TransferState.Queued => "Na fila",
        TransferState.Running when _finalizing => "Finalizando…",
        TransferState.Running => "Transferindo",
        TransferState.Completed => "Concluída",
        TransferState.Failed => "Falhou",
        _ => "Cancelada"
    };

    internal CancellationTokenSource Cancellation { get; }
    internal void RequestCancellation()
    {
        if (State is TransferState.Completed or TransferState.Failed or TransferState.Canceled) return;
        _cancelRequested = true;
        Cancellation.Cancel();
        if (State == TransferState.Queued) SetState(TransferState.Canceled);
        NotifyProgress();
    }

    internal void SetState(TransferState state, string error = "")
    {
        if (State == TransferState.Running) UpdateTiming();
        if (state == TransferState.Running && State != TransferState.Running)
        {
            _startedAt = _timeProvider.GetTimestamp();
            _elapsed = TimeSpan.Zero;
            _lastByteProgress = TimeSpan.Zero;
            _rateSamples.Clear();
            _rateSamples.Add(new RateSample(TimeSpan.Zero, BytesTransferred));
        }
        _state = state;
        _error = error;
        if (state == TransferState.Completed) _bytesTransferred = _totalBytes;
        if (state != TransferState.Running) { _bytesPerSecond = 0; _estimatedRemaining = null; }
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Error));
        NotifyProgress();
    }

    internal void UpdateProgress(TransferProgress progress)
    {
        if (_finalizing && !progress.IsFinalizing) return;
        var previousBytes = _bytesTransferred;
        _totalBytes = Math.Max(0, progress.TotalBytes);
        _bytesTransferred = Math.Clamp(progress.BytesTransferred, 0, _totalBytes);
        _finalizing |= progress.IsFinalizing;
        if (State == TransferState.Running)
        {
            if (_bytesTransferred > previousBytes) _lastByteProgress = GetElapsed();
            UpdateTiming();
        }
        NotifyProgress();
    }

    /// <summary>Refreshes live timing on the UI thread, including periods without byte updates.</summary>
    internal void RefreshTiming()
    {
        if (State != TransferState.Running) return;
        UpdateTiming();
        NotifyTiming();
    }

    private TimeSpan GetElapsed()
        => _startedAt is { } started ? _timeProvider.GetElapsedTime(started) : TimeSpan.Zero;

    private void UpdateTiming()
    {
        // TimeProvider timestamps are monotonic; wall-clock changes cannot alter a transfer's duration.
        var elapsed = GetElapsed();
        _elapsed = elapsed < _elapsed ? _elapsed : elapsed;
        _bytesPerSecond = 0;
        _estimatedRemaining = null;
        if (_finalizing || _startedAt is null) return;

        if (_rateSamples.Count == 0 || _elapsed - _rateSamples[^1].Elapsed >= SampleInterval)
            _rateSamples.Add(new RateSample(_elapsed, BytesTransferred));
        var cutoff = _elapsed - RateWindow;
        while (_rateSamples.Count > 1 && _rateSamples[1].Elapsed <= cutoff) _rateSamples.RemoveAt(0);

        var oldest = _rateSamples[0];
        var interval = _elapsed - oldest.Elapsed;
        if (interval < MinimumRateInterval || _elapsed - _lastByteProgress >= RateWindow) return;
        var rate = Math.Max(0, BytesTransferred - oldest.Bytes) / interval.TotalSeconds;
        if (!double.IsFinite(rate) || rate <= 0) return;
        _bytesPerSecond = rate;
        var remaining = TotalBytes - BytesTransferred;
        if (remaining <= 0) return;
        var seconds = remaining / rate;
        if (double.IsFinite(seconds) && seconds > 0)
            _estimatedRemaining = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1));
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(BytesTransferred));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(BytesLabel));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(StatusLabel));
        NotifyTiming();
    }

    private void NotifyTiming()
    {
        OnPropertyChanged(nameof(BytesPerSecond));
        OnPropertyChanged(nameof(EstimatedRemaining));
        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(SpeedLabel));
        OnPropertyChanged(nameof(RemainingTimeLabel));
        OnPropertyChanged(nameof(ElapsedTimeLabel));
    }

    private static string FormatBytes(double count)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, count);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan duration, bool roundUp = false)
    {
        var seconds = (long)(roundUp ? Math.Ceiling(duration.TotalSeconds) : Math.Floor(duration.TotalSeconds));
        if (seconds >= 86400) return $"{seconds / 86400} d {seconds % 86400 / 3600:00} h";
        if (seconds >= 3600) return $"{seconds / 3600} h {seconds % 3600 / 60:00} min";
        if (seconds >= 60) return $"{seconds / 60} min {seconds % 60:00} s";
        return $"{seconds} s";
    }

    private readonly record struct RateSample(TimeSpan Elapsed, long Bytes);
}
