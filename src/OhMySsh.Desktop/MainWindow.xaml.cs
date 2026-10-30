using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OhMySsh.Core.Models;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Desktop.Terminal;

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
    private bool _updatingLanguagePicker;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;
    private Point? _localDragOrigin;
    private FileEntry? _deferredLocalSelection;
    private bool _transfersExpanded;
    private GridLength _workspaceBrowserHeight;
    private GridLength _workspaceTransferHeight;
    private double _workspaceBrowserMinimum;
    private double _workspaceTransferMinimum;

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
        UiText.Current.LanguageChanged += OnLanguageChanged;
        UpdateThemeControls();
        UpdateLanguageControls();
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

    private void TerminalPreferences_Click(object sender, RoutedEventArgs e)
        => new Views.TerminalPreferencesWindow { Owner = this }.ShowDialog();

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
        var shutdown = Task.WhenAll(_viewModel.ShutdownAsync(), PendingLanguageChange);
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
        UiText.Current.LanguageChanged -= OnLanguageChanged;
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

    public Task PendingLanguageChange { get; private set; } = Task.CompletedTask;

    private void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigationReady && !_updatingLanguagePicker && LanguagePicker.SelectedItem is ComboBoxItem { Tag: string code })
            PendingLanguageChange = ChangeLanguageAsync(code);
    }

    private async Task ChangeLanguageAsync(string code)
    {
        LanguagePicker.IsEnabled = false;
        LanguageSaveErrorText.Visibility = Visibility.Collapsed;
        try { await LocalizationService.Current.SetLanguageAsync(code); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LanguageSaveErrorText.Visibility = Visibility.Visible;
            ApplicationLog.WriteFailure("Language preference save", exception);
        }
        finally { LanguagePicker.IsEnabled = true; }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateLanguageControls();

    private void UpdateLanguageControls()
    {
        _updatingLanguagePicker = true;
        try
        {
            LanguagePicker.SelectedValue = UiText.Current.CultureName;
        }
        finally { _updatingLanguagePicker = false; }
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

    private void TransferPanelSize_Changed(object sender, RoutedEventArgs e)
    {
        if (!_navigationReady) return;
        var expanded = ExpandTransfersToggle.IsChecked == true;
        if (_transfersExpanded == expanded) return;
        _transfersExpanded = expanded;
        if (expanded)
        {
            _workspaceBrowserHeight = FileBrowserRow.Height;
            _workspaceTransferHeight = WorkspaceTransferRow.Height;
            _workspaceBrowserMinimum = FileBrowserRow.MinHeight;
            _workspaceTransferMinimum = WorkspaceTransferRow.MinHeight;
            FileBrowserGrid.Visibility = Visibility.Collapsed;
            WorkspaceFilesSplitter.Visibility = Visibility.Collapsed;
            FileBrowserRow.MinHeight = 0;
            FileBrowserRow.Height = new GridLength(0);
            WorkspaceSplitterRow.Height = new GridLength(0);
            WorkspaceTransferRow.Height = new GridLength(1, GridUnitType.Star);
            WorkspaceTabs.SelectedItem = TransfersWorkspaceTab;
        }
        else
        {
            FileBrowserRow.MinHeight = _workspaceBrowserMinimum;
            WorkspaceTransferRow.MinHeight = _workspaceTransferMinimum;
            FileBrowserRow.Height = _workspaceBrowserHeight;
            WorkspaceTransferRow.Height = _workspaceTransferHeight;
            WorkspaceSplitterRow.Height = new GridLength(8);
            FileBrowserGrid.Visibility = Visibility.Visible;
            WorkspaceFilesSplitter.Visibility = Visibility.Visible;
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
            UiText.Get("Shell.AboutBody"), UiText.Get("Shell.About"), MessageBoxButton.OK, MessageBoxImage.Information);

    private void FocusSearch_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo("connections");
        ConnectionSearchBox.Focus();
        ConnectionSearchBox.SelectAll();
    }

    private void HandleWindowShortcuts(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TerminalSurface) return;
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

    private void LocalFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.UpdateSelectedLocalSelection(LocalFilesGrid.SelectedItems.OfType<FileEntry>());
    }

    private void LocalFiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _localDragOrigin = null;
        _deferredLocalSelection = null;
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not FileEntry entry) return;
        _localDragOrigin = e.GetPosition(LocalFilesGrid);
        if (!entry.IsDirectory && LocalFilesGrid.SelectedItems.Count > 1
            && LocalFilesGrid.SelectedItems.Contains(entry) && Keyboard.Modifiers == ModifierKeys.None)
        {
            // Keep an existing multi-selection intact while deciding between a click and a drag.
            _deferredLocalSelection = entry;
            LocalFilesGrid.Focus();
            e.Handled = true;
        }
    }

    private void LocalFiles_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _localDragOrigin = null;
        if (_deferredLocalSelection is not { } entry) return;
        _deferredLocalSelection = null;
        LocalFilesGrid.SelectedItems.Clear();
        LocalFilesGrid.SelectedItem = entry;
        e.Handled = true;
    }

    private void LocalFiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _localDragOrigin is not { } origin) return;
        var position = e.GetPosition(LocalFilesGrid);
        if (Math.Abs(position.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _localDragOrigin = null;
        _deferredLocalSelection = null;
        var paths = LocalFilesGrid.SelectedItems.OfType<FileEntry>()
            .Select(entry => entry.FullPath).ToArray();
        if (paths.Length == 0) return;
        DragDrop.DoDragDrop(LocalFilesGrid, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
        e.Handled = true;
    }

    private void UploadFiles_DragOver(object sender, DragEventArgs e)
    {
        var paths = TransferDropData.ReadFiles(e.Data);
        var allowed = paths.Length > 0 && (e.AllowedEffects & DragDropEffects.Copy) != 0
            && _viewModel.UploadFilesCommand.CanExecute(paths);
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        RemoteDropOverlay.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void UploadFiles_Drop(object sender, DragEventArgs e)
    {
        RemoteDropOverlay.Visibility = Visibility.Collapsed;
        var paths = TransferDropData.ReadFiles(e.Data);
        var allowed = paths.Length > 0 && (e.AllowedEffects & DragDropEffects.Copy) != 0
            && _viewModel.UploadFilesCommand.CanExecute(paths);
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (allowed) _viewModel.UploadFilesCommand.Execute(paths);
    }

    private void UploadFiles_DragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(RemoteFilesPanel);
        if (point.X < 0 || point.Y < 0 || point.X > RemoteFilesPanel.ActualWidth || point.Y > RemoteFilesPanel.ActualHeight)
            RemoteDropOverlay.Visibility = Visibility.Collapsed;
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

    private void RemoteFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.UpdateSelectedRemoteSelection(RemoteFilesGrid.SelectedItems.OfType<FileEntry>());
    }

    private void RemoteFiles_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is FileEntry entry)
        {
            OpenRemoteDirectory(entry);
        }
    }

    private void RemoteFiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SelectedRemoteEntry is { } entry)
        {
            OpenRemoteDirectory(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _viewModel.GoUpRemoteCommand.CanExecute(null))
        {
            _viewModel.GoUpRemoteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenRemoteDirectory(FileEntry entry)
    {
        if (_viewModel.OpenRemoteEntryCommand.CanExecute(entry))
        {
            _viewModel.OpenRemoteEntryCommand.Execute(entry);
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
