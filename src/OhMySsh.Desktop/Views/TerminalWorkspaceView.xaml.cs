using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop.Views;

public partial class TerminalWorkspaceView : UserControl
{
    private TerminalWorkspaceViewModel? _model;

    public TerminalWorkspaceView()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe(DataContext as TerminalWorkspaceViewModel);
        Unloaded += (_, _) => Subscribe(null);
    }

    private void Workspace_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded) Subscribe(e.NewValue as TerminalWorkspaceViewModel);
    }

    private void Subscribe(TerminalWorkspaceViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null) _model.TerminalOpened -= Terminal_Opened;
        _model = model;
        if (_model is not null) _model.TerminalOpened += Terminal_Opened;
    }

    private void Terminal_Opened(TerminalSessionViewModel tab)
    {
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsVisible || _model?.SelectedTerminal != tab || !tab.IsOpen) return;
            var view = FindSurface(this);
            if (view is not null && ReferenceEquals(view.Emulator, tab.Emulator)) view.Focus();
        }));
    }

    private static TerminalSurface? FindSurface(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TerminalSurface surface) return surface;
            if (FindSurface(child) is { } found) return found;
        }
        return null;
    }

    private void TabName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: TerminalSessionViewModel tab }) return;
        Rename(tab);
        e.Handled = true;
    }

    private void RenameTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: TerminalSessionViewModel tab } } }) Rename(tab);
    }

    private void Rename(TerminalSessionViewModel tab)
    {
        var owner = Window.GetWindow(this);
        if (owner is not null) new TerminalRenameWindow(tab) { Owner = owner }.ShowDialog();
    }
}
