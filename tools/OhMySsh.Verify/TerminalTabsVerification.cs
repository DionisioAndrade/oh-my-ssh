using System.Text;
using System.IO;
using OhMySsh.Core.Abstractions;
using OhMySsh.Desktop.ViewModels;

namespace OhMySsh.Verify;

internal sealed partial class VerificationRunner
{
    private async Task VerifyTerminalTabsAsync()
    {
        await using var workspace = new TerminalWorkspaceViewModel();
        var shells = new List<QaTerminalShell>();
        var service = new QaTerminalShellService
        {
            OpenHandler = (_, _) =>
            {
                var shell = new QaTerminalShell();
                shells.Add(shell);
                return Task.FromResult<IRemoteShell>(shell);
            }
        };
        var session = new QaRemoteConnectionSession(TerminalProfile()) { Shells = service };
        workspace.AttachSession(session);
        var focusRequests = new List<TerminalSessionViewModel>();
        workspace.TerminalOpened += focusRequests.Add;
        var first = workspace.SelectedTerminal;
        Require(workspace.Terminals.Count == 1 && service.Requests.Count == 0 && first.OpenCommand.CanExecute(null),
            "A connected terminal workspace did not start with one explicitly unopened tab.");
        first.Name = "Build logs · café";
        Require(first.Name == "Build logs · café", "Renaming a terminal changed user-entered Unicode text.");
        first.Name = "\u001b\r\n" + new string('x', 80);
        Require(first.Name.Length == 64 && !first.Name.Any(char.IsControl), "A terminal name retained control characters or exceeded its limit.");
        first.Name = string.Empty;
        Require(!first.HasCustomName && first.Name.Length > 0, "An empty terminal name did not restore its default label.");
        await first.OpenCommand.ExecuteAsync();
        var firstEmulator = first.Emulator!;
        await shells[0].EmitAsync(Encoding.UTF8.GetBytes("FIRST_ONLY\r\n"));
        await WaitUntilAsync(() => firstEmulator.GetVisibleText().Contains("FIRST_ONLY", StringComparison.Ordinal));
        await workspace.NewTerminalCommand.ExecuteAsync();
        var second = workspace.SelectedTerminal;
        Require(focusRequests.Count == 1 && ReferenceEquals(focusRequests[0], second),
            "An explicitly opened new terminal did not request focus for its selected tab.");
        Require(workspace.Terminals.Count == 2 && first.IsOpen && second.IsOpen && !ReferenceEquals(first.Emulator, second.Emulator),
            "A new terminal replaced or reused its sibling shell or emulator.");
        await shells[1].EmitAsync(Encoding.UTF8.GetBytes("SECOND_ONLY\r\n"));
        await WaitUntilAsync(() => second.Emulator!.GetVisibleText().Contains("SECOND_ONLY", StringComparison.Ordinal));
        Require(!firstEmulator.GetVisibleText().Contains("SECOND_ONLY", StringComparison.Ordinal)
            && !second.Emulator!.GetVisibleText().Contains("FIRST_ONLY", StringComparison.Ordinal), "Terminal output crossed a tab boundary.");
        firstEmulator.SendText("first-input");
        second.Emulator!.SendText("second-input");
        await WaitUntilAsync(() => shells[0].Input.Count == 1 && shells[1].Input.Count == 1);
        Require(Encoding.UTF8.GetString(shells[0].Input.Single()) == "first-input"
            && Encoding.UTF8.GetString(shells[1].Input.Single()) == "second-input", "Terminal input crossed a tab boundary.");
        first.RequestResize(101, 34);
        second.RequestResize(77, 27);
        await WaitUntilAsync(() => shells[0].Resizes.Count > 0 && shells[1].Resizes.Count > 0);
        Require(shells[0].Resizes.Last() == (101, 34) && shells[1].Resizes.Last() == (77, 27),
            "Resizing one tab changed its sibling's PTY dimensions.");
        workspace.SelectTerminalCommand.Execute(first);
        Require(ReferenceEquals(first, workspace.SelectedTerminal) && ReferenceEquals(firstEmulator, first.Emulator),
            "Selecting an existing tab opened another shell or replaced its screen.");
        await workspace.CloseTerminalCommand.ExecuteAsync(first);
        Require(shells[0].DisposeCalls == 1 && second.IsOpen && shells[1].DisposeCalls == 0 && session.IsConnected,
            "Closing a tab disposed a sibling terminal or the parent session.");
        shells[1].Fail(new IOException("SYNTHETIC_UPSTREAM_FAILURE"));
        await WaitUntilAsync(() => !second.IsOpen && second.Emulator is null && shells[1].DisposeCalls == 1);
        Require(workspace.Terminals.Count == 1 && service.Requests.Count == 2, "A failed terminal opened another shell automatically.");
        while (workspace.Terminals.Count < TerminalWorkspaceViewModel.MaximumTerminals)
            await workspace.NewTerminalCommand.ExecuteAsync();
        Require(!workspace.NewTerminalCommand.CanExecute(null) && workspace.HasError && workspace.Error.Length > 0,
            "The terminal tab limit did not disable creation or explain the limit.");
        var requests = service.Requests.Count;
        foreach (var tab in workspace.Terminals.ToArray()) await workspace.CloseTerminalCommand.ExecuteAsync(tab);
        Require(workspace.Terminals.Count == 1 && !workspace.SelectedTerminal.IsOpen && workspace.SelectedTerminal.Emulator is null
            && service.Requests.Count == requests && workspace.SelectedTerminal.OpenCommand.CanExecute(null),
            "Closing the final tab did not leave an explicitly unopened ready tab.");
        Require(shells.All(shell => shell.DisposeCalls == 1), "Closing all terminal tabs retained a channel or disposed it twice.");
    }

