using System.Windows;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop.Views;

public partial class TerminalRenameWindow : Window
{
    private readonly TerminalSessionViewModel _tab;
    public TerminalRenameWindow(TerminalSessionViewModel tab)
    {
        InitializeComponent();
        _tab = tab;
        TabNameBox.Text = tab.Name;
    }
    private void Window_Loaded(object sender, RoutedEventArgs e) { TabNameBox.Focus(); TabNameBox.SelectAll(); }
    private void Save_Click(object sender, RoutedEventArgs e) { _tab.Name = TabNameBox.Text; DialogResult = true; }
}
