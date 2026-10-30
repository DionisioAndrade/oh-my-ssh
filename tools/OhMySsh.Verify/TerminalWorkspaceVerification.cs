using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyTerminalWorkspaceAsync()
    {
        var originalCulture = UiText.Current.CultureName;
        var profile = TerminalProfile();
        var other = profile with { Id = Guid.NewGuid(), Name = "Other fixture", Host = "other.example.invalid" };
        var shell = new QaTerminalShell();
        var shells = new QaTerminalShellService { OpenHandler = (_, _) => Task.FromResult<IRemoteShell>(shell) };
        var session = new QaRemoteConnectionSession(profile) { Shells = shells };
        var service = new QaRemoteConnectionService { ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(session) };
        var model = CreateViewModel(new MemoryProfileStore([profile, other]), new RecordingInteractions(), service);
        await model.InitializeAsync();
        var window = new MainWindow(model, initializeOnLoad: false) { Width = 1180, Height = 800 };
        PrepareHiddenWindow(window);
        try
        {
            window.Show();
            await model.ConnectCommand.ExecuteAsync(profile);
            window.NavigateTo("terminal");
            await SettleLayoutAsync(window);
            Require(shells.Requests.Count == 0, "Opening the terminal page started a shell without explicit action.");
            await model.Terminal.OpenCommand.ExecuteAsync();
            var emulator = model.Terminal.Emulator!;
            await shell.EmitAsync(Encoding.UTF8.GetBytes("test-user@example.invalid\r\n\u001b[38;2;120;190;255mShared SSH shell\u001b[0m\r\n$ "));
            await WaitUntilAsync(() => emulator.GetVisibleText().Contains("Shared SSH shell", StringComparison.Ordinal));
            model.SelectedConnection = model.Connections.Single(item => item.Id == other.Id);
            var tabs = (TabControl)window.FindName("WorkspaceTabs");

            foreach (var culture in UiText.SupportedCultureNames)
            {
                await LocalizationService.Current.SetLanguageAsync(culture);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.Current.Apply(theme);
                    foreach (var docked in new[] { false, true })
                    {
                        window.NavigateTo(docked ? "files" : "terminal");
                        if (docked) tabs.SelectedIndex = 1;
                        await SettleLayoutAsync(window);
                        await Task.Delay(140); // Includes the production 100 ms resize coalescing window.
                        var view = VisualDescendants<TerminalView>(window).Single(item => item.IsVisible);
                        var surface = (TerminalSurface)view.FindName("TerminalDisplay");
                        Require(ReferenceEquals(view.DataContext, model.Terminal) && ReferenceEquals(surface.Emulator, emulator)
                            && model.Terminal.Endpoint.Contains(profile.Host, StringComparison.Ordinal) && shells.Requests.Count == 1,
                            "Switching terminal views or the selected profile replaced the active shell.");
                        Require(emulator.GetVisibleText().Contains("Shared SSH shell", StringComparison.Ordinal),
                            "Switching native terminal viewports lost the active screen.");
                        RequireVisibleWithinWindow(window, surface, "Shared terminal viewport");
                        Require(!((Button)view.FindName("OpenTerminalButton")).IsVisible,
                            "An active terminal displayed an unavailable open action.");
                        RequireVisibleWithinWindow(window, (Button)view.FindName("CloseTerminalButton"), "Close terminal action");
                        var snapshot = emulator.GetSnapshot();
                        var lastSize = shell.Resizes.IsEmpty
                            ? (shells.Requests[0].Columns, shells.Requests[0].Rows) : shell.Resizes.Last();
                        Require(lastSize == (snapshot.Columns, snapshot.Rows) && snapshot.Columns >= 2 && snapshot.Rows >= 2,
                            "The remote PTY did not follow the visible shared terminal viewport.");
                        Capture(window, $"terminal-workspace-{culture}-{theme.ToString().ToLowerInvariant()}-{(docked ? "docked" : "page")}.png");
                    }
                }
            }

            var active = VisualDescendants<TerminalView>(window).Single(item => item.IsVisible);
            var activeSurface = (TerminalSurface)active.FindName("TerminalDisplay");
            var writes = shell.Input.Count;
            SendTerminalKey(activeSurface, Key.Enter);
            await WaitUntilAsync(() => shell.Input.Count == writes + 1);
            Require(shell.Input.Last().AsSpan().SequenceEqual("\r"u8), "The embedded terminal intercepted a remote Enter key.");
            await model.DisconnectCommand.ExecuteAsync();
            await SettleLayoutAsync(window);
            Require(model.Terminal.Emulator is null && !model.Terminal.IsOpen && shell.DisposeCalls == 1,
                "Disconnecting left a shared terminal channel or viewport active.");
            RequireConnectionLanding(window);
        }
        finally
        {
            await model.ShutdownAsync();
            window.Close();
            ThemeService.Current.Apply(AppTheme.Light);
            await LocalizationService.Current.SetLanguageAsync(originalCulture);
        }
    }
}
