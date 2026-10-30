namespace OhMySsh.Desktop.Terminal;

/// <summary>A detached viewport. No object here refers to the live parser or its mutable buffers.</summary>
public sealed record TerminalSnapshot(
    int Columns,
    int Rows,
    IReadOnlyList<TerminalLine> Lines,
    int CursorColumn,
    int CursorRow,
    bool CursorVisible,
    bool CursorBlink,
    TerminalCursorShape CursorShape,
    uint CursorColor,
    uint Background,
    bool IsAlternateBuffer,
    int ScrollbackCount,
    int ViewportOffset,
    bool ApplicationCursorKeys,
    bool BracketedPaste,
    bool MouseTracking,
    bool HasSelection,
    long OutputVersion = 0,
    bool HasNewOutputWhileScrolled = false);

public sealed record TerminalLine(IReadOnlyList<TerminalCell> Cells, bool IsWrapped);

/// <summary>Colors are opaque ARGB; Width zero is the continuation of a wide character.</summary>
public readonly record struct TerminalCell(
    string Text,
    int Width,
    uint Foreground,
    uint Background,
    bool Bold,
    bool Dim,
    bool Italic,
    bool Underline,
    bool StrikeThrough,
    bool Overline,
    bool Invisible,
    bool Selected);

public enum TerminalCursorShape { Block, Underline, Bar }

[Flags]
public enum TerminalModifiers { None = 0, Shift = 1, Alt = 2, Control = 4 }

public enum TerminalKey
{
    Enter, Tab, Backspace, Escape, Space,
    UpArrow, DownArrow, RightArrow, LeftArrow, Home, End, PageUp, PageDown, Insert, Delete,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
    KeypadDecimal, KeypadDivide, KeypadMultiply, KeypadSubtract, KeypadAdd, KeypadEnter
}

public enum TerminalPointerButton { None = -1, Left = 0, Middle = 1, Right = 2, WheelUp = 64, WheelDown = 65 }
public enum TerminalPointerAction { Down, Up, Move, Drag, WheelUp, WheelDown }

public sealed class TerminalInputEventArgs(byte[] data) : EventArgs
{
    /// <summary>An owned UTF-8 byte array that remains valid after the callback returns.</summary>
    public byte[] Data { get; } = data;
}

public sealed class TerminalDimensionsEventArgs(int columns, int rows) : EventArgs
{
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
}

public sealed class TerminalZoomEventArgs(int steps) : EventArgs
{
    public int Steps { get; } = steps;
}

public sealed class TerminalViewportEventArgs(int scrollbackCount, int viewportOffset, int rows,
    bool isAlternateBuffer, bool hasNewOutput) : EventArgs
{
    public int ScrollbackCount { get; } = scrollbackCount;
    public int ViewportOffset { get; } = viewportOffset;
    public int Rows { get; } = rows;
    public bool IsAlternateBuffer { get; } = isAlternateBuffer;
    public bool HasNewOutput { get; } = hasNewOutput;
}

public sealed class TerminalSurfaceErrorEventArgs(string resourceKey) : EventArgs
{
    public string ResourceKey { get; } = resourceKey;
    public string Message => OhMySsh.Core.Localization.UiText.Get(ResourceKey);
}
