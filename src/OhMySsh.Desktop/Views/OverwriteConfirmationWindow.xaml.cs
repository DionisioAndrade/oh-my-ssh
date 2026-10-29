using System.Windows;

namespace OhMySsh.Desktop.Views;

public partial class OverwriteConfirmationWindow : Window
{
    public OverwriteConfirmationWindow(string destination, bool isRemote)
    {
        InitializeComponent();
        DestinationTextBox.Text = destination;
        LocationText.Text = isRemote
            ? "Já existe um arquivo com esse nome na VPS. Confirme para substituí-lo."
            : "Já existe um arquivo com esse nome neste computador. Confirme para substituí-lo.";
    }

    private void Replace_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
