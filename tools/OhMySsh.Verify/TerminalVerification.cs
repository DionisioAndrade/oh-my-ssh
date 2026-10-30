using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private static void TerminalFeed(TerminalEmulator emulator, string text) => emulator.Feed(Encoding.UTF8.GetBytes(text));
    private static string TerminalLineText(TerminalEmulator emulator, int row) => string.Concat(
        emulator.GetSnapshot().Lines[row].Cells.Where(cell => cell.Width != 0).Select(cell => cell.Text)).TrimEnd();

    private Task VerifyTerminalEmulatorAsync()
    {
        using var emulator = new TerminalEmulator(20, 4, 5);
        TerminalFeed(emulator, "first\r\nsecond\x1b[1;3H!\x1b[2;3H\x1b[K");
        Require(TerminalLineText(emulator, 0) == "fi!st" && TerminalLineText(emulator, 1) == "se",
            "VT cursor addressing or erase-to-line-end lost terminal state.");
        TerminalFeed(emulator, "\x1b[?1049hALT\x1b[?1049l");
        Require(!emulator.GetSnapshot().IsAlternateBuffer && TerminalLineText(emulator, 0) == "fi!st",
            "The alternate screen did not restore the normal buffer.");
        TerminalFeed(emulator, "\x1b[2J\x1b[H");
        foreach (var value in Encoding.UTF8.GetBytes("界e\u0301🙂")) emulator.Feed([value]);
        var cells = emulator.GetSnapshot().Lines[0].Cells;
        Require(cells[0].Text == "界" && cells[0].Width == 2 && cells[1].Width == 0
            && cells[2].Text == "e\u0301" && cells[3].Width == 2,
            "UTF-8 boundaries, wide cells, or combining characters were corrupted.");
        TerminalFeed(emulator, "\x1b[2J\x1b[H\x1b[31mR\x1b[38;5;196mI\x1b[38;2;12;34;56mT\x1b[48;2;65;43;21mB");
        cells = emulator.GetSnapshot().Lines[0].Cells;
        Require(cells[0].Foreground != cells[0].Background && cells[1].Foreground == 0xFFFF0000
            && cells[2].Foreground == 0xFF0C2238 && cells[3].Background == 0xFF412B15,
            "The native cell projection confused ANSI, 256-color, and true-color attributes.");
        TerminalFeed(emulator, "\x1b[0m\x1b[2J\x1b[HHEADER\x1b[4;1HFOOTER\x1b[2;3r\x1b[2;1Hone\r\ntwo\r\nthree");
        Require(TerminalLineText(emulator, 0) == "HEADER" && TerminalLineText(emulator, 3) == "FOOTER"
            && TerminalLineText(emulator, 1) == "two" && TerminalLineText(emulator, 2) == "three",
            "Scrolling within a region damaged fixed header/footer rows.");
        TerminalFeed(emulator, "\x1b[r\x1b[2J\x1b[H");
        for (var index = 0; index < 50; index++) TerminalFeed(emulator, $"line{index}\r\n");
        Require(emulator.GetSnapshot().ScrollbackCount == 5, "The scrollback capacity was not enforced.");
        emulator.Scroll(-4);
        Require(emulator.GetSnapshot().ViewportOffset == 4, "Scrollback navigation did not move the viewport.");
        emulator.Resize(25, 6);
        Require(emulator.GetSnapshot().Columns == 25 && emulator.GetSnapshot().Rows == 6, "Grid resizing failed.");

        using var inputEngine = new TerminalEmulator(20, 4);
        var input = new List<byte[]>();
        inputEngine.InputGenerated += (_, args) => input.Add(args.Data);
        foreach (var value in Encoding.ASCII.GetBytes("\x1b[?1h\x1b[?2004h")) inputEngine.Feed([value]);
        inputEngine.SendKey(TerminalKey.UpArrow);
        inputEngine.SendText("c", TerminalModifiers.Control);
        inputEngine.SendText("v", TerminalModifiers.Control);
        inputEngine.Paste("hello\nworld\x1b[31m");
        Require(Encoding.UTF8.GetString(input[0]) == "\x1bOA" && input[1].SequenceEqual(new byte[] { 3 })
            && input[2].SequenceEqual(new byte[] { 22 }), "Application cursor keys or remote Ctrl+C/Ctrl+V were changed.");
        Require(Encoding.UTF8.GetString(input[3]) == "\x1b[200~hello\rworld[31m\x1b[201~",
            "Bracketed paste did not normalize newlines and filter embedded escape controls.");
        input.Clear();
        TerminalFeed(inputEngine, "\x1b[2;3H\x1b[6n");
        Require(Encoding.UTF8.GetString(input.Single()) == "\x1b[2;3R", "A cursor-position report was not sent to the transport.");
        TerminalFeed(inputEngine, "\x1b[2J\x1b[H界test");
        inputEngine.BeginSelection(0, 0); inputEngine.UpdateSelection(5, 0); inputEngine.EndSelection();
        Require(inputEngine.GetSelectedText() == "界test", "Selection copy lost wide characters.");
        var windowCount = application.Windows.Count;
        var clipboardSequence = TerminalClipboardSequence();
        TerminalFeed(inputEngine, "\x1b]52;c;dGVzdA==\a\x1b]9;synthetic notice\a\x1b]8;;https://example.invalid\aLINK\x1b]8;;\a\x1b_Ga=q;i=1;AAAA\x1b\\");
        Require(inputEngine.GetVisibleText().Contains("LINK", StringComparison.Ordinal) && application.Windows.Count == windowCount
            && clipboardSequence == TerminalClipboardSequence(),
            "Unsupported host escape sequences disrupted text, changed the clipboard, or created a host window.");
        var detached = inputEngine.GetSnapshot();
        TerminalFeed(inputEngine, "\x1b[HCHANGED");
        Require(detached.Lines[0].Cells[0].Text == "界", "A detached viewport changed after subsequent input.");
        using var ownedInput = new TerminalEmulator(20, 4);
        byte[]? observed = null;
        ownedInput.InputGenerated += (_, args) => Array.Clear(args.Data);
        ownedInput.InputGenerated += (_, args) => observed = args.Data;
        ownedInput.SendText("owned");
        Require(observed is not null && Encoding.UTF8.GetString(observed) == "owned",
            "A transport clearing its input buffer corrupted another subscriber's owned event data.");
        return Task.CompletedTask;
    }

    private async Task VerifyTerminalVolumeAsync()
    {
        using var emulator = new TerminalEmulator(100, 30, 2000);
        var block = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(new string('x', 78) + "\r\n", 128)));
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(Task.Run(() =>
        {
            for (var index = 0; index < 1024; index++) emulator.Feed(block);
        }), Task.Run(() =>
        {
            for (var index = 0; index < 100; index++) _ = emulator.GetSnapshot();
        }));
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Require(emulator.GetSnapshot().ScrollbackCount == 2000, "Sustained output escaped the scrollback bound.");
        Require(after - before < 96L * 1024 * 1024, "Sustained output retained more than the bounded terminal working set.");
        Require(elapsed.Elapsed < TimeSpan.FromSeconds(30), "Ten MiB of terminal output blocked parsing for an excessive interval.");
        var snapshot = emulator.GetSnapshot();
        Require(snapshot.Lines.Count == 30 && snapshot.Lines.All(line => line.Cells.Count == 100),
            "Snapshots included history or unbounded grid data.");
        TerminalFeed(emulator, "\x1b[2J\x1b[Htest-user@example.invalid\r\n界 café 🙂\r\n$ ");
        emulator.BeginSelection(0, 0); emulator.UpdateSelection(8, 0); emulator.EndSelection();
        emulator.Resize(70, 9);
        snapshot = emulator.GetSnapshot();
        Require(emulator.GetVisibleText().Contains("test-user@example.invalid", StringComparison.Ordinal)
            && snapshot.CursorColumn == 2 && snapshot.CursorRow == 2 && snapshot.ScrollbackCount == 2000
            && snapshot.ViewportOffset == 0 && emulator.GetSelectedText().Length == 0,
            "Full-history narrowing lost the live prompt, cursor, history bound, or cleared-selection policy.");
        emulator.Resize(120, 20);
        snapshot = emulator.GetSnapshot();
        Require(emulator.GetVisibleText().Contains("test-user@example.invalid", StringComparison.Ordinal)
            && snapshot.CursorColumn == 2 && TerminalLineText(emulator, snapshot.CursorRow).StartsWith("$ ".TrimEnd(), StringComparison.Ordinal),
            "Growing after full-history narrowing lost the live cursor line.");
        emulator.Scroll(-20);
        emulator.Resize(80, 12);
        Require(emulator.GetSnapshot().ViewportOffset == 20, "Width reflow changed the user's distance from the live scrollback tail.");
        emulator.ScrollToBottom();
        TerminalFeed(emulator, "\x1b[?1049hALT");
        emulator.Resize(60, 10);
        Require(emulator.GetSnapshot().IsAlternateBuffer && emulator.GetVisibleText().Contains("ALT", StringComparison.Ordinal),
            "Shrinking an alternate screen trimmed its live content instead of unused blank rows.");
        emulator.Resize(50, 6);
        Require(emulator.GetVisibleText().Contains("ALT", StringComparison.Ordinal),
            "Repeated alternate-screen height shrink lost live content.");
        TerminalFeed(emulator, "\x1b[?1049l");
        Require(emulator.GetVisibleText().Contains("test-user@example.invalid", StringComparison.Ordinal),
            "Resizing an alternate screen corrupted the inactive normal history.");

        using var extreme = new TerminalEmulator(500, 24, 2000);
        var wideLine = Encoding.ASCII.GetBytes(new string('w', 498) + "\r\n");
        for (var index = 0; index < 2200; index++) extreme.Feed(wideLine);
        TerminalFeed(extreme, "\x1b[2J\x1b[HZ");
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var resizeTime = Stopwatch.StartNew();
        extreme.Resize(2, 9);
        Require(extreme.GetVisibleText().Contains('Z') && extreme.GetSnapshot().ScrollbackCount == 2000
            && GC.GetTotalAllocatedBytes(precise: true) - allocated < 256L * 1024 * 1024
            && resizeTime.Elapsed < TimeSpan.FromSeconds(10),
            "The 500-to-2-column resize lost the live screen or exceeded its staged scratch-memory bound.");
        extreme.Resize(70, 12);
        Require(extreme.GetVisibleText().Contains('Z'), "Growing after an extreme staged resize lost the live screen.");
    }

    private async Task VerifyTerminalSurfaceAsync()
    {
        await VerifyTerminalClipboardBoundaryAsync();
        var originalCulture = UiText.Current.CultureName;
        await using var model = new TerminalSessionViewModel();
        var shell = new QaTerminalShell();
        var service = new QaTerminalShellService { OpenHandler = (_, _) => Task.FromResult<IRemoteShell>(shell) };
        model.AttachSession(new QaRemoteConnectionSession(TerminalProfile()) { Shells = service });
        var view = new TerminalView { DataContext = model };
        var next = new Button { Content = "synthetic focus target", Height = 28 };
        var panel = new DockPanel();
        DockPanel.SetDock(next, Dock.Bottom); panel.Children.Add(next); panel.Children.Add(view);
        var window = new Window { Content = panel, Width = 800, Height = 420 };
        PrepareHiddenWindow(window);
        try
        {
            window.Show(); await SettleLayoutAsync(window);
            var surface = (TerminalSurface)view.FindName("TerminalDisplay");
            Require(surface.IsVisible && surface.Emulator is null && service.Requests.Count == 0,
                "A visible terminal opened a shell automatically or did not expose its native surface.");
            await model.OpenCommand.ExecuteAsync();
            await WaitUntilAsync(() => surface.Emulator is not null);
            Require(service.Requests.Single().Columns != 80 || service.Requests.Single().Rows != 24,
                "Visible cell dimensions were not reported before opening the PTY.");
            await shell.EmitAsync(Encoding.UTF8.GetBytes("\x1b[?1h\x1b[?2004h\x1b[38;2;120;190;255mtest-user@example.invalid\x1b[0m\r\n界 café 🙂\r\n$ "));
            await WaitUntilAsync(() => model.Emulator!.GetVisibleText().Contains("example.invalid", StringComparison.Ordinal));
            var collected = new List<byte[]>();
            model.Emulator!.InputGenerated += (_, args) => collected.Add(args.Data.ToArray());
            SendTerminalKey(surface, Key.Up);
            SendTerminalKey(surface, Key.Tab);
            SendTerminalKey(surface, Key.Escape);
            SendTerminalKey(surface, Key.F1);
            SendTerminalKey(surface, Key.C, ModifierKeys.Control);
            SendTerminalKey(surface, Key.V, ModifierKeys.Control);
            Require(collected.Count == 6 && Encoding.UTF8.GetString(collected[0]) == "\x1bOA"
                && Encoding.UTF8.GetString(collected[1]) == "\t" && collected[2].SequenceEqual(new byte[] { 27 })
                && Encoding.UTF8.GetString(collected[3]) == "\x1bOP"
                && collected[4].SequenceEqual(new byte[] { 3 }) && collected[5].SequenceEqual(new byte[] { 22 }),
                "Native routed keyboard input did not reach the remote protocol unchanged.");
            var count = collected.Count;
            SendTerminalKey(surface, Key.C, ModifierKeys.Control | ModifierKeys.Shift);
            Require(collected.Count == count, "Copying an empty selection emitted remote input.");
            SendTerminalKey(surface, Key.Tab, ModifierKeys.Control);
            Require(collected.Count == count, "Ctrl+Tab sent remote input instead of leaving terminal focus.");
            surface.SetCurrentValue(TerminalSurface.IsInputEnabledProperty, false);
            SendTerminalKey(surface, Key.Return, expectedHandled: false);
            Require(collected.Count == count, "An inactive terminal accepted native keyboard input.");
            BindingOperations.GetBindingExpression(surface, TerminalSurface.IsInputEnabledProperty)!.UpdateTarget();
            view.DataContext = null;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            SendTerminalKey(surface, Key.Return, expectedHandled: false);
            Require(collected.Count == count, "A replaced DataContext replayed input to its previous emulator.");
            view.DataContext = model;
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            await VerifyTerminalSurfaceThroughputAsync(surface, model.Emulator!);
            TerminalFeed(model.Emulator!, "\x1b[2J\x1b[H\x1b[38;2;120;190;255mtest-user@example.invalid\x1b[0m\r\n界 café 🙂\r\n$ ");
            foreach (var culture in UiText.SupportedCultureNames)
            {
                await LocalizationService.Current.SetLanguageAsync(culture);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.Current.Apply(theme);
                    window.Width = 600; window.Height = 320;
                    await SettleLayoutAsync(window);
                    await WaitUntilAsync(() => shell.Resizes.Any(size => size.Columns < service.Requests[0].Columns));
                    await Task.Delay(45);
                    RequireVisibleWithinWindow(window, surface, "Native terminal");
                    Require(AutomationProperties.GetName(surface) == UiText.Get("Terminal.Surface"),
                        "The terminal accessibility name did not follow the current language.");
                    Require(model.Emulator!.GetVisibleText().Contains("test-user@example.invalid", StringComparison.Ordinal),
                        "A native layout resize removed the live terminal prompt before capture.");
                    var labels = surface.ContextMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
                    Require(labels.SequenceEqual(new[] { UiText.Get("Terminal.Copy"), UiText.Get("Terminal.Paste"), UiText.Get("Terminal.SelectAll"), UiText.Get("Terminal.LeaveFocus") }),
                        "An existing terminal menu did not refresh its localized text.");
                    Capture(window, $"terminal-native-{culture}-{theme.ToString().ToLowerInvariant()}.png");
                }
            }
            var dimensions = 0;
            surface.DimensionsChanged += (_, _) => dimensions++;
            surface.Visibility = Visibility.Collapsed; window.Width += 60;
            await SettleLayoutAsync(window);
            Require(dimensions == 0, "An invisible terminal surface issued a PTY resize.");
        }
        finally
        {
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            await LocalizationService.Current.SetLanguageAsync(originalCulture);
        }
    }

    private async Task VerifyTerminalSurfaceThroughputAsync(TerminalSurface surface, TerminalEmulator emulator)
    {
        var heartbeats = 0;
        var receivedInput = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => heartbeats++;
        EventHandler<TerminalInputEventArgs> inputHandler = (_, args) =>
        {
            if (args.Data.AsSpan().SequenceEqual("\r"u8)) receivedInput++;
        };
        emulator.InputGenerated += inputHandler;
        var chunk = Enumerable.Range(0, 8192).Select(index => (byte)((index % 80) switch
            { 78 => '\r', 79 => '\n', _ => 'x' })).ToArray();
        try
        {
            timer.Start();
            var producer = Task.Run(async () =>
            {
                for (var index = 0; index < 256; index++)
                {
                    emulator.Feed(chunk);
                    if (index % 8 == 0) await Task.Delay(2);
                }
            });
            await WaitUntilAsync(() => heartbeats > 0);
            SendTerminalKey(surface, Key.Return);
            await producer;
            var response = Stopwatch.StartNew();
            await Dispatcher.Yield(DispatcherPriority.Input);
            Require(heartbeats >= 2 && receivedInput == 1 && response.Elapsed < TimeSpan.FromMilliseconds(500),
                $"Background terminal output blocked dispatcher progress or routed keyboard input (heartbeats={heartbeats}, input={receivedInput}, dispatchMs={response.ElapsedMilliseconds}).");
            await Task.Delay(45);
            foreach (var (field, capacity) in new[] { ("_glyphs", 2048), ("_brushes", 1024) })
            {
                var cache = (System.Collections.ICollection)typeof(TerminalSurface)
                    .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
                Require(cache.Count <= capacity, "The native terminal retained an unbounded rendering cache.");
            }
            Require(emulator.GetSnapshot().ScrollbackCount <= 2000,
                "Visible high-volume output exceeded the production scrollback limit.");
        }
        finally { timer.Stop(); emulator.InputGenerated -= inputHandler; }
    }

    private async Task VerifyTerminalClipboardBoundaryAsync()
    {
        using var first = new TerminalEmulator(20, 4);
        using var replacement = new TerminalEmulator(20, 4);
        TerminalSurface? surface = null;
        string? copied = null;
        var reads = 0;
        var paste = "paste\ncafé";
        var fail = false;
        var replaceDuringRead = false;
        var input = new List<byte[]>();
        first.InputGenerated += (_, args) => input.Add(args.Data);
        replacement.InputGenerated += (_, args) => input.Add(args.Data);
        var args = new object[]
        {
            (Action<string>)(text => copied = text),
            (Func<int, string?>)(limit =>
            {
                Require(limit == TerminalSurface.MaximumPasteBytes, "The native clipboard adapter did not enforce the paste budget.");
                reads++;
                if (fail) throw new ExternalException("Synthetic unavailable clipboard.");
                if (replaceDuringRead) surface!.Emulator = replacement;
                return paste;
            })
        };
        surface = (TerminalSurface)Activator.CreateInstance(typeof(TerminalSurface), BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args, culture: null)!;
        surface.Emulator = first;
        surface.IsInputEnabled = true;
        var errors = new List<string>();
        surface.OperationFailed += (_, error) => errors.Add(error.ResourceKey);
        var window = new Window { Content = surface, Width = 600, Height = 200 };
        PrepareHiddenWindow(window);
        try
        {
            window.Show(); await SettleLayoutAsync(window);
            TerminalFeed(first, "copy界");
            first.BeginSelection(0, 0); first.UpdateSelection(5, 0); first.EndSelection();
            SendTerminalKey(surface, Key.C, ModifierKeys.Control | ModifierKeys.Shift);
            Require(copied == "copy界" && input.Count == 0 && reads == 0,
                "Ctrl+Shift+C did not copy exactly the selected wide text through the clipboard boundary.");
            SendTerminalKey(surface, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
            Require(reads == 1 && Encoding.UTF8.GetString(input.Single()) == "paste\rcafé",
                "Ctrl+Shift+V did not use explicit plain-text paste.");
            TerminalFeed(first, "\x1b[?2004h");
            input.Clear(); paste = new string('p', TerminalSurface.MaximumPasteBytes);
            SendTerminalKey(surface, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
            Require(input.Single().Length == TerminalSurface.MaximumPasteBytes + 12,
                "The surface rejected a permitted maximum-size bracketed paste.");
            input.Clear(); paste += "x";
            SendTerminalKey(surface, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
            Require(input.Count == 0 && errors.LastOrDefault() == "Terminal.PasteLimit",
                "The surface sent an oversized clipboard entry instead of the localized paste-limit error.");
            fail = true;
            SendTerminalKey(surface, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
            Require(input.Count == 0 && errors.LastOrDefault() == "Terminal.ClipboardUnavailable",
                "An unavailable clipboard did not stay within the localized UI error boundary.");
            fail = false; paste = "new synthetic text";
            var before = reads;
            surface.IsInputEnabled = false;
            SendTerminalKey(surface, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
            Require(reads == before && input.Count == 0, "A closed terminal read or pasted clipboard text.");
            surface.IsInputEnabled = true; replaceDuringRead = true;
            SendTerminalKey(surface, Key.V, ModifierKeys.Control | ModifierKeys.Shift);
            Require(input.Count == 0 && replacement.GetVisibleText().Trim().Length == 0,
                "A clipboard result crossed a changed emulator/session boundary.");
        }
        finally { window.Close(); }
    }

    private async Task VerifyTerminalControllerAsync()
    {
        var logs = new ConcurrentQueue<string>();
        await using var model = new TerminalSessionViewModel((_, message) => logs.Enqueue(message));
        var firstShell = new QaTerminalShell();
        var service = new QaTerminalShellService { OpenHandler = (_, _) => Task.FromResult<IRemoteShell>(firstShell) };
        var session = new QaRemoteConnectionSession(TerminalProfile()) { Shells = service };
        model.AttachSession(session);
        Require(service.Requests.Count == 0 && model.OpenCommand.CanExecute(null), "Attaching a connection opened a shell automatically.");
        model.RequestResize(91, 31);
        await model.OpenCommand.ExecuteAsync();
        Require(model.IsOpen && service.Requests.Single() == new RemoteTerminalOptions(TerminalEmulator.TerminalType, 91, 31),
            "The initial PTY did not capture its terminal identity and visible dimensions.");
        var oldEmulator = model.Emulator!;
        await firstShell.EmitAsync(Encoding.UTF8.GetBytes("captured-session\r\n"));
        await WaitUntilAsync(() => oldEmulator.GetVisibleText().Contains("captured-session", StringComparison.Ordinal));
        oldEmulator.SendText("SYNTHETIC_INPUT_NEVER_LOG");
        await WaitUntilAsync(() => firstShell.Input.Count == 1);
        for (var index = 0; index < 40; index++) model.RequestResize(100 + index, 32 + index % 3);
        await WaitUntilAsync(() => firstShell.Resizes.Count > 0);
        Require(firstShell.Resizes.Last() == (139, 32) && firstShell.Resizes.Count <= 2,
            "Rapid layout changes did not coalesce into the final remote PTY size.");
        await model.CloseCommand.ExecuteAsync();
        Require(!model.IsOpen && model.Emulator is null && firstShell.DisposeCalls == 1 && session.IsConnected,
            "Closing a terminal retained output or disposed its parent SSH session.");
        var secondShell = new QaTerminalShell();
        service.OpenHandler = (_, _) => Task.FromResult<IRemoteShell>(secondShell);
        await model.OpenCommand.ExecuteAsync();
        Require(service.Requests.Count == 2 && secondShell.Input.Count == 0 && model.Emulator!.GetVisibleText().Trim().Length == 0,
            "Explicit retry replayed old input or output.");
        secondShell.Fail(new IOException("SYNTHETIC_PRIVATE_UPSTREAM_ERROR"));
        await WaitUntilAsync(() => !model.IsOpen && model.Emulator is null);
        await WaitUntilAsync(() => model.OpenCommand.CanExecute(null));
        Require(service.Requests.Count == 2, "A failed channel automatically reopened a shell.");
        Require(logs.All(message => !message.Contains("SYNTHETIC_", StringComparison.Ordinal)),
            "Terminal input or upstream failure details entered diagnostic logs.");

        var opening = new TaskCompletionSource<IRemoteShell>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OpenHandler = (_, _) => opening.Task;
        var pending = model.OpenCommand.ExecuteAsync();
        Require(model.IsOpening, "The delayed terminal open did not remain pending.");
        var replacementShell = new QaTerminalShell();
        var replacementService = new QaTerminalShellService { OpenHandler = (_, _) => Task.FromResult<IRemoteShell>(replacementShell) };
        var replacement = new QaRemoteConnectionSession(TerminalProfile() with { Host = "replacement.example.invalid" }) { Shells = replacementService };
        model.AttachSession(replacement);
        var lateShell = new QaTerminalShell();
        opening.SetResult(lateShell);
        await pending;
        await WaitUntilAsync(() => lateShell.DisposeCalls == 1 && model.OpenCommand.CanExecute(null));
        Require(!model.IsOpen && model.Emulator is null && replacementService.Requests.Count == 0
            && model.Endpoint.Contains("replacement.example.invalid", StringComparison.Ordinal),
            "A stale shell completion replaced the captured session or automatically opened the replacement.");
        await model.OpenCommand.ExecuteAsync();
        Require(replacementService.Requests.Count == 1 && replacementShell.Input.Count == 0,
            "The replacement session inherited pending terminal input.");
        await replacementShell.EmitAsync(Encoding.ASCII.GetBytes("\x1b[?2004h"));
        await WaitUntilAsync(() => model.Emulator!.GetSnapshot().BracketedPaste);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replacementShell.WriteGate = releaseWrite.Task;
        model.Emulator!.Paste(new string('p', TerminalSurface.MaximumPasteBytes));
        await WaitUntilAsync(() => replacementShell.WritesStarted == 1);
        Require(model.IsOpen, "A permitted maximum-size bracketed paste closed the channel while its writer was busy.");
        releaseWrite.SetResult();
        await WaitUntilAsync(() => replacementShell.Input.Count == 1);
        var maximumPaste = replacementShell.Input.Single();
        Require(model.IsOpen && maximumPaste.Length == TerminalSurface.MaximumPasteBytes + 12
            && maximumPaste.AsSpan(0, 6).SequenceEqual("\x1b[200~"u8)
            && maximumPaste.AsSpan(maximumPaste.Length - 6).SequenceEqual("\x1b[201~"u8),
            "The accepted maximum paste lost its bracket framing or exceeded the input budget.");
        model.Emulator.SendText(new string('x', 3 * 1024 * 1024));
        await WaitUntilAsync(() => !model.IsOpen && model.Emulator is null);
        await WaitUntilAsync(() => model.OpenCommand.CanExecute(null));
        Require(replacementShell.Input.Count == 1 && replacementShell.DisposeCalls == 1,
            "An oversized owned input buffer was sent or did not close the overloaded channel.");
        await model.DetachAsync();
        Require(replacementShell.DisposeCalls == 1 && model.Emulator is null && !model.OpenCommand.CanExecute(null),
            "Detaching the parent connection left a usable terminal or stale output.");
    }

    private static ConnectionProfile TerminalProfile() => new()
    {
        Name = "Synthetic terminal", Host = "terminal.example.invalid", Username = "test-user", Group = "Synthetic QA", Protocol = ConnectionProtocol.Ssh
    };

    private static void SendTerminalKey(TerminalSurface surface, Key key, ModifierKeys modifiers = ModifierKeys.None, bool expectedHandled = true)
    {
        var keyboard = new QaTerminalKeyboard(modifiers);
        var args = new KeyEventArgs(keyboard, PresentationSource.FromVisual(surface), Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = surface };
        surface.RaiseEvent(args);
        Require(args.Handled == expectedHandled, $"The terminal routed key {key} across the wrong input boundary.");
    }

    [DllImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static extern uint TerminalClipboardSequence();
}

/// <summary>Provides routed-event modifier state without injecting keys into the operating system.</summary>
internal sealed class QaTerminalKeyboard(ModifierKeys modifiers) : KeyboardDevice(InputManager.Current)
{
    protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
    {
        Key.LeftCtrl when modifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
        Key.LeftShift when modifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
        Key.LeftAlt when modifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
        _ => KeyStates.None
    };
}

internal sealed class QaTerminalShellService : IRemoteShellService
{
    public List<RemoteTerminalOptions> Requests { get; } = [];
    public Func<RemoteTerminalOptions, CancellationToken, Task<IRemoteShell>> OpenHandler { get; set; }
        = (_, _) => Task.FromResult<IRemoteShell>(new QaTerminalShell());
    public Task<IRemoteShell> OpenAsync(RemoteTerminalOptions options, CancellationToken cancellationToken = default)
    {
        Requests.Add(options);
        return OpenHandler(options, cancellationToken);
    }
}

internal sealed class QaTerminalShell : IRemoteShell
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte[]? _pending;
    private int _offset;
    private int _disposeCalls;
    private int _writesStarted;
    public ConcurrentQueue<byte[]> Input { get; } = new();
    public ConcurrentQueue<(int Columns, int Rows)> Resizes { get; } = new();
    public bool IsOpen => DisposeCalls == 0 && !_completion.Task.IsCompleted;
    public int DisposeCalls => Volatile.Read(ref _disposeCalls);
    public int WritesStarted => Volatile.Read(ref _writesStarted);
    public Task? WriteGate { get; set; }
    public Task Completion => _completion.Task;
    public ValueTask EmitAsync(byte[] bytes) => _output.Writer.WriteAsync(bytes);
    public void Fail(Exception failure)
    {
        _output.Writer.TryComplete(failure);
        _completion.TrySetException(failure);
        _ = _completion.Task.Exception;
    }
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending is null)
        {
            try { _pending = await _output.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) when (_completion.Task.IsCompletedSuccessfully) { return 0; }
            _offset = 0;
        }
        var count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        if (_offset == _pending.Length) _pending = null;
        return count;
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _writesStarted);
        if (WriteGate is { } gate) await gate.WaitAsync(cancellationToken);
        Input.Enqueue(buffer.ToArray());
    }
    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Resizes.Enqueue((columns, rows));
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        _completion.TrySetResult();
        _output.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
