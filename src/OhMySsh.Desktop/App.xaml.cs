using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Infrastructure.Files;
using OhMySsh.Infrastructure.Persistence;

namespace OhMySsh.Desktop;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\OhMySsh.Desktop.Instance";
    private const string ActivationEventName = @"Local\OhMySsh.Desktop.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
        ThemeService.Current.Initialize(this);

        // Only one writer owns the local profile file in a Windows session.
        _instanceMutex = new Mutex(true, InstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                existingEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                MessageBox.Show("O oh-my-ssh já está aberto. Use o ícone na bandeja para restaurar a janela.",
                    "oh-my-ssh", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        var viewModel = new MainViewModel(new JsonConnectionProfileStore(), new LocalFileService(), new DesktopInteractionService());
        var window = new MainWindow(viewModel);
        MainWindow = window;
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(window.RestoreFromTray),
            null, Timeout.Infinite, false);

        DispatcherUnhandledException += (_, args) =>
        {
            // Preserve diagnostics locally without turning recoverable UI failures into silent exits.
            try
            {
                var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMySsh", "logs");
                Directory.CreateDirectory(logFolder);
                File.AppendAllText(Path.Combine(logFolder, "application.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The original error is still displayed if diagnostics cannot be written.
            }

            MessageBox.Show(window, "Não foi possível concluir a operação. Feche e abra o aplicativo se o problema continuar.",
                "oh-my-ssh", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();
        if (_ownsMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
