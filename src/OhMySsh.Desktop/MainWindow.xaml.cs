using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly bool _initializeOnLoad;
    private SystemTrayService? _tray;
    private readonly Stack<string> _navigationHistory = new();
    private string _currentPage = "connections";
    private bool _navigationReady;
    private bool _updatingThemePicker;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public MainWindow(MainViewModel viewModel, bool initializeOnLoad = true)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _initializeOnLoad = initializeOnLoad;
        DataContext = viewModel;
        PreviewKeyDown += HandleWindowShortcuts;
        _viewModel.ProfileSaved += OnProfileSaved;
        _viewModel.ActiveSessionChanged += OnActiveSessionChanged;
        _navigationReady = true;
        ThemeService.Current.ThemeChanged += OnThemeChanged;
        UpdateThemeControls();
        NavigateTo("connections");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_initializeOnLoad)
        {
            return;
        }

        _tray ??= new SystemTrayService(this);
        await _viewModel.InitializeAsync();
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            ReleaseWindowResources();
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        var shutdown = _viewModel.ShutdownAsync();
        if (shutdown.IsCompletedSuccessfully)
        {
            _shutdownCompleted = true;
            e.Cancel = false;
            ReleaseWindowResources();
            return;
        }

        // Let the closing event return before issuing Close again after asynchronous cleanup.
        await System.Windows.Threading.Dispatcher.Yield();
        try
        {
            await shutdown;
        }
        catch (Exception exception)
        {
            ApplicationLog.WriteFailure("Connection shutdown", exception);
        }

        _shutdownCompleted = true;
        Close();
    }

    private void ReleaseWindowResources()
    {
        _tray?.Dispose();
        ThemeService.Current.ThemeChanged -= OnThemeChanged;
        _viewModel.ProfileSaved -= OnProfileSaved;
        _viewModel.ActiveSessionChanged -= OnActiveSessionChanged;
    }

    /// <summary>Changes the visible native WPF page without replacing workspace state.</summary>
    public void NavigateTo(string page)
    {
        if (page is not ("home" or "connections" or "files" or "terminal" or "transfers" or "automations" or "settings"))
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        if ((page is "home" or "files" or "terminal" or "transfers" or "automations") && !_viewModel.HasActiveSession)
        {
            page = "connections";
        }

        if (_currentPage != page)
        {
            _navigationHistory.Push(_currentPage);
            _currentPage = page;
        }

        HomePage.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = page == "connections" ? Visibility.Visible : Visibility.Collapsed;
        MainWorkspaceGrid.Visibility = page == "files" ? Visibility.Visible : Visibility.Collapsed;
        TerminalPage.Visibility = page == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        TransfersPage.Visibility = page == "transfers" ? Visibility.Visible : Visibility.Collapsed;
        AutomationsPage.Visibility = page == "automations" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        var navigationItem = page switch
        {
            "home" => HomeNav,
            "connections" => ConnectionsNav,
            "files" => FilesNav,
            "terminal" => TerminalNav,
            "transfers" => TransfersNav,
            "automations" => AutomationsNav,
            _ => SettingsNav
        };
        navigationItem.IsChecked = true;
        BackButton.IsEnabled = _navigationHistory.Count > 0;
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (_navigationReady && sender is RadioButton { Tag: string page } && _currentPage != page)
        {
            NavigateTo(page);
        }
    }

    private void OnProfileSaved(object? sender, EventArgs e)
    {
        NavigateTo("connections");
        if (_viewModel.SelectedConnection is { } profile)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (IsVisible && _viewModel.Connections.Any(saved => saved.Id == profile.Id))
                {
                    SavedProfilesList.ScrollIntoView(profile);
                }
            }));
        }
    }

    private void OnActiveSessionChanged(object? sender, EventArgs e)
    {
        _navigationHistory.Clear();
        _currentPage = _viewModel.HasActiveSession ? "home" : "connections";
        NavigateTo(_currentPage);
    }

    private void NavigateCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
        {
            NavigateTo(page);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_navigationHistory.TryPop(out var previous))
        {
            // Avoid recording the page we are leaving as a new forward visit.
            _currentPage = previous;
            NavigateTo(previous);
        }
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
        => ThemeService.Current.Apply(ThemeService.Current.IsDark ? AppTheme.Light : AppTheme.Dark);

    private void ContentSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        => ContentSurface.Clip = new RectangleGeometry(new Rect(e.NewSize), 8, 8);

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigationReady && !_updatingThemePicker && ThemePicker.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<AppTheme>(tag, out var theme))
        {
            ThemeService.Current.Apply(theme);
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateThemeControls();

    private void UpdateThemeControls()
    {
        _updatingThemePicker = true;
        try
        {
            ThemeToggleGlyph.Text = ThemeService.Current.IsDark ? "\uE706" : "\uE708";
            ThemePicker.SelectedIndex = ThemeService.Current.Preference switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0
            };
        }
        finally
        {
            _updatingThemePicker = false;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void HideToTray_Click(object sender, RoutedEventArgs e)
    {
        _tray ??= new SystemTrayService(this);
        Hide();
    }

    private void About_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(this,
            "oh-my-ssh\nVersão 0.1.0\n\nUm espaço para organizar conexões, arquivos e tarefas.\n\nLicença Apache 2.0.",
            "Sobre o oh-my-ssh", MessageBoxButton.OK, MessageBoxImage.Information);

    private void FocusSearch_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("connections");
        ConnectionSearchBox.Focus();
        ConnectionSearchBox.SelectAll();
    }

    private void HandleWindowShortcuts(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearch_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Connections_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not null &&
            _viewModel.ConnectCommand.CanExecute(_viewModel.SelectedConnection))
        {
            _viewModel.ConnectCommand.Execute(_viewModel.SelectedConnection);
        }
    }

    private void Connections_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigationReady && ConnectionsList.IsKeyboardFocusWithin && e.AddedItems.Count > 0)
        {
            NavigateTo("connections");
        }
    }

    private void LocalFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is FileEntry entry)
        {
            OpenDirectory(entry);
        }
    }

    private void LocalFiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SelectedLocalEntry is { } entry)
        {
            OpenDirectory(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _viewModel.GoUpCommand.CanExecute(null))
        {
            _viewModel.GoUpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenDirectory(FileEntry entry)
    {
        if (_viewModel.OpenLocalEntryCommand.CanExecute(entry))
        {
            _viewModel.OpenLocalEntryCommand.Execute(entry);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
            {
                return match;
            }

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }
}
