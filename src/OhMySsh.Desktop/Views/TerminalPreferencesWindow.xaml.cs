using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop.Views;

public partial class TerminalPreferencesWindow : Window
{
    private readonly TerminalPreferencesViewModel _model;
    private readonly TerminalEmulator _preview = new(80, 8, 500);

    public TerminalPreferencesWindow(TerminalPreferencesService? service = null)
    {
        InitializeComponent();
        _model = new TerminalPreferencesViewModel(service ?? TerminalPreferencesService.Current);
        DataContext = _model;
        PreviewSurface.Emulator = _preview;
        _preview.Feed(Encoding.UTF8.GetBytes("\x1b[38;5;4mtest-user@example.invalid\x1b[0m ~\r\n"
            + "if (ready != null && count >= 2) => run();\r\n"
            + "\x1b[31mred  \x1b[32mgreen  \x1b[33myellow  \x1b[34mblue  \x1b[35mviolet  \x1b[36mcyan\x1b[0m\r\n"
            + "UTF-8: café 界 🙂  // -> === <= !=\r\n$ "));
        _model.PropertyChanged += PreferencesChanged;
        UiText.Current.LanguageChanged += LanguageChanged;
        Closed += (_, _) =>
        {
            _model.PropertyChanged -= PreferencesChanged;
            UiText.Current.LanguageChanged -= LanguageChanged;
            PreviewSurface.Emulator = null;
            _preview.Dispose();
        };
        ApplyPreview();
        LanguageChanged(this, EventArgs.Empty);
    }

    private void LanguageChanged(object? sender, EventArgs e) => Language = XmlLanguage.GetLanguage(UiText.Current.CultureName);
    private void PreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalPreferencesViewModel.PreviewPreferences)) ApplyPreview();
    }

    private void ApplyPreview()
    {
        var preferences = _model.PreviewPreferences;
        _preview.ApplyPreferences(preferences);
        PreviewSurface.FontFamily = preferences.CreateFontFamily();
        PreviewSurface.FontSize = preferences.FontSize;
        PreviewSurface.FontWeight = System.Windows.FontWeight.FromOpenTypeWeight(preferences.FontWeight);
        PreviewSurface.LigaturesEnabled = preferences.Ligatures;
        PreviewSurface.LineHeight = preferences.LineHeight;
        PreviewSurface.SelectionColor = TerminalPreferences.ToArgb(preferences.SelectionColor, preferences.SelectionOpacity);
        PreviewSurface.BackgroundColor = TerminalPreferences.ToArgb(preferences.Background);
        PreviewBorder.Background = new SolidColorBrush(Color.FromRgb((byte)(TerminalPreferences.ToArgb(preferences.Background) >> 16),
            (byte)(TerminalPreferences.ToArgb(preferences.Background) >> 8), (byte)TerminalPreferences.ToArgb(preferences.Background)));
        PreviewBorder.Padding = new Thickness(preferences.Padding);
    }

    private void Preview_DimensionsChanged(object? sender, TerminalDimensionsEventArgs e) => _preview.Resize(e.Columns, e.Rows);
    private void RestoreDefaults_Click(object sender, RoutedEventArgs e) => _model.RestoreDefaults();
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_model.IsSaving) Close(); }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await _model.SaveAsync()) { DialogResult = true; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = _model.IsSaving;

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TerminalColorField field) return;
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var color = TerminalPreferences.ToArgb(field.Color);
            dialog.Color = System.Drawing.Color.FromArgb((byte)(color >> 16), (byte)(color >> 8), (byte)color);
        }
        catch (ArgumentException) { }
        if (dialog.ShowDialog(new NativeOwner(new WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK)
            field.Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private sealed class NativeOwner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }
}
