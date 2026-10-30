using System.Windows;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop.Views;

public partial class TransferDetailsWindow : Window
{
    public TransferDetailsWindow(TransferItemViewModel job)
    {
        InitializeComponent();
        DataContext = job;
    }
}
