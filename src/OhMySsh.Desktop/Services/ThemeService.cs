using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OhMySsh.Desktop.Services;

public enum AppTheme
{
    Light,
    Dark,
    System
}

/// <summary>
/// Applies the native WPF Fluent theme and the application's color tokens.
/// The preference is kept in memory; no user settings are written to disk.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private const string PersonalizationKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private Application? _application;
    private ResourceDictionary? _palette;
    private bool _listeningForSystemChanges;
    private bool _disposed;

    private ThemeService()
    {
    }

    public static ThemeService Current { get; } = new();

    public AppTheme Preference { get; private set; } = AppTheme.System;

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    /// <summary>Initializes the theme on the application's dispatcher.</summary>
    public void Initialize(Application app, AppTheme preference = AppTheme.System)
    {
        ArgumentNullException.ThrowIfNull(app);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Initialize(app, preference));
            return;
        }

        if (_application is not null && !ReferenceEquals(_application, app))
        {
            throw new InvalidOperationException("O serviço de tema já está associado a outro aplicativo.");
        }

        if (_application is null)
        {
            _application = app;
            app.Exit += OnApplicationExit;
            try
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _listeningForSystemChanges = true;
            }
            catch (InvalidOperationException)
            {
                // Some non-interactive environments cannot create a system event
                // window. Explicit theme selection still works in that case.
            }
        }

        Apply(preference);
    }

    /// <summary>Changes both native controls and application DynamicResource brushes.</summary>
    public void Apply(AppTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        var app = _application ?? throw new InvalidOperationException("Inicialize o serviço de tema antes de usá-lo.");
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Apply(theme));
            return;
        }

        bool isDark = theme == AppTheme.Dark || theme == AppTheme.System && UsesDarkSystemTheme();
        if (_palette is not null && Preference == theme && IsDark == isDark)
        {
            return;
        }

        var assemblyName = typeof(ThemeService).Assembly.GetName().Name;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"/{assemblyName};component/Themes/Colors.{(isDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };

        ApplyNativeTheme(app, theme);

        // Replace only the dictionary owned by this service. Other merged
        // dictionaries, including the native Fluent resources, remain intact.
        var dictionaries = app.Resources.MergedDictionaries;
        int previousIndex = _palette is not null ? dictionaries.IndexOf(_palette) : -1;
        if (previousIndex >= 0)
        {
            dictionaries[previousIndex] = palette;
        }
        else
        {
            dictionaries.Add(palette);
        }

        _palette = palette;
        Preference = theme;
        IsDark = isDark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void ApplyNativeTheme(Application app, AppTheme theme)
    {
        // WPF exposes this API as experimental despite shipping Fluent styles.
        // Keep theme dictionaries at application scope: a Window.ThemeMode
        // would inject implicit styles into Window.Resources and shadow the
        // workbench's application-level customizations.
#pragma warning disable WPF0001
        var nativeTheme = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System
        };

        app.ThemeMode = nativeTheme;
#pragma warning restore WPF0001
    }

    private static bool UsesDarkSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizationKey, writable: false);
            return key?.GetValue("AppsUseLightTheme") is int lightTheme && lightTheme == 0;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Match Windows' light default if personalization is inaccessible.
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var app = _application;
        if (_disposed || Preference != AppTheme.System || app is null || app.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            app.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                if (!_disposed && Preference == AppTheme.System && !app.Dispatcher.HasShutdownStarted)
                {
                    Apply(AppTheme.System);
                }
            }));
        }
        catch (InvalidOperationException) when (app.Dispatcher.HasShutdownStarted)
        {
            // A queued Windows notification can race application shutdown.
        }
    }

    private void OnApplicationExit(object sender, ExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_listeningForSystemChanges)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _listeningForSystemChanges = false;
        }

        if (_application is not null)
        {
            _application.Exit -= OnApplicationExit;
            _application = null;
        }

        _palette = null;
        ThemeChanged = null;
        GC.SuppressFinalize(this);
    }
}
