using System.Collections;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OhMySsh.Desktop.Terminal;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyTerminalRenderingPreferencesAsync()
    {
        var settings = TerminalPreferences.Default;
        using var emulator = new TerminalEmulator(40, 6, 2000);
        TerminalFeed(emulator, "preserved\r\n\x1b[31mR\x1b[38;2;12;34;56mT\x1b[0m\r\n$ ");
        var before = emulator.GetVisibleText();
        var input = new List<byte[]>();
        emulator.InputGenerated += (_, args) => input.Add(args.Data);
        var custom = TerminalPreferences.Preset("light") with
        {
            CursorStyle = TerminalCursorShape.Bar, CursorBlink = false,
            AnsiColors = Array.AsReadOnly(settings.AnsiColors.Select((color, index) => index == 1 ? "#123456" : color).ToArray())
        };
        emulator.ApplyPreferences(custom);
        var snapshot = emulator.GetSnapshot();
        Require(emulator.GetVisibleText() == before && input.Count == 0 && snapshot.Background == 0xFFFAFAFC
            && snapshot.CursorShape == TerminalCursorShape.Bar && !snapshot.CursorBlink
            && snapshot.Lines[1].Cells[0].Foreground == 0xFF123456 && snapshot.Lines[1].Cells[1].Foreground == 0xFF0C2238,
            "Applying terminal preferences reset contents, emitted remote input, or changed literal RGB colors.");

        for (var index = 0; index < 2200; index++) TerminalFeed(emulator, $"history{index}\r\n");
        emulator.ScrollToOffset(20);
        before = emulator.GetVisibleText();
        var version = emulator.GetSnapshot().OutputVersion;
        TerminalFeed(emulator, "new remote output\r\n");
        Require(emulator.GetVisibleText() == before && emulator.GetSnapshot().ViewportOffset == 21
            && emulator.GetSnapshot().HasNewOutputWhileScrolled && emulator.GetSnapshot().OutputVersion > version,
            "New output interrupted history browsing or failed to mark unread output.");
        emulator.ClearSelection();
        Require(emulator.GetSnapshot().HasNewOutputWhileScrolled, "A non-output operation cleared the unread history indicator.");
        emulator.ScrollToBottom();
        Require(emulator.GetSnapshot().ViewportOffset == 0 && !emulator.GetSnapshot().HasNewOutputWhileScrolled,
            "Following live output retained the history indicator.");
        emulator.ScrollToOffset(10);
        TerminalFeed(emulator, "another output\r\n");
        emulator.SendKey(TerminalKey.Enter);
        Require(emulator.GetSnapshot().ViewportOffset == 0 && !emulator.GetSnapshot().HasNewOutputWhileScrolled,
            "Explicit remote input did not return to live output.");
        emulator.ApplyPreferences(custom with { ScrollbackLines = 500 });
        Require(emulator.GetSnapshot().ScrollbackCount == 500 && emulator.GetVisibleText().Contains("another output", StringComparison.Ordinal),
            "Lowering the history limit escaped the bound or discarded the current screen.");
        TerminalFeed(emulator, "\x1b[?1049hALT");
        emulator.ScrollToOffset(500);
        Require(emulator.GetSnapshot().IsAlternateBuffer && emulator.GetSnapshot().ScrollbackCount == 0,
            "An alternate-screen application exposed normal-screen scrollback.");
        TerminalFeed(emulator, "\x1b[?1049l");

        // Resolve the actual bundled font, then compare native OpenType glyph substitutions.
        // Merely checking a ligature preference would not catch WPF silently falling back.
        var font = settings.CreateFontFamily();
        var typeface = new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Require(typeface.TryGetGlyphTypeface(out var bundled) && bundled.FontUri.Scheme == "pack"
            && bundled.FontUri.OriginalString.Contains("firacode-regular.ttf", StringComparison.OrdinalIgnoreCase),
            "The terminal did not resolve the bundled Fira Code font resource.");
        using var drawingEngine = new TerminalEmulator(40, 6);
        drawingEngine.ApplyPreferences(settings);
        var surface = new TerminalSurface { Emulator = drawingEngine, IsInputEnabled = true, FontFamily = font, FontSize = 16 };
        drawingEngine.ApplyPreferences(settings with { CursorStyle = TerminalCursorShape.Bar, CursorBlink = false });
        var previewCursor = typeof(TerminalSurface).GetMethod("DrawCursor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var outline = new DrawingVisual();
        using (var context = outline.RenderOpen()) previewCursor.Invoke(surface, [context, drawingEngine.GetSnapshot()]);
        surface.PreviewCursor = true;
        var configured = new DrawingVisual();
        using (var context = configured.RenderOpen()) previewCursor.Invoke(surface, [context, drawingEngine.GetSnapshot()]);
        Require(outline.Drawing.Children.OfType<GeometryDrawing>().Any(item => item.Pen is not null && item.Brush is null)
            && configured.Drawing.Children.OfType<GeometryDrawing>().Any(item => item.Pen is null && item.Brush is not null && item.Geometry.Bounds.Width == 2),
            "The read-only appearance preview did not display the configured cursor without keyboard focus.");
        surface.PreviewCursor = false;
        var cell = new TerminalCell("=", 1, 0xFFD1D5DF, 0xFF202128, false, false, false, false, false, false, false, false);
        var shape = typeof(TerminalSurface).GetMethod("Shape", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var text in new[] { "!=", "===", "=>", "->" })
        {
            surface.LigaturesEnabled = false;
            var plain = shape.Invoke(surface, [text, cell, text.Length * 10d])!;
            surface.LigaturesEnabled = true;
            var joined = shape.Invoke(surface, [text, cell, text.Length * 10d])!;
            var glyphs = joined.GetType().GetProperty("GlyphIndices")!;
            Require(!((IEnumerable<ushort>)glyphs.GetValue(plain)!).SequenceEqual((IEnumerable<ushort>)glyphs.GetValue(joined)!),
                $"The Fira Code operator {text} did not undergo native contextual glyph substitution.");
            var drawing = (DrawingGroup)joined.GetType().GetProperty("Drawing")!.GetValue(joined)!;
            var natural = (double)joined.GetType().GetProperty("NaturalWidth")!.GetValue(joined)!;
            Require(Math.Abs(drawing.Transform.Value.M11 * natural - text.Length * 10d) < .001,
                "A shaped operator changed the terminal's exact cell advances.");
        }

        var zoomSteps = new List<int>();
        var viewport = new List<TerminalViewportEventArgs>();
        surface.ZoomRequested += (_, args) => zoomSteps.Add(args.Steps);
        surface.ViewportChanged += (_, args) => viewport.Add(args);
        var window = new Window { Content = surface, Width = 600, Height = 320 };
        PrepareHiddenWindow(window);
        try
        {
            window.Show(); await SettleLayoutAsync(window);
            TerminalFeed(drawingEngine, "a != b => a === b\r\n界 café 🙂");
            await Task.Delay(60);
            Capture(window, "terminal-fira-code-ligatures.png");
            var cache = (IDictionary)typeof(TerminalSurface).GetField("_shaped", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)!;
            Require(cache.Keys.Cast<object>().Any(key => ShapedRunText(key).Contains("!=", StringComparison.Ordinal)),
                "The visible native renderer did not use its shaped multi-cell operator runs.");
            cache.Clear();
            TerminalFeed(drawingEngine, "\x1b[2J\x1b[H!\x1b[31m=\x1b[0m界e\u0301");
            await Task.Delay(60);
            Require(cache.Keys.Cast<object>().All(key => !ShapedRunText(key).Contains("!=", StringComparison.Ordinal)
                && !ShapedRunText(key).Contains('界') && !ShapedRunText(key).Contains("e\u0301", StringComparison.Ordinal)),
                "Native shaping crossed a style change, wide cell, or combining-cell boundary.");
            TerminalFeed(drawingEngine, "\x1b[2J\x1b[H!=");
            drawingEngine.BeginSelection(0, 0); drawingEngine.UpdateSelection(0, 0); drawingEngine.EndSelection();
            cache.Clear(); await Task.Delay(60);
            Require(cache.Keys.Cast<object>().All(key => !ShapedRunText(key).Contains("!=", StringComparison.Ordinal))
                && drawingEngine.GetSelectedText() == "!", "A ligature crossed the selection boundary or changed copied text.");

            var remoteInput = new List<byte[]>();
            drawingEngine.InputGenerated += (_, args) => remoteInput.Add(args.Data);
            TerminalFeed(drawingEngine, "\x1b[?1000h\x1b[?1006h");
            var wheel = typeof(TerminalSurface).GetMethod("HandleWheel", BindingFlags.NonPublic | BindingFlags.Instance)!;
            wheel.Invoke(surface, [120, ModifierKeys.Control, new Point(5, 5)]);
            wheel.Invoke(surface, [-240, ModifierKeys.Control, new Point(5, 5)]);
            Require(zoomSteps.SequenceEqual(new[] { 1, -2 }) && remoteInput.Count == 0,
                "Ctrl+wheel reached the remote mouse protocol instead of requesting local zoom.");

            drawingEngine.ClearSelection();
            TerminalFeed(drawingEngine, "\x1b[?1000l\x1b[?1006l");
            for (var index = 0; index < 30; index++) TerminalFeed(drawingEngine, $"line{index}\r\n");
            drawingEngine.ScrollToOffset(3);
            await WaitUntilAsync(() => viewport.LastOrDefault()?.ViewportOffset == 3);
            TerminalFeed(drawingEngine, "unread\r\n");
            await WaitUntilAsync(() => viewport.LastOrDefault()?.HasNewOutput == true);
            drawingEngine.ScrollToBottom();
            await WaitUntilAsync(() => viewport.LastOrDefault() is { ViewportOffset: 0, HasNewOutput: false });
            Require(cache.Count <= 512, "The terminal retained an unbounded shaped-run cache.");
            surface.Emulator = null;
            Require(cache.Count == 0 && viewport.LastOrDefault() is { ScrollbackCount: 0, HasNewOutput: false },
                "A detached terminal retained shaped output or stale viewport state.");
        }
        finally { window.Close(); }
    }

    private static string ShapedRunText(object key) => (string)key.GetType().GetProperty("Text")!.GetValue(key)!;
}
