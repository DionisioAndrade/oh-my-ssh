using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OhMySsh.Core.Localization;
using MediaColor = System.Windows.Media.Color;
using WpfKey = System.Windows.Input.Key;

namespace OhMySsh.Desktop.Terminal;

/// <summary>
/// Native WPF cell renderer and input adapter. A dispatcher timer samples one dirty flag,
/// keeping render work bounded at 30 frames per second without posting output chunks to WPF.
/// </summary>
public sealed class TerminalSurface : FrameworkElement
{
    public const int MaximumPasteBytes = 1024 * 1024;
    private const uint UnicodeTextClipboardFormat = 13;
    private readonly Action<string> _copyClipboard;
    private readonly Func<int, string?> _readClipboard;
    private readonly DispatcherTimer _renderTimer;
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private readonly Dictionary<GlyphKey, FormattedText> _glyphs = [];
    private TerminalSnapshot? _snapshot;
    private int _dirty = 1;
    private bool _blinkVisible = true;
    private long _lastBlink;
    private double _cellWidth = 8.5;
    private double _cellHeight = 19;
    private double _pixelsPerDip = 1;
    private int _reportedColumns;
    private int _reportedRows;
    private bool _selecting;
    private TerminalPointerButton _trackedButton = TerminalPointerButton.None;
    private Typeface _regular = new(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    public static readonly DependencyProperty EmulatorProperty = DependencyProperty.Register(
        nameof(Emulator), typeof(TerminalEmulator), typeof(TerminalSurface),
        new FrameworkPropertyMetadata(null, OnEmulatorChanged));

    public static readonly DependencyProperty IsInputEnabledProperty = DependencyProperty.Register(
        nameof(IsInputEnabled), typeof(bool), typeof(TerminalSurface),
        new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(TerminalSurface), new FrameworkPropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), OnFontChanged));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(TerminalSurface), new FrameworkPropertyMetadata(13d, OnFontChanged));

    public TerminalSurface() : this(Clipboard.SetText, ReadClipboardText)
    {
    }

    internal TerminalSurface(Action<string> copyClipboard, Func<int, string?> readClipboard)
    {
        _copyClipboard = copyClipboard ?? throw new ArgumentNullException(nameof(copyClipboard));
        _readClipboard = readClipboard ?? throw new ArgumentNullException(nameof(readClipboard));
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1d / 30d)
        };
        _renderTimer.Tick += OnRenderTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibleChanged;
        ContextMenu = CreateContextMenu();
    }

    public TerminalEmulator? Emulator
    {
        get => (TerminalEmulator?)GetValue(EmulatorProperty);
        set => SetValue(EmulatorProperty, value);
    }

    public bool IsInputEnabled
    {
        get => (bool)GetValue(IsInputEnabledProperty);
        set => SetValue(IsInputEnabledProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public event EventHandler<TerminalDimensionsEventArgs>? DimensionsChanged;
    public event EventHandler<TerminalSurfaceErrorEventArgs>? OperationFailed;

    protected override AutomationPeer OnCreateAutomationPeer() => new TerminalSurfaceAutomationPeer(this);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ReportDimensions();
        Interlocked.Exchange(ref _dirty, 1);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMetrics();
        ReportDimensions();
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var snapshot = _snapshot;
        context.DrawRectangle(Brush(snapshot?.Background ?? 0xFF202128), null, new Rect(RenderSize));
        if (snapshot is null) return;
        context.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        var rows = Math.Min(snapshot.Rows, (int)Math.Ceiling(ActualHeight / _cellHeight));
        for (var row = 0; row < rows; row++)
        {
            var cells = snapshot.Lines[row].Cells;
            var y = row * _cellHeight;
            // Consecutive backgrounds share a draw; text remains on exact cell boundaries.
            for (var column = 0; column < cells.Count;)
            {
                var start = column;
                var background = cells[column].Background;
                while (++column < cells.Count && cells[column].Background == background) { }
                if (background != snapshot.Background)
                    context.DrawRectangle(Brush(background), null,
                        new Rect(start * _cellWidth, y, (column - start) * _cellWidth, _cellHeight));
            }
            for (var column = 0; column < cells.Count; column++)
            {
                var cell = cells[column];
                if (cell.Selected)
                    context.DrawRectangle(Brush(0x885F69A2), null,
                        new Rect(column * _cellWidth, y, _cellWidth, _cellHeight));
                if (cell.Width == 0 || cell.Invisible) continue;
                var width = Math.Min(cell.Width, cells.Count - column) * _cellWidth;
                var bounds = new Rect(column * _cellWidth, y, width, _cellHeight);
                context.PushClip(new RectangleGeometry(bounds));
                if (!string.IsNullOrEmpty(cell.Text) && cell.Text != " ")
                    context.DrawText(Glyph(cell), new Point(bounds.X, y));
                var pen = new Pen(Brush(cell.Foreground), Math.Max(1 / _pixelsPerDip, FontSize / 14));
                if (cell.Underline) context.DrawLine(pen, new Point(bounds.Left, y + _cellHeight - 2), new Point(bounds.Right, y + _cellHeight - 2));
                if (cell.StrikeThrough) context.DrawLine(pen, new Point(bounds.Left, y + _cellHeight * .53), new Point(bounds.Right, y + _cellHeight * .53));
                if (cell.Overline) context.DrawLine(pen, new Point(bounds.Left, y + 1), new Point(bounds.Right, y + 1));
                context.Pop();
            }
        }
        DrawCursor(context, snapshot);
        context.Pop();
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (!IsInputEnabled || Emulator is null || string.IsNullOrEmpty(e.Text)) return;
        // TextComposition carries whole IME/Unicode strings. AltGr is text input, not Meta.
        var keyboard = e.Device as KeyboardDevice;
        var modifiers = Modifiers(keyboard?.Modifiers);
        if (keyboard?.IsKeyDown(WpfKey.RightAlt) == true) modifiers &= ~(TerminalModifiers.Control | TerminalModifiers.Alt);
        Emulator.SendText(e.Text, modifiers);
        ResetBlink();
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == WpfKey.System ? e.SystemKey : e.Key;
        var modifiers = Modifiers(e.KeyboardDevice.Modifiers);
        if (key == WpfKey.Tab && modifiers.HasFlag(TerminalModifiers.Control))
        {
            MoveFocus(new TraversalRequest(modifiers.HasFlag(TerminalModifiers.Shift)
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
            e.Handled = true;
            return;
        }
        if (key == WpfKey.Apps || (key == WpfKey.F10 && modifiers == TerminalModifiers.Shift))
        {
            OpenContextMenu();
            e.Handled = true;
            return;
        }
        if (Emulator is null) return;
        if (modifiers == (TerminalModifiers.Control | TerminalModifiers.Shift))
        {
            if (key == WpfKey.C) { CopySelection(); e.Handled = true; return; }
            if (key == WpfKey.V) { PasteClipboard(); e.Handled = true; return; }
        }
        if (modifiers == TerminalModifiers.Shift && key is WpfKey.PageUp or WpfKey.PageDown)
        {
            Emulator.Scroll((key == WpfKey.PageUp ? -1 : 1) * Math.Max(1, (_snapshot?.Rows ?? 24) - 1));
            e.Handled = true;
            return;
        }
        if (!IsInputEnabled) return;
        if (TryMapKey(key, modifiers, out var terminalKey))
        {
            Emulator.SendKey(terminalKey, modifiers);
            ResetBlink();
            e.Handled = true;
            return;
        }
        if (e.KeyboardDevice.IsKeyDown(WpfKey.RightAlt)) return;
        // WPF suppresses TextInput for Ctrl combinations and for Alt/system keys.
        if ((modifiers & TerminalModifiers.Control) != 0 && TryControlCharacter(key, modifiers, out var control))
        {
            Emulator.SendText(control.ToString(), modifiers);
            ResetBlink();
            e.Handled = true;
        }
        else if ((modifiers & TerminalModifiers.Alt) != 0 && TryPrintableKey(key, out var printable))
        {
            Emulator.SendText(printable, modifiers);
            ResetBlink();
            e.Handled = true;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (IsInputEnabled) Emulator?.SendFocus(true);
        ResetBlink();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (IsInputEnabled) Emulator?.SendFocus(false);
        Interlocked.Exchange(ref _dirty, 1);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var emulator = Emulator;
        if (emulator is null) return;
        var cell = CellAt(e.GetPosition(this));
        if (ForwardMouseToRemote())
        {
            _trackedButton = ToPointerButton(e.ChangedButton);
            emulator.SendPointer(_trackedButton, cell.Column, cell.Row, TerminalPointerAction.Down, Modifiers());
            CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            _selecting = true;
            emulator.BeginSelection(cell.Column, cell.Row, e.ClickCount);
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var emulator = Emulator;
        if (emulator is null) return;
        var position = e.GetPosition(this);
        var cell = CellAt(position);
        if (_selecting)
        {
            if (position.Y < 0) emulator.Scroll(-1);
            else if (position.Y > ActualHeight) emulator.Scroll(1);
            emulator.UpdateSelection(cell.Column, cell.Row);
            e.Handled = true;
        }
        else if (ForwardMouseToRemote())
        {
            emulator.SendPointer(_trackedButton, cell.Column, cell.Row,
                _trackedButton == TerminalPointerButton.None ? TerminalPointerAction.Move : TerminalPointerAction.Drag, Modifiers());
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var cell = CellAt(e.GetPosition(this));
        if (_selecting)
        {
            Emulator?.EndSelection();
            _selecting = false;
            e.Handled = true;
        }
        if (_trackedButton != TerminalPointerButton.None && IsInputEnabled)
        {
            Emulator?.SendPointer(ToPointerButton(e.ChangedButton), cell.Column, cell.Row, TerminalPointerAction.Up, Modifiers());
            _trackedButton = TerminalPointerButton.None;
            e.Handled = true;
        }
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _selecting = false;
        _trackedButton = TerminalPointerButton.None;
        Emulator?.EndSelection();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Emulator is null) return;
        var steps = Math.Max(1, Math.Abs(e.Delta) / 120);
        if (ForwardMouseToRemote())
        {
            var cell = CellAt(e.GetPosition(this));
            for (var step = 0; step < steps; step++)
                Emulator.SendPointer(e.Delta > 0 ? TerminalPointerButton.WheelUp : TerminalPointerButton.WheelDown,
                    cell.Column, cell.Row, e.Delta > 0 ? TerminalPointerAction.WheelUp : TerminalPointerAction.WheelDown, Modifiers());
        }
        else Emulator.Scroll((e.Delta > 0 ? -1 : 1) * steps * 3);
        e.Handled = true;
    }

    protected override void OnContextMenuOpening(ContextMenuEventArgs e)
    {
        base.OnContextMenuOpening(e);
        if (e.CursorLeft >= 0 && ForwardMouseToRemote()) e.Handled = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateMetrics();
        UpdateTimer();
        ReportDimensions();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _renderTimer.Stop();
        _selecting = false;
        _trackedButton = TerminalPointerButton.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateTimer();
        if (IsVisible)
        {
            _reportedColumns = _reportedRows = 0;
            ReportDimensions();
            Interlocked.Exchange(ref _dirty, 1);
        }
    }

    private void UpdateTimer()
    {
        if (IsLoaded && IsVisible && Emulator is not null) _renderTimer.Start();
        else _renderTimer.Stop();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        var dirty = Interlocked.Exchange(ref _dirty, 0) != 0;
        if (dirty)
        {
            try { _snapshot = Emulator?.GetSnapshot(); }
            catch (ObjectDisposedException) { _snapshot = null; }
        }
        var now = Environment.TickCount64;
        if (now - _lastBlink >= 530)
        {
            _lastBlink = now;
            _blinkVisible = !_blinkVisible;
            if (IsKeyboardFocused && _snapshot?.CursorBlink == true) dirty = true;
        }
        if (dirty) InvalidateVisual();
    }

    private static void OnEmulatorChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var surface = (TerminalSurface)target;
        if (e.OldValue is TerminalEmulator oldEmulator) oldEmulator.Changed -= surface.OnEmulatorChanged;
        if (e.NewValue is TerminalEmulator newEmulator) newEmulator.Changed += surface.OnEmulatorChanged;
        surface._snapshot = null;
        surface._glyphs.Clear();
        surface._brushes.Clear();
        surface._selecting = false;
        surface._trackedButton = TerminalPointerButton.None;
        if (surface.IsMouseCaptured) surface.ReleaseMouseCapture();
        surface._reportedColumns = surface._reportedRows = 0;
        Interlocked.Exchange(ref surface._dirty, 1);
        surface.UpdateTimer();
        surface.InvalidateVisual();
        surface.ReportDimensions();
    }

    private void OnEmulatorChanged(object? sender, EventArgs e) => Interlocked.Exchange(ref _dirty, 1);

    private static void OnFontChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var surface = (TerminalSurface)target;
        surface.UpdateMetrics();
        surface.ReportDimensions();
    }

    private void UpdateMetrics()
    {
        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _regular = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var metric = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _regular, FontSize, Brushes.White, _pixelsPerDip);
        _cellWidth = Math.Max(1, Math.Ceiling(metric.WidthIncludingTrailingWhitespace * _pixelsPerDip) / _pixelsPerDip);
        _cellHeight = Math.Max(1, Math.Ceiling(Math.Max(metric.Height, FontSize * 1.38) * _pixelsPerDip) / _pixelsPerDip);
        _glyphs.Clear();
        Interlocked.Exchange(ref _dirty, 1);
    }

    private void ReportDimensions()
    {
        if (!IsLoaded || !IsVisible || ActualWidth < _cellWidth * 2 || ActualHeight < _cellHeight * 2) return;
        var columns = Math.Clamp((int)Math.Floor(ActualWidth / _cellWidth), 2, TerminalEmulator.MaximumColumns);
        var rows = Math.Clamp((int)Math.Floor(ActualHeight / _cellHeight), 2, TerminalEmulator.MaximumRows);
        if (columns == _reportedColumns && rows == _reportedRows) return;
        _reportedColumns = columns;
        _reportedRows = rows;
        DimensionsChanged?.Invoke(this, new TerminalDimensionsEventArgs(columns, rows));
    }

    private void DrawCursor(DrawingContext context, TerminalSnapshot snapshot)
    {
        if (!snapshot.CursorVisible || snapshot.CursorColumn < 0 || snapshot.CursorColumn >= snapshot.Columns ||
            snapshot.CursorRow < 0 || snapshot.CursorRow >= snapshot.Rows ||
            (IsKeyboardFocused && snapshot.CursorBlink && !_blinkVisible)) return;
        var cell = snapshot.Lines[snapshot.CursorRow].Cells[snapshot.CursorColumn];
        var width = Math.Max(1, cell.Width) * _cellWidth;
        var rectangle = new Rect(snapshot.CursorColumn * _cellWidth, snapshot.CursorRow * _cellHeight, width, _cellHeight);
        var brush = Brush(snapshot.CursorColor);
        if (!IsKeyboardFocused)
            context.DrawRectangle(null, new Pen(brush, 1 / _pixelsPerDip), rectangle);
        else if (snapshot.CursorShape == TerminalCursorShape.Underline)
            context.DrawRectangle(brush, null, new Rect(rectangle.Left, rectangle.Bottom - 2, rectangle.Width, 2));
        else if (snapshot.CursorShape == TerminalCursorShape.Bar)
            context.DrawRectangle(brush, null, new Rect(rectangle.Left, rectangle.Top, 2, rectangle.Height));
        else
        {
            context.DrawRectangle(brush, null, rectangle);
            if (cell.Width > 0 && !cell.Invisible && !string.IsNullOrWhiteSpace(cell.Text))
            {
                context.PushClip(new RectangleGeometry(rectangle));
                context.DrawText(Glyph(cell with { Foreground = cell.Background }), rectangle.TopLeft);
                context.Pop();
            }
        }
    }

    private FormattedText Glyph(TerminalCell cell)
    {
        var color = cell.Dim ? Dim(cell.Foreground) : cell.Foreground;
        var key = new GlyphKey(cell.Text, color, cell.Bold, cell.Italic);
        if (_glyphs.TryGetValue(key, out var glyph)) return glyph;
        var typeface = cell.Bold || cell.Italic
            ? new Typeface(FontFamily, cell.Italic ? FontStyles.Italic : FontStyles.Normal,
                cell.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal)
            : _regular;
        glyph = new FormattedText(cell.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, FontSize, Brush(color), _pixelsPerDip);
        if (_glyphs.Count >= 2048) _glyphs.Clear();
        _glyphs[key] = glyph;
        return glyph;
    }

    private SolidColorBrush Brush(uint argb)
    {
        if (_brushes.TryGetValue(argb, out var brush)) return brush;
        brush = new SolidColorBrush(MediaColor.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        if (_brushes.Count >= 1024) _brushes.Clear();
        _brushes[argb] = brush;
        return brush;
    }

    private static uint Dim(uint color) => 0xFF000000u | ((color & 0xFEFEFEu) >> 1);

    private void ResetBlink()
    {
        _lastBlink = Environment.TickCount64;
        _blinkVisible = true;
        Interlocked.Exchange(ref _dirty, 1);
    }

    private (int Column, int Row) CellAt(Point position) => (
        Math.Clamp((int)Math.Floor(position.X / _cellWidth), 0, Math.Max(0, (_snapshot?.Columns ?? 80) - 1)),
        Math.Clamp((int)Math.Floor(position.Y / _cellHeight), 0, Math.Max(0, (_snapshot?.Rows ?? 24) - 1)));

    private bool ForwardMouseToRemote() => IsInputEnabled && Emulator?.IsMouseTracking == true &&
        !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    private static TerminalPointerButton ToPointerButton(System.Windows.Input.MouseButton button) => button switch
    {
        System.Windows.Input.MouseButton.Left => TerminalPointerButton.Left,
        System.Windows.Input.MouseButton.Middle => TerminalPointerButton.Middle,
        System.Windows.Input.MouseButton.Right => TerminalPointerButton.Right,
        _ => TerminalPointerButton.None
    };

    private static TerminalModifiers Modifiers(ModifierKeys? modifiers = null)
    {
        var source = modifiers ?? Keyboard.Modifiers;
        return (source.HasFlag(ModifierKeys.Shift) ? TerminalModifiers.Shift : TerminalModifiers.None) |
               (source.HasFlag(ModifierKeys.Alt) ? TerminalModifiers.Alt : TerminalModifiers.None) |
               (source.HasFlag(ModifierKeys.Control) ? TerminalModifiers.Control : TerminalModifiers.None);
    }

    private static bool TryMapKey(WpfKey key, TerminalModifiers modifiers, out TerminalKey result)
    {
        result = key switch
        {
            WpfKey.Return => TerminalKey.Enter,
            WpfKey.Tab => TerminalKey.Tab,
            WpfKey.Back => TerminalKey.Backspace,
            WpfKey.Escape => TerminalKey.Escape,
            WpfKey.Space when modifiers.HasFlag(TerminalModifiers.Control) => TerminalKey.Space,
            WpfKey.Up => TerminalKey.UpArrow,
            WpfKey.Down => TerminalKey.DownArrow,
            WpfKey.Right => TerminalKey.RightArrow,
            WpfKey.Left => TerminalKey.LeftArrow,
            WpfKey.Home => TerminalKey.Home,
            WpfKey.End => TerminalKey.End,
            WpfKey.PageUp => TerminalKey.PageUp,
            WpfKey.PageDown => TerminalKey.PageDown,
            WpfKey.Insert => TerminalKey.Insert,
            WpfKey.Delete => TerminalKey.Delete,
            WpfKey.Decimal => TerminalKey.KeypadDecimal,
            WpfKey.Divide => TerminalKey.KeypadDivide,
            WpfKey.Multiply => TerminalKey.KeypadMultiply,
            WpfKey.Subtract => TerminalKey.KeypadSubtract,
            WpfKey.Add => TerminalKey.KeypadAdd,
            _ => (TerminalKey)(-1)
        };
        if (key is >= WpfKey.F1 and <= WpfKey.F12) result = TerminalKey.F1 + (key - WpfKey.F1);
        else if (key is >= WpfKey.NumPad0 and <= WpfKey.NumPad9) result = TerminalKey.Keypad0 + (key - WpfKey.NumPad0);
        return (int)result >= 0;
    }

    private static bool TryControlCharacter(WpfKey key, TerminalModifiers modifiers, out char character)
    {
        character = key switch
        {
            >= WpfKey.A and <= WpfKey.Z => (char)('a' + (int)key - (int)WpfKey.A),
            WpfKey.OemOpenBrackets => '[',
            WpfKey.OemCloseBrackets => ']',
            WpfKey.OemPipe or WpfKey.OemBackslash => '\\',
            WpfKey.D2 => '@',
            WpfKey.D6 => '^',
            WpfKey.OemMinus => '_',
            WpfKey.OemQuestion when modifiers.HasFlag(TerminalModifiers.Shift) => '?',
            _ => '\0'
        };
        return character != '\0';
    }

    private static bool TryPrintableKey(WpfKey key, out string text)
    {
        // Translate with the active Windows keyboard layout while neutralizing Alt/Control,
        // so Meta uses the same printable character as ordinary text input.
        var state = new byte[256];
        if (!GetKeyboardState(state)) { text = string.Empty; return false; }
        state[0x11] = state[0xA2] = state[0xA3] = 0;
        state[0x12] = state[0xA4] = state[0xA5] = 0;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var characters = new char[8];
        var count = ToUnicode(virtualKey, MapVirtualKey(virtualKey, 0), state, characters, characters.Length, 4);
        text = count > 0 ? new string(characters, 0, Math.Min(count, characters.Length)) : string.Empty;
        return text.Length != 0;
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        var copy = MenuItem("Terminal.Copy", CopySelection);
        var paste = MenuItem("Terminal.Paste", PasteClipboard);
        var select = MenuItem("Terminal.SelectAll", () => Emulator?.SelectAll());
        var leave = MenuItem("Terminal.LeaveFocus", () => MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(select);
        menu.Items.Add(new Separator());
        menu.Items.Add(leave);
        menu.Opened += (_, _) =>
        {
            copy.IsEnabled = Emulator?.GetSelectedText().Length > 0;
            paste.IsEnabled = IsInputEnabled && Emulator is not null;
            select.IsEnabled = Emulator is not null;
        };
        return menu;
    }

    private static MenuItem MenuItem(string key, Action action)
    {
        var item = new MenuItem();
        item.SetBinding(HeaderedItemsControl.HeaderProperty, new Binding($"[{key}]") { Source = UiText.Current });
        item.Click += (_, _) => action();
        return item;
    }

    private void OpenContextMenu()
    {
        if (ContextMenu is null) return;
        ContextMenu.PlacementTarget = this;
        ContextMenu.IsOpen = true;
    }

    private void CopySelection()
    {
        var text = Emulator?.GetSelectedText();
        if (string.IsNullOrEmpty(text)) return;
        try { _copyClipboard(text); }
        catch (ExternalException) { ReportClipboardError("Terminal.ClipboardUnavailable"); }
    }

    private void PasteClipboard()
    {
        var emulator = Emulator;
        if (!IsInputEnabled || emulator is null) return;
        string? text;
        try { text = _readClipboard(MaximumPasteBytes); }
        catch (TerminalPasteLimitException) { ReportClipboardError("Terminal.PasteLimit"); return; }
        catch (ExternalException) { ReportClipboardError("Terminal.ClipboardUnavailable"); return; }
        if (text is null) return;
        if (Encoding.UTF8.GetByteCount(text) > MaximumPasteBytes)
        {
            ReportClipboardError("Terminal.PasteLimit");
            return;
        }
        if (!IsInputEnabled || !ReferenceEquals(emulator, Emulator)) return;
        emulator.Paste(text);
        ResetBlink();
    }

    private static string? ReadClipboardText(int maximumBytes)
    {
        if (!IsClipboardFormatAvailable(UnicodeTextClipboardFormat)) return null;
        if (!OpenClipboard(IntPtr.Zero))
            throw new ExternalException("The clipboard is currently unavailable.");
        string? text = null;
        try
        {
            var handle = GetClipboardData(UnicodeTextClipboardFormat);
            if (handle == IntPtr.Zero) throw new ExternalException("The clipboard text could not be read.");
            var size = GlobalSize(handle);
            // Inspect native memory before allocating a managed string. UTF-16 can take at
            // most twice this paste's byte budget, plus its terminator; UTF-8 is checked below.
            if (size > (nuint)((maximumBytes + 1) * sizeof(char))) throw new TerminalPasteLimitException();
            if (size == 0) return string.Empty;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new ExternalException("The clipboard text could not be locked.");
            try
            {
                text = Marshal.PtrToStringUni(pointer, checked((int)(size / sizeof(char)))) ?? string.Empty;
                var terminator = text.IndexOf('\0');
                if (terminator >= 0) text = text[..terminator];
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
        return text;
    }

    private void ReportClipboardError(string key) => OperationFailed?.Invoke(this, new TerminalSurfaceErrorEventArgs(key));

    private readonly record struct GlyphKey(string Text, uint Color, bool Bold, bool Italic);
    private sealed class TerminalPasteLimitException() : Exception("The clipboard text exceeds the paste limit.");

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] state);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicode(uint virtualKey, uint scanCode, byte[] state, [Out] char[] buffer, int capacity, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    private sealed class TerminalSurfaceAutomationPeer(TerminalSurface owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override string GetClassNameCore() => nameof(TerminalSurface);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => true;
        public string Value => owner.Emulator?.GetVisibleText() ?? string.Empty;
        public void SetValue(string value) => throw new InvalidOperationException("The terminal automation value is read-only.");
    }
}
