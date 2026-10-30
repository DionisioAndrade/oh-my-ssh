using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyTerminalPreferencesPersistenceAsync()
    {
        var directory = Path.Combine(outputDirectory, "terminal-preferences");
        var path = Path.Combine(directory, "appearance.json");
        var service = new TerminalPreferencesService(path);
        var changed = 0;
        service.Changed += (_, _) =>
        {
            Require(application.Dispatcher.CheckAccess(), "Preference changes escaped the UI dispatcher.");
            changed++;
        };
        var colors = TerminalPreferences.Preset("light").AnsiColors.ToArray();
        var expectedColors = colors.ToArray();
        var custom = TerminalPreferences.Preset("light") with
        {
            FontSize = 17.5, FontWeight = 500, Ligatures = false, LineHeight = 1.6, Padding = 14,
            CursorStyle = TerminalCursorShape.Bar, CursorBlink = false, SelectionOpacity = .4,
            BoldBrightColors = false, ScrollbackLines = 4500, AnsiColors = colors
        };
        await service.SaveAsync(custom);
        colors[0] = "#ABCDEF";
        Require(service.Preferences.AnsiColors[0] != colors[0], "Saved preferences retained the caller's mutable palette.");
        var reloaded = new TerminalPreferencesService(path).Preferences;
        Require(reloaded.FontFamily == "Fira Code" && reloaded.FontSize == 17.5 && reloaded.FontWeight == 500
            && !reloaded.Ligatures && reloaded.LineHeight == 1.6 && reloaded.Padding == 14
            && reloaded.Background == custom.Background && reloaded.Foreground == custom.Foreground
            && reloaded.CursorColor == custom.CursorColor && reloaded.SelectionColor == custom.SelectionColor
            && reloaded.SelectionOpacity == .4 && reloaded.AnsiColors.SequenceEqual(expectedColors)
            && reloaded.CursorStyle == TerminalCursorShape.Bar && !reloaded.CursorBlink && !reloaded.BoldBrightColors && reloaded.ScrollbackLines == 4500,
            "Appearance persistence lost a font, color, cursor, spacing, or history field.");
        var pending = Enumerable.Range(0, 12).Select(_ => service.UpdateAsync(preferences => preferences with { FontSize = preferences.FontSize + .5 })).ToList();
        pending.Add(service.UpdateAsync(preferences => preferences with { Background = "#112233" }));
        await Task.WhenAll(pending);
        await service.FlushAsync();
        reloaded = new TerminalPreferencesService(path).Preferences;
        Require(reloaded.FontSize == 23.5 && reloaded.Background == "#112233" && reloaded.FontWeight == 500 && changed == 14,
            "Concurrent zoom updates lost each other or overwrote a newer palette.");
        var before = await File.ReadAllTextAsync(path);
        foreach (var invalid in new[]
        {
            service.Preferences with { FontSize = double.NaN }, service.Preferences with { FontSize = 33 },
            service.Preferences with { Foreground = "red" }, service.Preferences with { AnsiColors = [] },
            service.Preferences with { FontFamily = "https://example.invalid/font" },
            service.Preferences with { LineHeight = 3 }, service.Preferences with { ScrollbackLines = 10001 }
        })
        {
            try { await service.SaveAsync(invalid); throw new InvalidOperationException("Invalid preferences were accepted."); }
            catch (ArgumentException) { }
        }
        Require(await File.ReadAllTextAsync(path) == before && service.Preferences.FontSize == 23.5,
            "A rejected preference changed the live model or persisted file.");
        Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Preference saves left partial files behind.");
        using (var document = JsonDocument.Parse(before))
        {
            var allowed = new[] { "fontFamily", "fontSize", "fontWeight", "ligatures", "lineHeight", "padding", "foreground", "background",
                "cursorColor", "selectionColor", "selectionOpacity", "ansiColors", "cursorStyle", "cursorBlink", "boldBrightColors", "scrollbackLines" };
            Require(document.RootElement.EnumerateObject().Select(property => property.Name).Order().SequenceEqual(allowed.Order()),
                "The appearance file included data outside the documented preference fields.");
        }
        await File.WriteAllTextAsync(path, "{\"fontSize\":-1}");
        var invalidLoad = new TerminalPreferencesService(path);
        Require(invalidLoad.Preferences.FontSize == TerminalPreferences.DefaultFontSize && await File.ReadAllTextAsync(path) == "{\"fontSize\":-1}",
            "Invalid stored appearance blocked defaults or was rewritten without an explicit save.");
        await File.WriteAllTextAsync(path, "not json");
        Require(new TerminalPreferencesService(path).Preferences.FontFamily == "Fira Code", "Corrupt appearance prevented safe defaults.");
        var blocked = new TerminalPreferencesService(directory);
        try { await blocked.SaveAsync(custom); throw new InvalidOperationException("A directory was accepted as a settings file."); }
        catch (IOException) { }
        Require(blocked.Preferences.FontSize == TerminalPreferences.DefaultFontSize && !Directory.EnumerateFiles(directory, "*.tmp").Any()
            && !Directory.EnumerateFiles(outputDirectory, "*.tmp").Any(),
            "A failed preference write changed live settings or left a partial file.");
    }

    private async Task VerifyTerminalPreferencesEditorAsync()
    {
        var originalCulture = UiText.Current.CultureName;
        var service = new TerminalPreferencesService(Path.Combine(outputDirectory, "terminal-editor", "appearance.json"));
        await service.SaveAsync(TerminalPreferences.Default);
        var persisted = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "terminal-editor", "appearance.json"));
        var model = new TerminalPreferencesViewModel(service) { FontSize = 21, FontWeight = 700 };
        model.BackgroundField.Color = "#102030";
        Require(service.Preferences.FontSize == 13 && service.Preferences.Background != "#102030",
            "Editing an appearance draft changed active preferences before saving.");
        model.PalettePreset = "light";
        Require(model.FontSize == 21 && model.FontWeight == 700 && model.PreviewPreferences.Background == TerminalPreferences.Preset("light").Background,
            "A color preset replaced unrelated font preferences.");
        model.ForegroundField.Color = "bad";
        Require(!model.CanSave && model.HasError, "An invalid draft could be saved without a visible validation error.");
        model.RestoreDefaults();
        Require(model.CanSave && !model.HasError && model.FontSize == 13 && service.Preferences.FontSize == 13,
            "Restore defaults could not recover an invalid private draft.");
        var typeface = new Typeface(TerminalPreferences.Default.CreateFontFamily(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Require(typeface.TryGetGlyphTypeface(out var glyphs) && glyphs.FontUri.ToString().Contains("/Assets/Fonts/", StringComparison.OrdinalIgnoreCase)
            && glyphs.FamilyNames.Values.Any(name => name.Equals("Fira Code", StringComparison.OrdinalIgnoreCase)),
            "The terminal font fell back to a system font instead of its embedded Fira Code resource.");
        var window = new TerminalPreferencesWindow(service) { Width = 760, Height = 650 };
        PrepareHiddenWindow(window);
        try
        {
            window.Show();
            var tabs = (TabControl)window.FindName("PreferencesTabs");
            foreach (var culture in UiText.SupportedCultureNames)
            {
                await LocalizationService.Current.SetLanguageAsync(culture);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.Current.Apply(theme);
                    for (var tab = 0; tab < tabs.Items.Count; tab++)
                    {
                        tabs.SelectedIndex = tab;
                        await SettleLayoutAsync(window);
                        var surface = (TerminalSurface)window.FindName("PreviewSurface");
                        var engine = surface.Emulator ?? throw new InvalidOperationException("The appearance preview lost its emulator.");
                        var screen = engine.GetSnapshot();
                        Require(engine.GetVisibleText().Contains("test-user@example.invalid", StringComparison.Ordinal),
                            $"The appearance preview lost terminal state at {culture}/{theme}/{tab}: {screen.Columns}x{screen.Rows}, cursor {screen.CursorColumn},{screen.CursorRow}, history {screen.ScrollbackCount}, offset {screen.ViewportOffset}.");
                        // Native output is sampled at 30 fps. Layout settling alone does not wait
                        // for the next surface frame after a native theme/style reload.
                        await Task.Delay(45);
                        await SettleLayoutAsync(window);
                        var displayed = (TerminalSnapshot?)typeof(TerminalSurface).GetField("_snapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(surface);
                        Require(displayed is not null && string.Concat(displayed.Lines[0].Cells.Select(cell => cell.Text)).Contains("test-user", StringComparison.Ordinal),
                            $"The appearance preview did not render its intact terminal state at {culture}/{theme}/{tab}.");
                        Require(window.Title == UiText.Get("TerminalPreferences.Title"), "The open appearance window did not update its language.");
                        RequireVisibleWithinWindow(window, (FrameworkElement)window.FindName("PreviewSurface"), "Appearance preview");
                        RequireVisibleWithinWindow(window, (FrameworkElement)window.FindName("SaveTerminalPreferencesButton"), "Save appearance action");
                        var filename = $"terminal-preferences-{culture}-{theme.ToString().ToLowerInvariant()}-{tab}.png";
                        Capture(window, filename);
                        RequirePreferencePreviewPixels(window, surface, Path.Combine(outputDirectory, filename));
                    }
                }
            }
        }
        finally
        {
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            await LocalizationService.Current.SetLanguageAsync(originalCulture);
        }
        Require(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "terminal-editor", "appearance.json")) == persisted,
            "Closing the appearance window saved an unconfirmed draft.");
        model.FontSize = 18;
        Require(await model.SaveAsync() && new TerminalPreferencesService(Path.Combine(outputDirectory, "terminal-editor", "appearance.json")).Preferences.FontSize == 18,
            "Explicit appearance save did not persist the editor draft.");
    }

    private static void RequirePreferencePreviewPixels(Window window, TerminalSurface surface, string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var bounds = surface.TransformToAncestor(window).TransformBounds(new Rect(surface.RenderSize));
        var glyphPixels = 0;
        for (var y = Math.Max(0, (int)bounds.Top); y < Math.Min(bitmap.PixelHeight, bounds.Top + Math.Min(110, bounds.Height)); y++)
            for (var x = Math.Max(0, (int)bounds.Left); x < Math.Min(bitmap.PixelWidth, bounds.Right); x++)
            {
                var index = (y * bitmap.PixelWidth + x) * 4;
                // The isolated editor starts with its dark preview (#202128). Inspect the
                // actual encoded image rather than only the model or retained snapshot.
                if (pixels[index + 3] > 200 && (Math.Abs(pixels[index] - 40) > 70
                    || Math.Abs(pixels[index + 1] - 33) > 70 || Math.Abs(pixels[index + 2] - 32) > 70)) glyphPixels++;
            }
        Require(glyphPixels > 300, $"The encoded appearance preview was blank ({glyphPixels} contrasting pixels): {Path.GetFileName(path)}.");
    }
}
