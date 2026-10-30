using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop.Views;

public partial class TerminalView : UserControl
{
    private bool _focusAfterOpen;
    private bool _updatingScrollBar;
    private string? _noticeKey;
    private TerminalSessionViewModel? _model;

    public TerminalView()
    {
        InitializeComponent();
        Loaded += View_Loaded;
        Unloaded += View_Unloaded;
        IsVisibleChanged += (_, _) => { if (!IsVisible) _focusAfterOpen = false; };
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe(DataContext as TerminalSessionViewModel);
        TerminalPreferencesService.Current.Changed += Preferences_Changed;
        UiText.Current.LanguageChanged += Language_Changed;
        ApplyPreferences();
    }

    private void View_Unloaded(object sender, RoutedEventArgs e)
    {
        Subscribe(null);
        _focusAfterOpen = false;
        TerminalPreferencesService.Current.Changed -= Preferences_Changed;
        UiText.Current.LanguageChanged -= Language_Changed;
    }

    private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _focusAfterOpen = false;
        if (IsLoaded) Subscribe(e.NewValue as TerminalSessionViewModel);
        if (OperationNotice is not null) OperationNotice.Visibility = Visibility.Collapsed;
        ResetScrollState();
    }

    private void Subscribe(TerminalSessionViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null) _model.PropertyChanged -= Model_PropertyChanged;
        _model = model;
        if (_model is not null) _model.PropertyChanged += Model_PropertyChanged;
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) => _focusAfterOpen = true;

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalSessionViewModel.Emulator)) ResetScrollState();
        if (!_focusAfterOpen || _model?.IsOpen != true || !IsVisible) return;
        _focusAfterOpen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible && _model?.IsOpen == true) TerminalDisplay.Focus();
        }));
    }

    private void Terminal_DimensionsChanged(object? sender, TerminalDimensionsEventArgs e)
        => (DataContext as TerminalSessionViewModel)?.RequestResize(e.Columns, e.Rows);

    private void Preferences_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => { if (IsLoaded) ApplyPreferences(); })); return; }
        ApplyPreferences();
    }

    private void Language_Changed(object? sender, EventArgs e)
    {
        ApplyPreferences();
        if (_noticeKey is not null) OperationNoticeText.Text = UiText.Get(_noticeKey);
    }

    private void ApplyPreferences()
    {
        var preferences = TerminalPreferencesService.Current.Preferences;
        TerminalDisplay.FontFamily = preferences.CreateFontFamily();
        TerminalDisplay.FontSize = preferences.FontSize;
        TerminalDisplay.FontWeight = FontWeight.FromOpenTypeWeight(preferences.FontWeight);
        TerminalDisplay.LigaturesEnabled = preferences.Ligatures;
        TerminalDisplay.LineHeight = preferences.LineHeight;
        TerminalDisplay.SelectionColor = TerminalPreferences.ToArgb(preferences.SelectionColor, preferences.SelectionOpacity);
        TerminalDisplay.BackgroundColor = TerminalPreferences.ToArgb(preferences.Background);
        ViewportBorder.Padding = new Thickness(preferences.Padding);
        ViewportBorder.Background = ColorBrush(preferences.Background);
        OpenHintText.Foreground = ColorBrush(preferences.Foreground);
        TerminalScrollBar.Foreground = ColorBrush(preferences.Foreground);
        ZoomLevelText.Text = preferences.FontSize.ToString("0.#", UiText.Current.Culture);
        ZoomOutButton.IsEnabled = preferences.FontSize > TerminalPreferences.MinimumFontSize;
        ZoomInButton.IsEnabled = preferences.FontSize < TerminalPreferences.MaximumFontSize;
        ResetZoomButton.IsEnabled = preferences.FontSize != TerminalPreferences.DefaultFontSize;
    }

    private static SolidColorBrush ColorBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private async void ZoomIn_Click(object sender, RoutedEventArgs e) => await ChangeZoomAsync(1);
    private async void ZoomOut_Click(object sender, RoutedEventArgs e) => await ChangeZoomAsync(-1);
    private async void ResetZoom_Click(object sender, RoutedEventArgs e) => await ChangeZoomAsync(0, reset: true);
    private async void Terminal_ZoomRequested(object? sender, TerminalZoomEventArgs e)
        => await ChangeZoomAsync(Math.Clamp(e.Steps, -8, 8));

    private async Task ChangeZoomAsync(int steps, bool reset = false)
    {
        try
        {
            await TerminalPreferencesService.Current.UpdateAsync(current => current with
            {
                FontSize = reset ? TerminalPreferences.DefaultFontSize
                    : Math.Clamp(current.FontSize + steps, TerminalPreferences.MinimumFontSize, TerminalPreferences.MaximumFontSize)
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ShowNotice("TerminalControls.SaveError");
            ApplicationLog.WriteFailure("Saving terminal zoom", exception);
        }
    }

    private void CustomizeTerminal_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is null) return;
        new TerminalPreferencesWindow { Owner = owner }.ShowDialog();
    }

    private void Terminal_ViewportChanged(object? sender, TerminalViewportEventArgs e)
    {
        if (TerminalDisplay.Emulator is null) { ResetScrollState(); return; }
        _updatingScrollBar = true;
        try
        {
            TerminalScrollBar.Maximum = e.ScrollbackCount;
            TerminalScrollBar.ViewportSize = e.Rows;
            TerminalScrollBar.LargeChange = Math.Max(1, e.Rows - 1);
            TerminalScrollBar.Value = Math.Max(0, e.ScrollbackCount - e.ViewportOffset);
            TerminalScrollBar.Visibility = !e.IsAlternateBuffer && e.ScrollbackCount > 0 ? Visibility.Visible : Visibility.Hidden;
            BackToEndButton.Visibility = !e.IsAlternateBuffer && e.ViewportOffset > 0 ? Visibility.Visible : Visibility.Collapsed;
            NewOutputBadge.Visibility = e.HasNewOutput ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingScrollBar = false; }
    }

    private void TerminalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingScrollBar || TerminalDisplay?.Emulator is not { } emulator) return;
        emulator.ScrollToOffset((int)Math.Round(TerminalScrollBar.Maximum - e.NewValue));
    }

    private void BackToEnd_Click(object sender, RoutedEventArgs e)
    {
        TerminalDisplay.Emulator?.ScrollToBottom();
        TerminalDisplay.Focus();
    }

    private void ResetScrollState()
    {
        if (TerminalScrollBar is null) return;
        _updatingScrollBar = true;
        try
        {
            TerminalScrollBar.Maximum = 0;
            TerminalScrollBar.Value = 0;
            TerminalScrollBar.Visibility = Visibility.Hidden;
            BackToEndButton.Visibility = Visibility.Collapsed;
            NewOutputBadge.Visibility = Visibility.Collapsed;
        }
        finally { _updatingScrollBar = false; }
    }

    private void Terminal_OperationFailed(object? sender, TerminalSurfaceErrorEventArgs e) => ShowNotice(e.ResourceKey);
    private void ShowNotice(string key)
    {
        _noticeKey = key;
        OperationNoticeText.Text = UiText.Get(key);
        OperationNotice.Visibility = Visibility.Visible;
    }
    private void DismissNotice_Click(object sender, RoutedEventArgs e)
    {
        _noticeKey = null;
        OperationNotice.Visibility = Visibility.Collapsed;
    }
}
