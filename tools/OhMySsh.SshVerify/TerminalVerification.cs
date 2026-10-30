using System.Text;
using System.Threading.Channels;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.Terminal;

namespace OhMySsh.SshVerify;

internal sealed partial class SshVerificationSuite
{
    private async Task RunTerminalChecksAsync()
    {
        await CheckAsync("Terminal: PTY identity, fragmented UTF-8/ANSI bytes, raw input, and live resize", async () =>
        {
            await using var session = await Service("terminal-protocol").ConnectAsync(Profile);
            await using var shell = await RequireShells(session).OpenAsync(new("xterm-256color", 97, 31));
            await using var capture = new TerminalCapture(shell);
            await capture.UntilAsync("FIXTURE_READY xterm-256color 97 31\r\n");
            await SendAsync(shell, "fixture-fragments\r");
            await capture.UntilAsync("FRAGMENTS_DONE\r\n");
            Require(capture.Text.Contains("FRAGMENTS café 空 🌍\r\n", StringComparison.Ordinal)
                && capture.Text.Contains("\u001b[38;2;12;34;56mCOLOR\u001b[0m", StringComparison.Ordinal)
                && capture.Text.Contains("\u001b[?1049h\u001b[2J\u001b[HALTERNATE\u001b[?1049l", StringComparison.Ordinal),
                "The shell transport changed fragmented UTF-8, color, or alternate-screen bytes.");
            var input = Encoding.UTF8.GetBytes("café\u001b[A\u001b[3~\u0003\t");
            await shell.WriteAsync(input);
            await SendAsync(shell, "\r");
            await capture.UntilAsync($"INPUT {Convert.ToHexString(input).ToLowerInvariant()}\r\n");
            await shell.ResizeAsync(123, 42);
            await SendAsync(shell, "fixture-size\r");
            await capture.UntilAsync("SIZE 123 42\r\n");
            Require(session.IsConnected, "Resizing or typing ended the authenticated SSH session.");
        });
        await CheckAsync("Terminal: independent sibling channels, EOF, explicit reopen, and shared SFTP lifetime", async () =>
        {
            await using var session = await Service("terminal-lifetime").ConnectAsync(Profile);
            var shells = RequireShells(session);
            await using var first = await shells.OpenAsync(new());
            await using var sibling = await shells.OpenAsync(new("xterm-256color", 88, 28));
            await using var siblingCapture = new TerminalCapture(sibling);
            await siblingCapture.UntilAsync("FIXTURE_READY xterm-256color 88 28");
            await using (var capture = new TerminalCapture(first))
            {
                await capture.UntilAsync("FIXTURE_READY");
                await SendAsync(first, "first-only\r");
                await capture.UntilAsync("INPUT 66697273742d6f6e6c79\r\n");
                await SendAsync(sibling, "sibling-only\r");
                await siblingCapture.UntilAsync("INPUT 7369626c696e672d6f6e6c79\r\n");
                Require(!capture.Text.Contains("7369626c696e672d6f6e6c79", StringComparison.Ordinal)
                    && !siblingCapture.Text.Contains("66697273742d6f6e6c79", StringComparison.Ordinal),
                    "Input or output crossed a sibling shell channel boundary.");
                await first.ResizeAsync(101, 37);
                await SendAsync(first, "fixture-size\r");
                await capture.UntilAsync("SIZE 101 37\r\n");
                await SendAsync(sibling, "fixture-size\r");
                await siblingCapture.UntilAsync("SIZE 88 28\r\n");
                await SendAsync(first, "fixture-close\r");
                await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                await capture.Finished.WaitAsync(TimeSpan.FromSeconds(5));
                Require(capture.Text.Contains("SHELL_EXIT", StringComparison.Ordinal), "Normal shell exit lost its final output.");
            }
            Require(!first.IsOpen && session.IsConnected, "Shell EOF closed its parent session or reopened automatically.");
            Require(sibling.IsOpen, "Shell EOF closed an independent sibling channel.");
            await SendAsync(sibling, "after-sibling-exit\r");
            await siblingCapture.UntilAsync("INPUT 61667465722d7369626c696e672d65786974\r\n");
            await using (var second = await shells.OpenAsync(new("xterm-256color", 88, 28)))
            {
                await using var capture = new TerminalCapture(second);
                await capture.UntilAsync("FIXTURE_READY xterm-256color 88 28");
            }
            Require(await RequireFiles(session).GetHomeDirectoryAsync() == "/", "Closing a shell disabled SFTP.");
        });
        await CheckAsync("Terminal: channel limit, released slots, and parent disposal retire every sibling", async () =>
        {
            await using var session = await Service("terminal-channel-limit").ConnectAsync(Profile);
            var service = RequireShells(session);
            var channels = new List<IRemoteShell>();
            try
            {
                for (var index = 0; index < 8; index++) channels.Add(await service.OpenAsync(new()));
                await ExpectTerminalCodeAsync(() => service.OpenAsync(new()), RemoteTerminalErrorCode.AlreadyOpen);
                await channels[0].DisposeAsync();
                channels.Add(await service.OpenAsync(new()));
                Require(channels.Skip(1).All(channel => channel.IsOpen), "Reusing a disposed channel slot closed another terminal.");
                await session.DisposeAsync();
                Require(channels.All(channel => !channel.IsOpen), "Parent session disposal retained an interactive channel.");
                foreach (var channel in channels)
                {
                    try { await channel.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (RemoteTerminalException) { }
                }
            }
            finally { foreach (var channel in channels) await channel.DisposeAsync(); }
        });
        await CheckAsync("Terminal: bounded high-volume output drains without loss", async () =>
        {
            await using var session = await Service("terminal-burst").ConnectAsync(Profile);
            await using var shell = await RequireShells(session).OpenAsync(new());
            await using var capture = new TerminalCapture(shell);
            await capture.UntilAsync("FIXTURE_READY");
            await SendAsync(shell, "fixture-burst\r");
            await capture.UntilAsync("BURST_DONE\r\n");
            Require(capture.Text.Count(character => character == 'x') == 2 * 1024 * 1024 + 1,
                "A draining terminal lost bytes during high-volume output.");
        });
        await CheckAsync("Terminal: stalled readers receive a sanitized buffer-limit failure", async () =>
        {
            await using var session = await Service("terminal-overflow").ConnectAsync(Profile);
            await using var shell = await RequireShells(session).OpenAsync(new());
            await SendAsync(shell, "fixture-overflow\r");
            try { await shell.Completion.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (RemoteTerminalException exception) when (exception.Code == RemoteTerminalErrorCode.BufferLimitExceeded)
            {
                Require(!shell.IsOpen && session.IsConnected, "Terminal overflow disabled the parent SSH session.");
                return;
            }
            throw new InvalidOperationException("Stalled terminal output did not close with a buffer-limit error.");
        });
        await CheckAsync("Terminal: pending-read cancellation closes only its shell channel", async () =>
        {
            await using var session = await Service("terminal-cancel-read").ConnectAsync(Profile);
            await using var shell = await RequireShells(session).OpenAsync(new());
            var buffer = new byte[1024];
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try
            {
                while (await shell.ReadAsync(buffer, cancellation.Token) > 0) { }
            }
            catch (OperationCanceledException)
            {
                await shell.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Require(!shell.IsOpen && session.IsConnected, "Canceling a read retained its channel or disconnected SSH.");
                return;
            }
            throw new InvalidOperationException("A pending terminal read did not observe cancellation.");
        });
        await CheckAsync("Terminal: transport loss faults completion and allows a fresh connection", async () =>
        {
            await using var session = await Service("terminal-loss").ConnectAsync(Profile);
            await using var shell = await RequireShells(session).OpenAsync(new());
            await fixture.CommandAsync("drop");
            try { await shell.Completion.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (RemoteTerminalException exception) when (exception.Code == RemoteTerminalErrorCode.ConnectionLost)
            {
                Require(!shell.IsOpen && !session.IsConnected, "Transport loss retained an active terminal.");
                await using var retried = await Service("terminal-after-loss").ConnectAsync(Profile);
                await using var reopened = await RequireShells(retried).OpenAsync(new());
                return;
            }
            throw new InvalidOperationException("Transport loss did not fault terminal completion with a sanitized code.");
        });

        if (fixture.Info.RealTerminalPort == 0)
            Console.WriteLine("SKIP Terminal real programs: prepare the isolated MSYS2 runtime to verify Vim and tmux.");
        else
        {
            await CheckAsync("Terminal real programs: native emulator renders Bash/Vim, Unicode keys, and PTY resize", VerifyRealVimAsync);
            await CheckAsync("Terminal real programs: native emulator renders tmux alternate screen and live resize", VerifyRealTmuxAsync);
        }

        await CheckAsync("Terminal: fixture channels retire after shell disposal", async () =>
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var stats = await fixture.CommandAsync("stats");
                if (stats.GetProperty("TerminalSessions").GetInt32() == 0
                    && stats.GetProperty("RealTerminalSessions").GetInt32() == 0) return;
                await Task.Delay(50);
            }
            throw new InvalidOperationException("A terminal channel remained open after disposal.");
        });
    }

    private async Task VerifyRealVimAsync()
    {
        await using var session = await Service("terminal-real-vim").ConnectAsync(Profile with { Port = fixture.Info.RealTerminalPort });
        await using var shell = await RequireShells(session).OpenAsync(new("xterm-256color", 90, 30));
        using var emulator = new TerminalEmulator(90, 30);
        await using var capture = new TerminalCapture(shell, emulator, Path.Combine(output, "real-vim-terminal"));
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("fixture$", StringComparison.Ordinal));
        await capture.SendLineAsync("printf '\\nBASH_%s\\n' READY");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("BASH_READY", StringComparison.Ordinal));
        await capture.SendLineAsync("vim -Nu NONE -i NONE -n --noplugin -c 'set shortmess+=I' vim-fixture.txt");
        await capture.UntilRenderedAsync(() => emulator.GetSnapshot().IsAlternateBuffer
            && emulator.GetVisibleText().Contains("VIM_FIXTURE_INITIAL", StringComparison.Ordinal));
        emulator.Resize(112, 40);
        await shell.ResizeAsync(112, 40);
        await Task.Delay(250);
        await capture.SendKeyAsync(TerminalKey.Escape);
        await capture.SendLineAsync(":call writefile([printf('%d %d', &columns, &lines)], 'vim-size.txt')");
        var directory = Path.Combine(output, "fixture");
        await WaitForFileAsync(Path.Combine(directory, "vim-size.txt"));
        await capture.SendTextAsync("gg0iEdited café 🌍 ");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("Edited café 🌍 VIM_FIXTURE_INITIAL", StringComparison.Ordinal));
        await capture.SendKeyAsync(TerminalKey.LeftArrow);
        await capture.SendKeyAsync(TerminalKey.RightArrow);
        var snapshot = emulator.GetSnapshot();
        Require(snapshot.IsAlternateBuffer && snapshot.Columns == 112 && snapshot.Rows == 40
            && snapshot.Lines.SelectMany(line => line.Cells).Any(cell => cell.Text == "🌍" && cell.Width == 2),
            "The native emulator did not render Vim's resized alternate screen and wide Unicode cell.");
        await capture.SendKeyAsync(TerminalKey.Escape);
        await capture.SendControlAsync("l");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("Edited café 🌍 VIM_FIXTURE_INITIAL", StringComparison.Ordinal));
        await capture.SendLineAsync(":wq");
        await capture.UntilRenderedAsync(() => !emulator.GetSnapshot().IsAlternateBuffer
            && emulator.GetVisibleText().Contains("fixture$", StringComparison.Ordinal));
        await capture.UntilAsync("written");
        Require((await File.ReadAllTextAsync(Path.Combine(directory, "vim-fixture.txt"))).StartsWith("Edited café 🌍 VIM_FIXTURE_INITIAL", StringComparison.Ordinal),
            "Real Vim did not preserve typed Unicode text in the isolated fixture file.");
        Require((await File.ReadAllTextAsync(Path.Combine(directory, "vim-size.txt"))).Trim() == "112 40",
            "Real Vim did not receive the requested PTY dimensions after resize.");
    }

    private async Task VerifyRealTmuxAsync()
    {
        await using var session = await Service("terminal-real-tmux").ConnectAsync(Profile with { Port = fixture.Info.RealTerminalPort });
        await using var shell = await RequireShells(session).OpenAsync(new("xterm-256color", 96, 32));
        using var emulator = new TerminalEmulator(96, 32);
        await using var capture = new TerminalCapture(shell, emulator, Path.Combine(output, "real-tmux-terminal"));
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("fixture$", StringComparison.Ordinal));
        await capture.SendLineAsync("tmux -L \"$FIXTURE_TMUX_SOCKET\" -f /dev/null new-session -s fixture '/usr/bin/bash --noprofile --norc -i' \\; set-option -g status-right test-host");
        await capture.UntilRenderedAsync(() => emulator.GetSnapshot().IsAlternateBuffer
            && (emulator.GetVisibleText().Contains("bash-", StringComparison.Ordinal)
                || emulator.GetVisibleText().Contains("fixture$", StringComparison.Ordinal)));
        await capture.SendLineAsync("printf '\\nTMUX_%s\\n' READY");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("TMUX_READY", StringComparison.Ordinal));
        emulator.Resize(118, 44);
        await shell.ResizeAsync(118, 44);
        await Task.Delay(300);
        await capture.SendLineAsync("tmux -L \"$FIXTURE_TMUX_SOCKET\" display-message -p 'TMUX_SIZE #{client_width} #{client_height}'");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("TMUX_SIZE 118 44", StringComparison.Ordinal));
        var snapshot = emulator.GetSnapshot();
        Require(snapshot.IsAlternateBuffer && snapshot.Columns == 118 && snapshot.Rows == 44,
            "The native emulator did not retain tmux's resized alternate screen.");
        await capture.SendControlAsync("b");
        await capture.SendTextAsync("%");
        await Task.Delay(250);
        await capture.SendLineAsync("tmux display-message -p 'PANES #{window_panes} ACTIVE #{pane_index}'");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("PANES 2 ACTIVE 1", StringComparison.Ordinal));
        await capture.SendLineAsync("printf '\\nPANE_%s\\n' ONE");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("PANE_ONE", StringComparison.Ordinal));
        await capture.SendControlAsync("b");
        await capture.SendKeyAsync(TerminalKey.LeftArrow);
        await capture.SendLineAsync("tmux display-message -p 'PANE_INDEX #{pane_index}'");
        await capture.UntilRenderedAsync(() => emulator.GetVisibleText().Contains("PANE_INDEX 0", StringComparison.Ordinal));
        await capture.SendControlAsync("b");
        await capture.SendTextAsync("d");
        await capture.UntilRenderedAsync(() => !emulator.GetSnapshot().IsAlternateBuffer
            && emulator.GetVisibleText().Contains("fixture$", StringComparison.Ordinal));
        await capture.SendLineAsync("tmux -L \"$FIXTURE_TMUX_SOCKET\" attach-session -t fixture");
        await capture.UntilRenderedAsync(() => emulator.GetSnapshot().IsAlternateBuffer
            && emulator.GetVisibleText().Contains("PANE_INDEX 0", StringComparison.Ordinal)
            && emulator.GetVisibleText().Contains("PANE_ONE", StringComparison.Ordinal));
        await capture.SendLineAsync("tmux kill-session -t fixture");
        await capture.UntilRenderedAsync(() => !emulator.GetSnapshot().IsAlternateBuffer
            && emulator.GetVisibleText().Contains("fixture$", StringComparison.Ordinal));
        Require(session.IsConnected, "Exiting tmux disconnected the authenticated SSH session.");
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path)) return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("The real terminal program did not create its expected fixture output.");
    }

    private static IRemoteShellService RequireShells(IRemoteConnectionSession session) => session.Shells
        ?? throw new InvalidOperationException("The authenticated session does not expose interactive shells.");
    private static ValueTask SendAsync(IRemoteShell shell, string text) => shell.WriteAsync(Encoding.UTF8.GetBytes(text));
    private static async Task ExpectTerminalCodeAsync(Func<Task<IRemoteShell>> action, RemoteTerminalErrorCode code)
    {
        try { await using var unexpected = await action(); }
        catch (RemoteTerminalException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException($"Expected a sanitized {code} terminal error.");
    }
}

