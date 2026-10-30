using OhMySsh.Core.Localization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace OhMySsh.Desktop.Views;

/// <summary>Collects an unsaved connection draft while leaving all session logic in the view model.</summary>
public partial class ManualConnectionView : UserControl
{
    public ManualConnectionView()
    {
        InitializeComponent();
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("Dialog.ChoosePrivateKey"),
            Filter = UiText.Get("Dialog.AllFiles"),
            CheckFileExists = true,
            Multiselect = false
        };

        var owner = Window.GetWindow(this);
        var selected = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (selected == true)
        {
            // Keep the existing TwoWay binding, including its source, intact.
            QuickKeyBox.SetCurrentValue(TextBox.TextProperty, dialog.FileName);
            QuickKeyBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
    }
}
