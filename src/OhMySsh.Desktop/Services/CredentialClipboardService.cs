using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace OhMySsh.Desktop.Services;

/// <summary>Copies a secret with Windows history/cloud opt-outs and best-effort owned-entry cleanup.</summary>
public sealed class CredentialClipboardService : ICredentialClipboardService
{
    private const string MarkerFormat = "OhMySsh.CredentialCopyMarker";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const string CloudFormat = "CanUploadToCloudClipboard";
    private const string ExclusionFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private static readonly TimeSpan SecretLifetime = TimeSpan.FromSeconds(30);
    private readonly Dispatcher _dispatcher;
    private readonly Action<DataObject> _publishSecret;
    private readonly Func<Guid, bool> _clearOwnedEntry;
    private readonly TimeProvider _timeProvider;
    private readonly List<PendingEntry> _pendingEntries = [];
    private DispatcherTimer? _cleanupTimer;
    private bool _subscribedToExit;

    public static CredentialClipboardService Shared { get; } = new();

    public CredentialClipboardService()
        : this(data => Clipboard.SetDataObject(data, copy: true), TryClearOwnedEntry, TimeProvider.System)
    {
    }

    internal CredentialClipboardService(Action<DataObject> publishSecret, Func<Guid, bool> clearOwnedEntry, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(publishSecret);
        ArgumentNullException.ThrowIfNull(clearOwnedEntry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _publishSecret = publishSecret;
        _clearOwnedEntry = clearOwnedEntry;
        _timeProvider = timeProvider;
    }

    public void CopyAddress(string address)
    {
        _dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (address.Contains('\0')) throw new ArgumentException("The address cannot be copied.", nameof(address));
        Clipboard.SetText(address, TextDataFormat.UnicodeText);
        // Leave earlier ownership records until their deadlines. They will not clear an unrelated
        // entry, and must survive any reentrant or partially failed publication still in progress.
    }

    public void CopySecret(string secret)
    {
        _dispatcher.VerifyAccess();
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (secret.Contains('\0')) throw new ArgumentException("The secret cannot be represented by the clipboard.", nameof(secret));
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            throw new InvalidOperationException("The clipboard is unavailable while the application is closing.");
        var marker = Guid.NewGuid();
        var data = CreateSecretData(secret, marker);
        var pending = new PendingEntry(marker, _timeProvider.GetTimestamp());
        _pendingEntries.Add(pending);
        if (!_subscribedToExit && Application.Current is { } application)
        {
            application.Exit += (_, _) => CleanupAllEntries();
            _subscribedToExit = true;
        }
        // OleSetClipboard can publish before OleFlushClipboard throws. Arm this exact marker's
        // original deadline before either stage, retaining all earlier markers on failure.
        ScheduleCleanup();
        try { _publishSecret(data); }
        finally
        {
            pending.Publishing = false;
            ScheduleCleanup();
        }
    }

    private static DataObject CreateSecretData(string secret, Guid marker)
    {
        var data = new DataObject();
        // Registered-format streams become raw bytes; Windows expects serialized DWORD zero values.
        // Exclusion presence also requests exclusion of every format from Windows monitoring.
        data.SetData(ExclusionFormat, new MemoryStream([0], writable: false), autoConvert: false);
        data.SetData(HistoryFormat, new MemoryStream(new byte[sizeof(uint)], writable: false), autoConvert: false);
        data.SetData(CloudFormat, new MemoryStream(new byte[sizeof(uint)], writable: false), autoConvert: false);
        data.SetData(MarkerFormat, new MemoryStream(marker.ToByteArray(), writable: false), autoConvert: false);
        data.SetText(secret, TextDataFormat.UnicodeText);
        return data;
    }

    private DispatcherTimer CreateCleanupTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        timer.Tick += (_, _) => CleanupExpiredEntries();
        return timer;
    }

    private void CleanupExpiredEntries()
    {
        _dispatcher.VerifyAccess();
        foreach (var pending in _pendingEntries.ToArray())
        {
            // An in-flight publisher may still publish after a reentrant timer tick. Keep its
            // cleanup record until publication has returned, even if its deadline already passed.
            if (pending.Publishing || _timeProvider.GetElapsedTime(pending.StartedAt) < SecretLifetime) continue;
            if (ClearEntry(pending.Marker)) _pendingEntries.Remove(pending);
        }
        ScheduleCleanup();
    }

    private void CleanupAllEntries()
    {
        foreach (var pending in _pendingEntries.ToArray()) ClearEntry(pending.Marker);
        _pendingEntries.Clear();
        _cleanupTimer?.Stop();
    }

    private bool ClearEntry(Guid marker)
    {
        try { return _clearOwnedEntry(marker); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return false; }
    }

    private void ScheduleCleanup()
    {
        if (_pendingEntries.Count == 0)
        {
            _cleanupTimer?.Stop();
            return;
        }
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        var remaining = _pendingEntries.Min(entry => SecretLifetime - _timeProvider.GetElapsedTime(entry.StartedAt));
        // Retry busy clipboard access without resetting or extending any entry's original deadline.
        var interval = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
        _cleanupTimer ??= CreateCleanupTimer();
        _cleanupTimer.Interval = interval < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : interval;
        _cleanupTimer.Start();
    }

    private static bool TryClearOwnedEntry(Guid marker)
    {
        var opened = false;
        try
        {
            // Keep the clipboard locked while checking ownership and clearing, preventing a race
            // in which a new copy from another application would be erased after an earlier check.
            opened = OpenClipboard(0);
            if (!opened) return false;
            var format = RegisterClipboardFormat(MarkerFormat);
            if (format == 0) return false;
            var handle = GetClipboardData(format);
            if (handle == 0) return true;
            if (GlobalSize(handle) < (nuint)16) return true;
            var pointer = GlobalLock(handle);
            if (pointer == 0) return false;
            bool matches;
            try
            {
                var bytes = new byte[16];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                matches = new Guid(bytes) == marker;
            }
            finally { GlobalUnlock(handle); }
            return !matches || EmptyClipboard();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
        finally { if (opened) CloseClipboard(); }
    }

    private sealed class PendingEntry(Guid marker, long startedAt)
    {
        public Guid Marker { get; } = marker;
        public long StartedAt { get; } = startedAt;
        public bool Publishing { get; set; } = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
}
