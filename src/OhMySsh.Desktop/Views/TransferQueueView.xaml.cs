using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop.Views;

public partial class TransferQueueView : UserControl
{
    public TransferQueueView() => InitializeComponent();

    private void TransferActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        var paths = TransferDropData.ReadFiles(e.Data);
        var allowed = DataContext is MainViewModel viewModel && paths.Length > 0
            && (e.AllowedEffects & DragDropEffects.Copy) != 0 && viewModel.UploadFilesCommand.CanExecute(paths);
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        TransferDropOverlay.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Files_Drop(object sender, DragEventArgs e)
    {
        TransferDropOverlay.Visibility = Visibility.Collapsed;
        var paths = TransferDropData.ReadFiles(e.Data);
        var allowed = DataContext is MainViewModel viewModel && paths.Length > 0
            && (e.AllowedEffects & DragDropEffects.Copy) != 0 && viewModel.UploadFilesCommand.CanExecute(paths);
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (allowed && DataContext is MainViewModel current) current.UploadFilesCommand.Execute(paths);
    }

    private void Files_DragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
            TransferDropOverlay.Visibility = Visibility.Collapsed;
    }
}
