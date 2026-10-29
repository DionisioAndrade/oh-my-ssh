using System.Windows;
using System.Windows.Threading;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Models;
using OhMySsh.Desktop.Views;
using OhMySsh.Infrastructure.Connections;

namespace OhMySsh.Desktop.Services;

/// <summary>Marshals credential and host verification prompts to the native UI thread.</summary>
public sealed class DesktopSshInteractionService(
    WindowsCredentialStore credentialStore,
    Func<Guid, bool> isSavedProfile,
    Dispatcher dispatcher) : IRemoteCredentialProvider, IHostKeyPrompt
{
    public async Task<RemoteConnectionCredentials?> GetCredentialsAsync(
        ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowRemember = await dispatcher.InvokeAsync(() => isSavedProfile(profile.Id));
        if (allowRemember)
        {
            var saved = await credentialStore.LoadAsync(profile, cancellationToken);
            if (saved is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    saved.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return saved;
            }
        }

        return await ShowAsync(
            () => new SshCredentialsWindow(profile, allowRemember),
            window => ((SshCredentialsWindow)window).Credentials, cancellationToken);
    }

    public async Task SaveCredentialsAsync(ConnectionProfile profile, RemoteConnectionCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowRemember = await dispatcher.InvokeAsync(() => isSavedProfile(profile.Id));
        if (credentials.Remember && allowRemember)
        {
            await credentialStore.SaveAsync(profile, credentials, cancellationToken);
        }
    }

    public async Task InvalidateAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        await credentialStore.DeleteAsync(profile, cancellationToken);
    }

    public Task<HostKeyDecision> ConfirmAsync(SshHostIdentity identity, CancellationToken cancellationToken = default)
        => ShowAsync(() => new HostKeyVerificationWindow(identity),
            window => ((HostKeyVerificationWindow)window).Decision, cancellationToken);

    private async Task<TResult> ShowAsync<TResult>(Func<Window> createWindow, Func<Window, TResult> readResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = createWindow();
            if (Application.Current?.MainWindow is { IsVisible: true } owner)
            {
                window.Owner = owner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            using var registration = cancellationToken.Register(() =>
            {
                if (!dispatcher.HasShutdownStarted)
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                    {
                        if (window.IsVisible)
                        {
                            window.Close();
                        }
                    }));
                }
            });

            window.ShowDialog();
            var result = readResult(window);
            if (cancellationToken.IsCancellationRequested)
            {
                (result as IDisposable)?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return result;
        });
    }
}
