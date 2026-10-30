using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop.Views;

public partial class TerminalView : UserControl
{
    private bool _focusAfterOpen;
    private TerminalSessionViewModel? _model;

    public TerminalView()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe(DataContext as TerminalSessionViewModel);
        Unloaded += (_, _) => Subscribe(null);
    }

    private void View_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        => Subscribe(e.NewValue as TerminalSessionViewModel);

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
        if (!_focusAfterOpen || _model?.IsOpen != true || !IsVisible) return;
        _focusAfterOpen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsVisible && _model?.IsOpen == true) TerminalDisplay.Focus();
        }));
    }

    private void Terminal_DimensionsChanged(object? sender, TerminalDimensionsEventArgs e)
        => (DataContext as TerminalSessionViewModel)?.RequestResize(e.Columns, e.Rows);

    private void Terminal_OperationFailed(object? sender, TerminalSurfaceErrorEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner is not null)
            MessageBox.Show(owner, UiText.Get(e.ResourceKey), UiText.Get("Terminal.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
