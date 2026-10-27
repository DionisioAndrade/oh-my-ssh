# Architecture

## Dependencies

`Desktop → Core` and `Desktop → Infrastructure → Core`.

Core contains immutable profile and file models, along with persistence and navigation contracts, without a WPF dependency. Infrastructure implements these contracts. The application wires up dependencies in `App` and injects `MainViewModel` into the main window.

## VPS profiles

`ConnectionsPage` is the entry screen: `ManualConnectionView` appears above the saved server library. `MainWorkspaceGrid` is the file browser page. Saving a profile opens the library through the `ProfileSaved` event. Startup loads profiles without raising this event.

Card commands receive a profile and resolve its ID against the current collection, ensuring that an action targets the correct server regardless of the current selection. Creating, editing, deleting, and favoriting profiles persist a snapshot before updating the interface. Creating and editing clear filters only after a successful save, so the saved profile remains visible. Verification covers persistence in a real file and reloading through a new instance.

`SshCommandBuilder` generates an OpenSSH command for PowerShell with quoted arguments, without starting a process. This action is available only for SSH/SFTP profiles. Generating the command leaves session state and SSH host identity checks unchanged.

## Session state

`IRemoteConnectionService` defines the connection contract; `IRemoteConnectionSession` represents a confirmed session and its asynchronous cleanup. `SelectedConnection` identifies the selected profile; `ActiveConnection` comes from the connected session and retains its own snapshot, even when a saved profile is edited.

The ViewModel exposes `HasActiveSession` only after awaiting the service and confirming `IsConnected`. `ActiveSessionChanged` tells the shell to open the VPS dashboard after a successful connection or return to the connection screen after disconnection. Navigation requires a session to open workspace pages, including through programmatic calls and history. The default unavailable service never returns a simulated session; QA sessions exist exclusively in the Verify project.

The manual form supplies a temporary profile for a connection attempt. Saving it requires an explicit command and uses the same profile store. Connection attempts, failures, and cancellations leave the saved library unchanged.

MVVM keeps search, selection, persistence, and navigation outside XAML. Code-behind handles window interactions: focus, keyboard input, double-clicks, resizing, and the system tray. Visual resources live in `Themes/Workbench.xaml`.

## Fluent appearance

The application uses WPF .NET 10's `Application.ThemeMode` and preserves the control templates from `PresentationFramework.Fluent`. Custom styles are named and derived from native styles. `ThemeService` maintains application colors in `Colors.Light.xaml` and `Colors.Dark.xaml`, accessed through `DynamicResource`, and observes Windows theme changes when the system theme option is selected.

Regular windows inherit their theme from the application. Setting `Window.ThemeMode` individually would introduce local styles that take precedence over application styles. Navigation switches WPF pages while preserving the ViewModel, file listing, and profiles. Gallery backgrounds are bundled local resources; the application loads no web pages or web content at runtime.

## Persistence

`JsonConnectionProfileStore` is an initial implementation without external dependencies. Each write uses a temporary file in the same directory and replaces the destination after serialization and flushing. Read failures block profile changes in the interface to prevent overwriting a recoverable file with an empty list. A single application instance per Windows session prevents concurrent writers during normal use.

SQLite should be introduced when transfer and execution history requires queries and transactions across entities. Migration should import JSON profiles, preserve the original file, and validate IDs. Credentials should be stored separately from profiles and history through the Windows credential store.

## Planned integrations

1. An SSH.NET adapter for authentication, command execution, SFTP, and tunnels. Host key verification must happen before accepting a session. Passwords and passphrases should be requested and stored separately from profiles.
2. A FluentFTP adapter for FTP/FTPS, keeping the protocol and its capabilities explicit. The profile editor defaults to explicit FTPS on port 21.
3. A dedicated VT/ANSI terminal emulator. A shell transport requires an emulator to render interactive programs. Validate Unicode, PTY, `vim`, `tmux`, resizing, and multiple sessions before choosing the component.
4. A transfer queue with cancellation, progress, and partial-file handling.
5. A task automation runner independent of tabs and windows, with persisted steps, timeouts, history, and a retry policy suited to each operation.

The application contains no fake adapters or simulated connected states. Profiles used in screenshots and verification are fixtures belonging to the verifier and are never inserted into the user's profile store.