    private async Task VerifyTerminalTabRetirementAsync()
    {
        await using var workspace = new TerminalWorkspaceViewModel();
        var focusRequests = 0;
        workspace.TerminalOpened += _ => focusRequests++;
        var firstOpening = new TaskCompletionSource<IRemoteShell>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOpening = new TaskCompletionSource<IRemoteShell>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serviceRequest = 0;
        var service = new QaTerminalShellService
        {
            OpenHandler = (_, _) => serviceRequest++ == 0 ? firstOpening.Task : secondOpening.Task
        };
        var oldSession = new QaRemoteConnectionSession(TerminalProfile()) { Shells = service };
        workspace.AttachSession(oldSession);
        var first = workspace.SelectedTerminal;
        var firstOpen = first.OpenCommand.ExecuteAsync();
        var secondOpen = workspace.NewTerminalCommand.ExecuteAsync();
        var second = workspace.SelectedTerminal;
        Require(first.IsOpening && second.IsOpening && !ReferenceEquals(first, second), "Independent tab openings did not remain separately pending.");
        var retirement = workspace.DetachAsync();
        Require(workspace.Terminals.Count == 1 && workspace.SelectedTerminal.Emulator is null
            && !workspace.SelectedTerminal.OpenCommand.CanExecute(null), "Disconnecting retained a usable terminal viewport.");
        var freshShell = new QaTerminalShell();
        var replacementService = new QaTerminalShellService { OpenHandler = (_, _) => Task.FromResult<IRemoteShell>(freshShell) };
        var replacement = new QaRemoteConnectionSession(TerminalProfile() with { Host = "replacement.example.invalid" }) { Shells = replacementService };
        workspace.AttachSession(replacement);
        var lateFirst = new QaTerminalShell();
        var lateSecond = new QaTerminalShell();
        firstOpening.SetResult(lateFirst);
        secondOpening.SetResult(lateSecond);
        await Task.WhenAll(retirement, firstOpen, secondOpen);
        Require(lateFirst.DisposeCalls == 1 && lateSecond.DisposeCalls == 1 && workspace.Terminals.Count == 1
            && replacementService.Requests.Count == 0 && !workspace.SelectedTerminal.IsOpen && focusRequests == 0,
            "Late results from disconnected tabs activated a replacement session or retained a channel.");
        await workspace.SelectedTerminal.OpenCommand.ExecuteAsync();
        Require(freshShell.Input.Count == 0 && workspace.SelectedTerminal.Emulator!.GetVisibleText().Trim().Length == 0,
            "A replacement tab replayed data from the disconnected session.");
        await workspace.DetachAsync();
        Require(freshShell.DisposeCalls == 1 && workspace.Terminals.All(tab => tab.Emulator is null),
            "Disconnecting the replacement left terminal work active.");
    }
}
