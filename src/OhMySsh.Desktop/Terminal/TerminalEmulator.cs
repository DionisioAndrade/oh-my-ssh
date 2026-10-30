using System.Text;
using XTerm;
using XTerm.Input;
using XTerm.Options;

namespace OhMySsh.Desktop.Terminal;

/// <summary>
/// Serializes the VT state machine and exposes detached, bounded viewports to native controls.
/// SSH transport and session lifetime belong to the owner, never to this engine.
/// </summary>
public sealed class TerminalEmulator : IDisposable
{
    public const string TerminalType = "xterm-256color";
    public const int MaximumColumns = 500;
    public const int MaximumRows = 200;
    private readonly object _gate = new();
    private XTerm.Terminal _terminal;
    private XTerm.Buffer.TerminalBuffer _normalBuffer;
    private XTerm.Buffer.TerminalBuffer _alternateBuffer;
    private readonly Queue<byte[]> _pendingInput = [];
    private int _publishingInput;
    private long _outputVersion;
    private bool _hasNewOutputWhileScrolled;
    private bool _trackOutputTrims;
    private int _outputTrimmedLines;
    private bool _disposed;

    public TerminalEmulator(int columns = 80, int rows = 24, int scrollback = 2000)
    {
        ValidateDimensions(columns, rows);
        ArgumentOutOfRangeException.ThrowIfNegative(scrollback);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scrollback, 100_000);
        _terminal = new XTerm.Terminal(new TerminalOptions
        {
            Cols = columns,
            Rows = rows,
            Scrollback = scrollback,
            TermName = TerminalType,
            CursorBlink = true,
            SixelEnabled = false,
            KittyGraphicsEnabled = false,
            ITerm2ImagesEnabled = false,
            KittyNotificationsEnabled = false,
            ClipboardReadEnabled = false,
            ClipboardWriteEnabled = false,
            PointerShapesEnabled = false,
            // This host implements the standard xterm keyboard protocol.
            KittyKeyboardEnabled = false,
            AllowPasteControls = false,
            Theme = new ThemeOptions
            {
                Foreground = "#D1D5DF", Background = "#202128", Cursor = "#D1D5DF"
            }
        });
        _normalBuffer = _terminal.Buffer;
        _normalBuffer.Trimmed += OnNormalBufferTrimmed;
        // Capture both stable buffers before the facade can be observed or emit input.
        _terminal.SwitchToAltBuffer();
        _alternateBuffer = _terminal.Buffer;
        _terminal.SwitchToNormalBuffer();
        _terminal.DataReceived += OnDataReceived;
    }

    public event EventHandler<TerminalInputEventArgs>? InputGenerated;
    public event EventHandler? Changed;

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        lock (_gate)
        {
            if (_disposed) return;
            var readingHistory = !_terminal.IsAlternateBufferActive && !_terminal.Buffer.IsAtBottom;
            var historyPosition = _normalBuffer.YDisp;
            _outputTrimmedLines = 0;
            _trackOutputTrims = readingHistory;
            try { _terminal.Write(data); }
            finally { _trackOutputTrims = false; }
            // The pinned engine leaves YDisp unchanged when a full history ring rotates,
            // which would move the text under a reader. Anchor to the retained logical rows.
            if (readingHistory) _normalBuffer.ScrollToLine(Math.Max(0, historyPosition - _outputTrimmedLines));
            _outputVersion++;
            if (readingHistory) _hasNewOutputWhileScrolled = true;
            if (_terminal.Buffer.IsAtBottom && !_terminal.IsAlternateBufferActive) _hasNewOutputWhileScrolled = false;
        }
        Publish();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resize(int columns, int rows)
    {
        ValidateDimensions(columns, rows);
        Mutate(terminal => ResizeCore(terminal, columns, rows));
    }

    /// <summary>Changes presentation and the history limit without replaying input or resetting screens.</summary>
    public void ApplyPreferences(TerminalPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var settings = preferences.Validate();
        Mutate(terminal =>
        {
            var palette = settings.AnsiColors;
            var theme = new ThemeOptions
            {
                Foreground = settings.Foreground, Background = settings.Background, Cursor = settings.CursorColor,
                Selection = settings.SelectionColor, Black = palette[0], Red = palette[1], Green = palette[2],
                Yellow = palette[3], Blue = palette[4], Magenta = palette[5], Cyan = palette[6], White = palette[7],
                BrightBlack = palette[8], BrightRed = palette[9], BrightGreen = palette[10], BrightYellow = palette[11],
                BrightBlue = palette[12], BrightMagenta = palette[13], BrightCyan = palette[14], BrightWhite = palette[15]
            };
            terminal.Options.Theme = theme;
            terminal.Colors.ApplyTheme(theme);
            terminal.Options.CursorStyle = Enum.Parse<XTerm.Common.CursorStyle>(settings.CursorStyle.ToString());
            terminal.Options.CursorBlink = settings.CursorBlink;
            terminal.Options.DrawBoldTextInBrightColors = settings.BoldBrightColors;
            terminal.Options.Scrollback = settings.ScrollbackLines;
            _normalBuffer.SetScrollback(settings.ScrollbackLines);
            if (_normalBuffer.IsAtBottom) _hasNewOutputWhileScrolled = false;
        });
    }

    public void SendKey(TerminalKey key, TerminalModifiers modifiers = TerminalModifiers.None)
    {
        Mutate(terminal =>
        {
            PrepareInput(terminal);
            if (Enum.TryParse<XTerm.Input.Key>(key.ToString(), out var engineKey))
                QueueInput(terminal.GenerateKeyInput(engineKey, (KeyModifiers)modifiers));
        });
    }

    public void SendText(string text, TerminalModifiers modifiers = TerminalModifiers.None)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return;
        Mutate(terminal =>
        {
            PrepareInput(terminal);
            if ((modifiers & (TerminalModifiers.Alt | TerminalModifiers.Control)) == 0)
            {
                QueueInput(text);
                return;
            }
            foreach (var character in text)
                QueueInput(terminal.GenerateCharInput(character, (KeyModifiers)modifiers));
        });
    }

    public void Paste(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Mutate(terminal => { PrepareInput(terminal); terminal.Paste(text); });
    }

    public void SendFocus(bool focused) => Mutate(terminal => QueueInput(terminal.GenerateFocusEvent(focused)));

    public bool IsMouseTracking
    {
        get { lock (_gate) return !_disposed && _terminal.MouseTrackingMode != MouseTrackingMode.None; }
    }

    public void SendPointer(TerminalPointerButton button, int column, int row,
        TerminalPointerAction action, TerminalModifiers modifiers = TerminalModifiers.None)
    {
        Mutate(terminal =>
        {
            if (terminal.MouseTrackingMode == MouseTrackingMode.None) return;
            var engineAction = action switch
            {
                TerminalPointerAction.Down => MouseEventType.Down,
                TerminalPointerAction.Up => MouseEventType.Up,
                TerminalPointerAction.Drag => MouseEventType.Drag,
                TerminalPointerAction.WheelUp => MouseEventType.WheelUp,
                TerminalPointerAction.WheelDown => MouseEventType.WheelDown,
                _ => MouseEventType.Move
            };
            QueueInput(terminal.GenerateMouseEvent((XTerm.Input.MouseButton)button,
                Math.Clamp(column, 0, terminal.Cols - 1), Math.Clamp(row, 0, terminal.Rows - 1),
                engineAction, (KeyModifiers)modifiers));
        });
    }

    public void Scroll(int lines) => Mutate(terminal =>
    {
        terminal.ScrollLines(lines);
        if (terminal.Buffer.IsAtBottom) _hasNewOutputWhileScrolled = false;
    });
    /// <summary>Zero follows live output; positive values select rows above the live viewport.</summary>
    public void ScrollToOffset(int offset) => Mutate(terminal =>
    {
        if (terminal.IsAlternateBufferActive) return;
        terminal.Buffer.ScrollToLine(Math.Clamp(terminal.Buffer.YBase - Math.Max(0, offset), 0, terminal.Buffer.YBase));
        if (terminal.Buffer.IsAtBottom) _hasNewOutputWhileScrolled = false;
    });
    public void ScrollToBottom() => Mutate(terminal =>
    {
        terminal.ScrollToBottom();
        _hasNewOutputWhileScrolled = false;
    });
    public void SelectAll() => Mutate(terminal => terminal.Selection.SelectAll());
    public void ClearSelection() => Mutate(terminal => terminal.Selection.ClearSelection());
    public void BeginSelection(int column, int row, int clickCount = 1) => Mutate(terminal =>
        terminal.Selection.StartSelection(column, row,
            clickCount >= 3 ? XTerm.Selection.SelectionMode.Line :
            clickCount == 2 ? XTerm.Selection.SelectionMode.Word : XTerm.Selection.SelectionMode.Normal));
    public void UpdateSelection(int column, int row) => Mutate(terminal => terminal.Selection.UpdateSelection(column, row));
    public void EndSelection() => Mutate(terminal => terminal.Selection.EndSelection());

    public string GetSelectedText()
    {
        lock (_gate) return _disposed ? string.Empty : _terminal.Selection.GetSelectionText();
    }

    public string GetVisibleText()
    {
        lock (_gate)
        {
            if (_disposed) return string.Empty;
            var lines = new string[_terminal.Rows];
            for (var row = 0; row < lines.Length; row++)
                lines[row] = _terminal.Buffer.GetLine(_terminal.Buffer.YDisp + row)?.TranslateToString(trimRight: true) ?? string.Empty;
            return string.Join(Environment.NewLine, lines);
        }
    }

    public TerminalSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var terminal = _terminal;
            var buffer = terminal.Buffer;
            var colors = terminal.Colors.Take();
            var lines = new TerminalLine[terminal.Rows];
            var defaultForeground = Argb(colors.Foreground);
            var defaultBackground = Argb(colors.Background);
            var hasSelection = terminal.Selection.TryGetSelection(out var selection);
            for (var row = 0; row < lines.Length; row++)
            {
                var sourceLine = buffer.GetLine(buffer.YDisp + row);
                var cells = new TerminalCell[terminal.Cols];
                var selectionStart = 0;
                var selectionEnd = -1;
                if (hasSelection && !selection.TryGetRowSpan(buffer.YDisp + row, terminal.Cols, out selectionStart, out selectionEnd))
                    selectionEnd = -1;
                for (var column = 0; column < cells.Length; column++)
                {
                    var source = sourceLine is not null && column < sourceLine.Length ? sourceLine[column] : XTerm.Buffer.BufferCell.Space;
                    var attributes = source.Attributes;
                    var fgMode = attributes.GetFgColorMode();
                    var bgMode = attributes.GetBgColorMode();
                    var fg = attributes.GetFgColor();
                    var bg = attributes.GetBgColor();
                    // Bold brightening applies only to the original eight indexed ANSI colors.
                    if (attributes.IsBold() && fgMode == 0 && fg < 8 && terminal.Options.DrawBoldTextInBrightColors) fg += 8;
                    // 3.0.0 stores palette colors in mode 0 (256/257 are default-color
                    // sentinels) and RGB in mode 1. Its README's mode table differs from code.
                    var foreground = fgMode == 1 ? Argb(fg) : fg < 256 ? Argb(colors[Math.Max(fg, 0)]) : defaultForeground;
                    var background = bgMode == 1 ? Argb(bg) : bg < 256 ? Argb(colors[Math.Max(bg, 0)]) : defaultBackground;
                    if (attributes.IsInverse() ^ terminal.ReverseVideo) (foreground, background) = (background, foreground);
                    cells[column] = new TerminalCell(source.Content, source.Width, foreground, background,
                        attributes.IsBold(), attributes.IsDim(), attributes.IsItalic(), attributes.IsUnderline(),
                        attributes.IsStrikethrough(), attributes.IsOverline(), attributes.IsInvisible(),
                        column >= selectionStart && column <= selectionEnd);
                }
                lines[row] = new TerminalLine(Array.AsReadOnly(cells), sourceLine?.IsWrapped ?? false);
            }
            var shape = terminal.Options.CursorStyle.ToString() switch
            {
                "Underline" => TerminalCursorShape.Underline,
                "Bar" => TerminalCursorShape.Bar,
                _ => TerminalCursorShape.Block
            };
            return new TerminalSnapshot(terminal.Cols, terminal.Rows, Array.AsReadOnly(lines),
                buffer.X, buffer.Y + buffer.YBase - buffer.YDisp,
                terminal.CursorVisible && buffer.YDisp == buffer.YBase,
                terminal.Options.CursorBlink, shape, Argb(colors.Cursor), defaultBackground,
                terminal.IsAlternateBufferActive, buffer.YBase, buffer.YBase - buffer.YDisp,
                terminal.ApplicationCursorKeys, terminal.BracketedPasteMode,
                terminal.MouseTrackingMode != MouseTrackingMode.None, hasSelection, _outputVersion,
                !terminal.IsAlternateBufferActive && _hasNewOutputWhileScrolled);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _terminal.DataReceived -= OnDataReceived;
            _normalBuffer.Trimmed -= OnNormalBufferTrimmed;
            InputGenerated = null;
            Changed = null;
            try
            {
                // Upstream Dispose releases subscriptions, not screen contents. Clear both
                // screens and history before releasing our last reference to the engine.
                _terminal.Reset();
                _terminal.Options.Scrollback = 0;
            }
            finally
            {
                _terminal.Dispose();
                _terminal = null!; // Every entry point checks _disposed while holding _gate.
                _normalBuffer = null!;
                _alternateBuffer = null!;
                while (_pendingInput.TryDequeue(out var bytes)) Array.Clear(bytes);
            }
        }
    }

    private void Mutate(Action<XTerm.Terminal> action)
    {
        lock (_gate)
        {
            if (_disposed) return;
            action(_terminal);
        }
        Publish();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PrepareInput(XTerm.Terminal terminal)
    {
        terminal.ScrollToBottom();
        _hasNewOutputWhileScrolled = false;
        terminal.Selection.ClearSelection();
    }

    private void ResizeCore(XTerm.Terminal terminal, int columns, int rows)
    {
        if (terminal.Cols == columns && terminal.Rows == rows) return;
        var normal = _normalBuffer;
        var following = normal.IsAtBottom;
        var scrollOffset = normal.YBase - normal.YDisp;
        var historyLimit = terminal.Options.Scrollback;
        if (normal.Cols != columns) terminal.Selection.ClearSelection();

        // XTerm.NET 3.0.0's TerminalBuffer.Resize can leave YBase far above the live
        // screen when ReflowSmaller trims a full ring. It also trims history instead of
        // unused blank rows when shrinking the height. Use the pinned release's public
        // buffer/reflow APIs to avoid those paths; no terminal bytes are replayed.
        // https://github.com/tomlm/XTerm.NET/blob/3.0.0/src/XTerm.NET/Buffer/TerminalBuffer.cs
        if (rows < normal.Rows)
        {
            TrimUnusedTail(normal, rows);
            normal.Resize(normal.Cols, rows);
        }
        TrimUnusedTail(_alternateBuffer, rows);
        while (columns < normal.Cols)
        {
            // Upstream creates inserted lines at their OLD width. Halving at most per
            // stage bounds scratch cells to the current grid/history working set, even
            // for 500 -> 2 columns, instead of allocating hundreds of full-width rows
            // for each retained line. Only the final geometry is published to the host.
            var nextColumns = Math.Max(columns, (normal.Cols + 1) / 2);
            if (historyLimit > 0)
            {
                var insertions = CountReflowInsertions(normal, nextColumns);
                normal.SetScrollback(Math.Max(historyLimit, checked(normal.Lines.Length + insertions - normal.Rows)));
            }
            try { normal.Resize(nextColumns, rows); }
            finally { normal.SetScrollback(historyLimit); }
        }
        if (normal.Cols != columns || normal.Rows != rows) normal.Resize(columns, rows);
        terminal.Resize(columns, rows);
        if (following) normal.ScrollToBottom();
        else normal.ScrollToLine(Math.Max(0, normal.YBase - scrollOffset));
    }

    private static void TrimUnusedTail(XTerm.Buffer.TerminalBuffer buffer, int rows)
    {
        var removable = buffer.Rows - rows;
        while (removable > 0 && buffer.Lines.Length > buffer.YBase + buffer.Y + 1)
        {
            var last = buffer.GetLine(buffer.Lines.Length - 1);
            if (last is null || last.GetTrimmedLength() != 0 || last.HasImages || last.HasSizedRuns) break;
            buffer.Lines.Pop();
            removable--;
        }
    }

    private static int CountReflowInsertions(XTerm.Buffer.TerminalBuffer buffer, int newColumns)
    {
        var insertions = 0;
        var cursor = buffer.YBase + buffer.Y;
        for (var row = 0; row < buffer.Lines.Length;)
        {
            var start = row;
            var group = new List<XTerm.Buffer.BufferLine>();
            if (buffer.GetLine(row++) is { } first) group.Add(first);
            while (row < buffer.Lines.Length && buffer.GetLine(row) is { IsWrapped: true } continuation)
            {
                group.Add(continuation);
                row++;
            }
            if (group.Count == 0 || cursor >= start && cursor < row) continue;
            var lengths = XTerm.Buffer.BufferReflow.ReflowSmallerGetNewLineLengths(group, buffer.Cols, newColumns);
            insertions = checked(insertions + Math.Max(0, lengths.Length - group.Count));
        }
        return insertions;
    }

    private static void ValidateDimensions(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MaximumColumns);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, MaximumRows);
    }

    private void OnDataReceived(object? sender, XTerm.Events.TerminalEvents.DataEventArgs e) => QueueInput(e.Data);
    private void OnNormalBufferTrimmed(int count)
    {
        if (_trackOutputTrims) _outputTrimmedLines = checked(_outputTrimmedLines + count);
    }
    private void QueueInput(string text)
    {
        if (text.Length != 0) _pendingInput.Enqueue(Encoding.UTF8.GetBytes(text));
    }
    private void Publish()
    {
        // Reads can generate protocol replies at the same time as WPF generates keyboard
        // input. A single publisher preserves parser order without calling a host under _gate.
        while (true)
        {
            if (Interlocked.CompareExchange(ref _publishingInput, 1, 0) != 0) return;
            try
            {
                while (true)
                {
                    byte[] bytes;
                    lock (_gate)
                    {
                        if (_pendingInput.Count == 0) break;
                        bytes = _pendingInput.Dequeue();
                    }
                    try
                    {
                        // A transport clears its owned input after writing. Give every subscriber
                        // a separate allocation so that asynchronous cleanup cannot race another
                        // subscriber that is still observing this input event.
                        var handlers = InputGenerated?.GetInvocationList();
                        if (handlers is not null)
                            foreach (EventHandler<TerminalInputEventArgs> handler in handlers)
                                handler(this, new TerminalInputEventArgs(bytes.ToArray()));
                    }
                    finally { Array.Clear(bytes); }
                }
            }
            finally { Interlocked.Exchange(ref _publishingInput, 0); }
            // Cover an enqueue between the empty check and relinquishing publication.
            lock (_gate) { if (_pendingInput.Count == 0) return; }
        }
    }
    private static uint Argb(int rgb) => 0xFF000000u | ((uint)rgb & 0xFFFFFFu);
}
