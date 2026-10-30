using OhMySsh.Core.Localization;
using System.Windows;

namespace OhMySsh.Desktop.Views;

public partial class OverwriteConfirmationWindow : Window
{
    private readonly bool _isRemote;

    public OverwriteConfirmationWindow(string destination, bool isRemote)
    {
        _isRemote = isRemote;
        InitializeComponent();
        DestinationTextBox.Text = destination;
        RefreshLocalizedText();
        UiText.Current.LanguageChanged += LanguageChanged;
        Closed += (_, _) => UiText.Current.LanguageChanged -= LanguageChanged;
    }

    private void LanguageChanged(object? sender, EventArgs e) => RefreshLocalizedText();

    private void RefreshLocalizedText()
    {
        LocationText.Text = _isRemote
            ? UiText.Get("Dialog.ReplaceRemote")
            : UiText.Get("Dialog.ReplaceLocal");
    }

    private void Replace_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
