using System.Text;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using OhMySsh.Core.Abstractions;
using OhMySsh.Core.Localization;
using OhMySsh.Desktop;
using OhMySsh.Desktop.Services;
using OhMySsh.Desktop.Terminal;
using OhMySsh.Desktop.Views;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyTerminalControlsAsync()
    {
        var preferences = TerminalPreferencesService.Current;
        var original = preferences.Preferences;
        var originalCulture = UiText.Current.CultureName;
        await preferences.SaveAsync(TerminalPreferences.Default);
        var profile = TerminalProfile();
        var shells = new List<QaTerminalShell>();
        var shellService = new QaTerminalShellService
        {
            OpenHandler = (_, _) =>
            {
                var created = new QaTerminalShell();
                shells.Add(created);
                return Task.FromResult<IRemoteShell>(created);
            }
        };
        var session = new QaRemoteConnectionSession(profile) { Shells = shellService };
        var remote = new QaRemoteConnectionService { ConnectHandler = (_, _) => Task.FromResult<IRemoteConnectionSession>(session) };
        var model = CreateViewModel(new MemoryProfileStore([profile]), new RecordingInteractions(), remote);
        await model.InitializeAsync();
        var window = new MainWindow(model, initializeOnLoad: false) { Width = 1180, Height = 800 };
        PrepareHiddenWindow(window);
        try
        {
            window.Show();
            await model.ConnectCommand.ExecuteAsync(profile);
            window.NavigateTo("terminal");
            await SettleLayoutAsync(window);
            await model.Terminal.OpenCommand.ExecuteAsync();
            var first = model.Terminal;
            var emulator = first.Emulator!;
            var view = VisualDescendants<TerminalView>(window).Single(item => item.IsVisible);
            var surface = (TerminalSurface)view.FindName("TerminalDisplay");
            Require(surface.FontFamily.Source.Contains("Fira Code", StringComparison.Ordinal) && surface.LigaturesEnabled,
                "The default terminal did not use the bundled Fira Code font with ligatures enabled.");
            ((Button)view.FindName("ZoomInButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => preferences.Preferences.FontSize == 14 && surface.FontSize == 14);
            var restarted = new TerminalPreferencesService(Path.Combine(outputDirectory, "preferences", "terminal-settings.json"));
            Require(restarted.Preferences.FontSize == 14, "A visible zoom action was not persisted for the next application start.");
            ((Button)view.FindName("ZoomOutButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => surface.FontSize == 13);
            await preferences.UpdateAsync(current => current with { FontSize = 17 });
            ((Button)view.FindName("ResetZoomButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => surface.FontSize == TerminalPreferences.DefaultFontSize);

            await shells[0].EmitAsync(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 100).Select(index => $"fixture line {index}\r\n"))));
            var scroll = (ScrollBar)view.FindName("TerminalScrollBar");
            await WaitUntilAsync(() => scroll.IsVisible && scroll.Maximum > 25);
            scroll.Value = scroll.Maximum - 20;
            await WaitUntilAsync(() => emulator.GetSnapshot().ViewportOffset == 20 && ((Button)view.FindName("BackToEndButton")).IsVisible);
            await shells[0].EmitAsync("NEW_OUTPUT_MARKER\r\n"u8.ToArray());
            await WaitUntilAsync(() => ((Border)view.FindName("NewOutputBadge")).IsVisible);
            Require(emulator.GetSnapshot().ViewportOffset > 0, "New output pulled the user away from the inspected history.");
            Capture(window, "terminal-scrollback-new-output.png");
            ((Button)view.FindName("BackToEndButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => emulator.GetSnapshot().ViewportOffset == 0 && !((Button)view.FindName("BackToEndButton")).IsVisible);

            var workspaceView = VisualDescendants<TerminalWorkspaceView>(window).Single(item => item.IsVisible);
            InvokeTerminalButton((Button)workspaceView.FindName("NewTerminalButton"));
            await WaitUntilAsync(() => model.TerminalWorkspace.Terminals.Count == 2 && model.Terminal.IsOpen);
            var second = model.Terminal;
            Require(!ReferenceEquals(first, second) && shells.Count == 2 && first.IsOpen,
                "The new-tab action reused or closed the original interactive shell.");
            await shells[1].EmitAsync("SECOND_SHELL_READY\r\n"u8.ToArray());
            await WaitUntilAsync(() => second.Emulator!.GetVisibleText().Contains("SECOND_SHELL_READY", StringComparison.Ordinal));
            var rename = new TerminalRenameWindow(second) { Owner = window };
            PrepareHiddenWindow(rename);
            _ = rename.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ((TextBox)rename.FindName("TabNameBox")).Text = "Build shell";
                ((Button)rename.FindName("SaveTabNameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Require(rename.ShowDialog() == true && second.Name == "Build shell", "The native rename dialog did not update the selected tab name.");

            foreach (var culture in UiText.SupportedCultureNames)
            {
                await LocalizationService.Current.SetLanguageAsync(culture);
                foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
                {
                    ThemeService.Current.Apply(theme);
                    await SettleLayoutAsync(window);
                    view = VisualDescendants<TerminalView>(window).Single(item => item.IsVisible);
                    foreach (var name in new[] { "ZoomInButton", "ZoomOutButton", "ResetZoomButton", "CustomizeTerminalButton", "CloseTerminalButton" })
                        RequireVisibleWithinWindow(window, (Button)view.FindName(name), name);
                    Require(!((Button)view.FindName("OpenTerminalButton")).IsVisible, "The compact header displayed an unavailable open action.");
                    Require(((Button)view.FindName("ZoomInButton")).ToolTip?.ToString() == UiText.Get("TerminalControls.ZoomIn"),
                        "The visible zoom controls did not follow the selected UI language.");
                    Capture(window, $"terminal-tabs-controls-{culture}-{theme.ToString().ToLowerInvariant()}.png");
                }
            }

            var tabList = (ListBox)workspaceView.FindName("TerminalTabList");
            var close = FindVisualChild<Button>(tabList, item => item.DataContext == second && item.Name == "CloseTabButton")!;
            InvokeTerminalButton(close);
            await WaitUntilAsync(() => model.TerminalWorkspace.Terminals.Count == 1 && shells[1].DisposeCalls == 1);
            Require(ReferenceEquals(model.Terminal, first) && first.IsOpen && !emulator.GetVisibleText().Contains("SECOND_SHELL_READY", StringComparison.Ordinal),
                "Closing a visible tab changed another shell's input, screen, or lifetime.");
        }
        finally
        {
            await model.ShutdownAsync();
            window.Close();
            await preferences.SaveAsync(original);
            ThemeService.Current.Apply(AppTheme.Light);
            await LocalizationService.Current.SetLanguageAsync(originalCulture);
        }
    }

    private static void InvokeTerminalButton(Button button)
    {
        var invocation = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
        invocation.Invoke();
    }
}
