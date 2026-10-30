using System.IO;
using System.Threading;
using System.Windows;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.ViewModels;
using OhMySsh.Infrastructure.Files;
using OhMySsh.Infrastructure.Connections;
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
        LocalizationService.Current.Initialize(this);
        ThemeService.Current.Initialize(this);
        TerminalPreferencesService.Current.Initialize();

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
                MessageBox.Show(UiText.Get("App.AlreadyOpen"),
                    "oh-my-ssh", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        MainViewModel? viewModel = null;
        var sshInteractions = new DesktopSshInteractionService(new WindowsCredentialStore(),
            id => viewModel?.Connections.Any(profile => profile.Id == id) == true, Dispatcher);
        var remoteConnections = new SshRemoteConnectionService(sshInteractions, sshInteractions, new JsonKnownHostStore());
        viewModel = new MainViewModel(new JsonConnectionProfileStore(), new LocalFileService(),
            new DesktopInteractionService(), remoteConnections, ApplicationLog.Write);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, _) => Dispatcher.BeginInvoke(window.RestoreFromTray),
            null, Timeout.Infinite, false);

        DispatcherUnhandledException += (_, args) =>
        {
            // Record a safe failure category; raw exception text can contain supplied secrets.
            ApplicationLog.WriteFailure("Unhandled UI operation", args.Exception);

            MessageBox.Show(window, UiText.Get("App.OperationFailed"),
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