internal sealed class TerminalCapture : IAsyncDisposable
{
    private readonly IRemoteShell _shell;
    private readonly TerminalEmulator? _emulator;
    private readonly string? _diagnosticPath;
    private readonly Channel<InputBlock> _input = Channel.CreateBounded<InputBlock>(128);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly MemoryStream _output = new();
    private readonly object _gate = new();
    private readonly Task _reader;
    private readonly Task _writer;

    public TerminalCapture(IRemoteShell shell, TerminalEmulator? emulator = null, string? diagnosticPath = null)
    {
        _shell = shell;
        _emulator = emulator;
        _diagnosticPath = diagnosticPath;
        if (emulator is not null) emulator.InputGenerated += OnInputGenerated;
        _writer = WriteAsync();
        _reader = ReadAsync();
    }

    public Task Finished => _reader;
    public string Text { get { lock (_gate) return Encoding.UTF8.GetString(_output.GetBuffer(), 0, checked((int)_output.Length)); } }
    public async Task UntilAsync(string value)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (Text.Contains(value, StringComparison.Ordinal)) return;
            if (_reader.IsCompleted)
            {
                await _reader;
                throw new InvalidOperationException("Terminal output ended before its expected fixture marker.");
            }
            await Task.Delay(25);
        }
        throw new InvalidOperationException("Terminal output did not contain its expected fixture marker.");
    }

    public async Task UntilRenderedAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition()) return;
            if (_reader.IsCompleted) await _reader;
            if (_writer.IsFaulted) await _writer;
            await Task.Delay(25);
        }
        throw new InvalidOperationException("The native emulator did not render its expected real-program state.");
    }

    public async Task SendTextAsync(string text)
    {
        (_emulator ?? throw new InvalidOperationException("The fixture has no native emulator.")).SendText(text);
        await FlushInputAsync();
    }

    public async Task SendKeyAsync(TerminalKey key)
    {
        (_emulator ?? throw new InvalidOperationException("The fixture has no native emulator.")).SendKey(key);
        await FlushInputAsync();
    }

    public async Task SendControlAsync(string text)
    {
        (_emulator ?? throw new InvalidOperationException("The fixture has no native emulator.")).SendText(text, TerminalModifiers.Control);
        await FlushInputAsync();
    }

    public async Task SendLineAsync(string text)
    {
        await SendTextAsync(text);
        await SendKeyAsync(TerminalKey.Enter);
    }

    private async Task FlushInputAsync()
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _input.Writer.WriteAsync(new([], flushed), _cancellation.Token);
        await flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private void OnInputGenerated(object? sender, TerminalInputEventArgs args)
    {
        if (!_input.Writer.TryWrite(new(args.Data, null))) throw new InvalidOperationException("The fixture terminal input queue exceeded its bound.");
    }

    private async Task WriteAsync()
    {
        await foreach (var block in _input.Reader.ReadAllAsync(_cancellation.Token))
        {
            if (block.Bytes.Length > 0) await _shell.WriteAsync(block.Bytes, _cancellation.Token);
            block.Flushed?.TrySetResult();
        }
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[32768];
        while (true)
        {
            var count = await _shell.ReadAsync(buffer, _cancellation.Token);
            if (count == 0) return;
            lock (_gate)
            {
                if (_output.Length + count > 8 * 1024 * 1024) throw new InvalidOperationException("Fixture output exceeded its capture bound.");
                _output.Write(buffer, 0, count);
            }
            _emulator?.Feed(buffer.AsSpan(0, count));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_emulator is not null) _emulator.InputGenerated -= OnInputGenerated;
        _cancellation.Cancel();
        try { await _reader; }
        catch (OperationCanceledException) { }
        catch (RemoteTerminalException) { }
        try { await _writer; }
        catch (OperationCanceledException) { }
        catch (RemoteTerminalException) { }
        if (_diagnosticPath is not null)
        {
            await File.WriteAllBytesAsync(_diagnosticPath + ".bin", _output.ToArray());
            if (_emulator is not null) await File.WriteAllTextAsync(_diagnosticPath + ".txt", _emulator.GetVisibleText());
        }
        _cancellation.Dispose();
        _output.Dispose();
    }

    private readonly record struct InputBlock(byte[] Bytes, TaskCompletionSource? Flushed);
}

