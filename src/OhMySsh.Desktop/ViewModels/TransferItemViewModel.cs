using System.Globalization;
using OhMySsh.Core.Models;

namespace OhMySsh.Desktop.ViewModels;

public enum TransferState { Queued, Running, Completed, Failed, Canceled }
public enum TransferDirection { Upload, Download }

/// <summary>One in-memory transfer bound to the connection and paths captured when queued.</summary>
public sealed class TransferItemViewModel : ObservableObject
{
    private TransferState _state;
    private long _bytesTransferred;
    private long _totalBytes;
    private string _error = string.Empty;
    private bool _finalizing;
    private bool _cancelRequested;

    internal TransferItemViewModel(ConnectionProfile profile, string source, string destination,
        string fileName, TransferDirection direction, CancellationToken lifetime)
    {
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
    public string Error => _error;
    public bool CanCancel => !_cancelRequested && !_finalizing && State is TransferState.Queued or TransferState.Running;
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
        _state = state;
        _error = error;
        if (state == TransferState.Completed) _bytesTransferred = _totalBytes;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Error));
        NotifyProgress();
    }

    internal void UpdateProgress(TransferProgress progress)
    {
        if (_finalizing && !progress.IsFinalizing) return;
        _totalBytes = Math.Max(0, progress.TotalBytes);
        _bytesTransferred = Math.Clamp(progress.BytesTransferred, 0, _totalBytes);
        _finalizing |= progress.IsFinalizing;
        NotifyProgress();
    }

    private void NotifyProgress()
    {
        OnPropertyChanged(nameof(BytesTransferred));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(BytesLabel));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(StatusLabel));
    }

    private static string FormatBytes(long count)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, count);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "N0" : "N1", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
