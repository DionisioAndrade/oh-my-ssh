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

The ViewModel exposes `HasActiveSession` only after awaiting the service and confirming `IsConnected`. `ActiveSessionChanged` tells the shell to open the VPS dashboard after a successful connection or return to the connection screen after disconnection. Navigation requires a session to open workspace pages, including through programmatic calls and history. `App` injects the real SSH.NET service. The unavailable service remains an explicit offline default for isolated ViewModel construction; QA sessions exist exclusively in the Verify project.

The manual form supplies a temporary profile for a connection attempt. Saving it requires an explicit command and uses the same profile store. Connection attempts, failures, and cancellations leave the saved library unchanged.

`IRemoteConnectionSession.Disconnected` reports transport loss. The ViewModel marshals state changes to its UI dispatcher, detaches notifications before disposal, and ignores callbacks from a superseded session. Shutdown cancels pending connection attempts and disposes the active session. Late results from canceled attempts are disposed without activating the workspace.

## SSH transport, credentials, and trust

`SshRemoteConnectionService` in Infrastructure uses the pinned SSH.NET dependency. It accepts SSH and SFTP profiles and rejects FTP/FTPS before opening a connection. Each attempt uses an immutable profile snapshot and a cancellation token. Profiles carry a connection timeout in seconds, validated between 1 and 300, with a 30-second default.

The transport deadline starts after credential entry and includes host verification. Synchronous SSH.NET key decoding and some handshake stages may finish after the UI has observed cancellation. Abandoned workers are observed and disposed; shutdown waits for their retirement. This avoids activating late connections or disposing resources concurrently with a library stage that still owns them.

Core defines temporary credentials, the credential provider, server identities, trust decisions, and a known-host store. These contracts have no WPF dependency. Native Desktop dialogs implement credential collection and first-use host verification through the dispatcher.

Passwords and key passphrases are transient objects with redacted string representations. `WindowsCredentialStore` uses Windows Credential Manager for explicit opt-in persistence after successful authentication. Stored credentials are scoped to the profile and its connection settings so editing a profile cannot reuse credentials for a different endpoint. Manual connection IDs are absent from the saved library and cannot opt in. Failed cached credentials are invalidated so a retry can request fresh input.

Unknown host identities display the hostname, port, algorithm, and SHA-256 fingerprint. The user may trust once, trust and remember, or reject. `JsonKnownHostStore` persists non-secret identities separately from profiles and credentials. Malformed trust data and changed keys fail closed. A changed identity requires deliberate review of the local trust entry outside the connection flow; it is never silently replaced.

Connection failures use typed codes and safe English messages. Application-generated logs do not include credential objects, key contents, raw transport exceptions, or command output. Native strings retained by PasswordBox, SSH.NET, and Windows APIs cannot be guaranteed to be erased from process memory; disposal releases references and network resources.

Explicit disclosure in `SshCredentialsWindow` is separate from transport credential reuse. Each reveal/copy request captures its dialog generation, authenticates through `IWindowsUserVerificationService`, and then rechecks cancellation, visibility, lifecycle, and native foreground ownership. The native adapter collects a Windows password, validates it with `LogonUser`, and requires the returned token's SID to match the process user; prompt success alone never authorizes disclosure. Late or rejected results cannot populate a closed, edited, or inactive dialog. The temporary display clears on hide, deactivation, edit, connect, and close, with a 15-second timeout and no undo/selection copy path.

For a local Windows account, the native password prompt fixes the account name to `COMPUTER\user`. Validation explicitly uses the computer's local account database and still requires the resulting token's SID to match the process user. The prompt distinguishes this local Windows password from Microsoft account and SSH/VPS passwords.

`CredentialClipboardService` handles deliberate secret copies separately from address copies. Secret entries include history/cloud opt-out metadata and an ownership marker. Cleanup is registered before clipboard publication, since an OLE flush failure can leave already-published data in place. A dispatcher timer attempts to clear only expired owned entries after 30 seconds without retaining or reading back the secret, preserving earlier pending markers when a subsequent copy fails. Raw private-key metadata is not echoed in the authentication dialog; existing non-secret profile fields and opt-in vault connection behavior remain separate and unchanged.

MVVM keeps search, selection, persistence, and navigation outside XAML. Code-behind handles window interactions: focus, keyboard input, double-clicks, resizing, and the system tray. Visual resources live in `Themes/Workbench.xaml`.

## Fluent appearance

The application uses WPF .NET 10's `Application.ThemeMode` and preserves the control templates from `PresentationFramework.Fluent`. Custom styles are named and derived from native styles. `ThemeService` maintains application colors in `Colors.Light.xaml` and `Colors.Dark.xaml`, accessed through `DynamicResource`, and observes Windows theme changes when the system theme option is selected.

Regular windows inherit their theme from the application. Setting `Window.ThemeMode` individually would introduce local styles that take precedence over application styles. Navigation switches WPF pages while preserving the ViewModel, file listing, and profiles. Gallery backgrounds are bundled local resources; the application loads no web pages or web content at runtime.

## Interface localization

All application-owned interface text supports `en-US`, `pt-BR`, and `es-ES`. The first launch defaults to `pt-BR`. `LocalizationService` initializes the selected culture before creating windows and atomically stores the preference in `%LocalAppData%\OhMySsh\settings.json`, independently of profiles, host trust, and credentials. An unreadable or unsupported preference falls back to `pt-BR`. A save failure leaves the current language usable and displays a localized inline error in Settings.

Core embeds the JSON catalogs in `Localization/Catalogs`. Each module has one catalog per supported culture with identical keys and format arguments. `UiText` resolves the active catalog, provides an English fallback, and notifies bindings and weak model observers when the culture changes. Desktop's `LocExtension` binds XAML to this catalog; dynamic ViewModel messages retain resource keys and arguments so existing statuses can be translated without repeating operations. Window language and number, date, and duration formatting follow the selected culture.

Settings changes update open views, dialogs, validation, transfer rows, accessibility labels, and the tray menu without restarting or altering an active session. User-entered profile names, groups, hostnames, paths, and filenames are data and remain unchanged. Standard controls supplied by Windows may use the operating system language; application-provided native dialog titles and messages still use the selected language.

Documentation, developer-script output, and diagnostic logs remain in English. UI descriptions of typed failures use localized safe text, while `UiText.English` explicitly resolves log messages independently of the current culture. Localization does not expose raw transport exceptions or weaken credential disclosure checks. See [AGENTS.md](../AGENTS.md) for the required policy when adding interface text.

## Persistence

`JsonConnectionProfileStore` is an initial implementation without external dependencies. Each write uses a temporary file in the same directory and replaces the destination after serialization and flushing. Read failures block profile changes in the interface to prevent overwriting a recoverable file with an empty list. A single application instance per Windows session prevents concurrent writers during normal use.

On Windows, the final profile-file replacement tolerates brief access, sharing, or lock denials with up to five attempts and cancellation-aware delays of 40, 80, 160, and 320 milliseconds. It reuses the same serialized staging file and does not delete the existing destination, change permissions, or clear read-only attributes. Read-only and directory destinations fail immediately; other persistent denials still propagate after the retry budget. Cancellation before a replacement attempt preserves the previous file and runs staging cleanup.

SQLite should be introduced when transfer and execution history requires queries and transactions across entities. Migration should import JSON profiles, preserve the original file, and validate IDs. Credentials already use the Windows credential store and remain separate from profiles and future history.

## SFTP browsing and transfers

Core exposes session-owned `IRemoteFileService` operations and POSIX path helpers. `IRemoteConnectionSession.Files` is an optional capability, allowing offline QA sessions and SSH servers without SFTP to remain valid. Infrastructure uses SSH.NET's SFTP client for directory listing, existence checks, and individual-file streaming.

SSH.NET does not expose a supported way to attach a public SftpClient to an existing SshClient session. The active VPS therefore owns a lazy secondary SFTP transport. Each SFTP connection attempt creates its own connection and authentication state using the active profile and credentials, with cancellation tied to that attempt. Password copies are cleared during child cleanup; parsed private keys remain owned by the parent until child cleanup finishes. The SFTP client pins the exact host fingerprint accepted for the SSH session, including trust-once decisions. Parent disconnection cancels file work and disposes SFTP resources before authentication material is released.

The Desktop ViewModel keeps the loaded remote path separate from the editable path field. Navigation changes the listing only after success and rejects stale results using session/request identity. SFTP subsystem errors remain separate from SSH authentication and do not create a simulated listing.

The in-memory queue owns immutable session and path snapshots. One job runs at a time. UI progress is throttled; 100 percent and Completed are published only after destination finalization succeeds. Progress explicitly identifies the final commit phase, so reaching the total byte count alone does not disable cancellation during a pending flush. Disconnect, transport loss, and application exit cancel pending work and retire running operations before releasing the session. Selecting or editing a saved profile cannot redirect an existing job.

Both transfer directions stream through bounded buffers into staging files in the destination directory. Downloads finalize with a local move; uploads use standard no-overwrite rename or the server's atomic POSIX rename extension for a confirmed replacement. The adapter never deletes an existing destination to emulate overwrite. See [SFTP transfers](sftp-transfers.md) for user-facing semantics and limitations.

`TransferQueueView` renders the same queue in the file workspace and the dedicated transfers page, preserving the native Fluent control styles and shared theme brushes.

The file workspace uses two resizable regions: the paired file browsers and a lower tabbed panel that opens on Transfers. Activity and the terminal preview share that lower region instead of reserving separate vertical space. Expanding transfers temporarily collapses the browsers and divider; restoring reuses the saved row sizes and minimum heights. These layout changes remain in window code-behind and do not mutate ViewModel session or transfer state. The shared queue combines speed and remaining time, allocates flexible width to names and progress, and bounds informational notices while keeping their complete text available through tooltips and accessibility properties.

Each ViewModel owns its own `ListCollectionView` for transfer filtering. Queued and running jobs share the active filter, while the pending summary is calculated from the complete queue. Size hints distinguish a known empty file from an unknown size and are replaced by streaming progress. State changes refresh the filtered view; timing and byte updates refresh metrics without rebuilding or scrolling the listing.

Multi-file selection, the native multi-file picker, and native file drops share one batch-upload path. The batch captures the session and loaded directory before asynchronous metadata inspection, deduplicates local paths, and adds validated jobs in chunks without blocking the dispatcher. A batch generation token rejects additions after cancel-all or session changes. Files remain serial; directories are explicitly unsupported. WPF code-behind handles selection and drag gestures, while the ViewModel decides whether and where uploads can be queued.

The remote grid supports extended selection with Ctrl, Shift, and Ctrl+A. Download captures the selected sources and session before opening a native save dialog for one file or a destination-folder picker for multiple files. The batch skips directories, rejects unsafe Windows filenames without sanitizing them, and deduplicates sources and case-insensitive destination paths, retaining the first valid file. A localized notice reports skipped entries. Accepted files use the existing serial queue, per-file overwrite confirmation, progress, cancellation, and retry behavior; navigation or profile changes cannot redirect them.

Transfer failures are classified by typed error code or exception category into static safe descriptions and suggested actions. `TransferDetailsWindow` renders a single failed job using native Fluent resources. No upstream exception message is retained in a job or displayed in the dialog. Completed-job shortcuts derive paths from the immutable job, validate its membership and state, and use the desktop interaction service. File selection uses structured Windows Shell APIs instead of constructing a command line; asynchronous Shell operations run on a short-lived STA thread.

`TransferItemViewModel` calculates speed and remaining time from a bounded recent sample window using monotonic `TimeProvider` timestamps. A dispatcher timer refreshes running-job timing when byte progress stops; terminal jobs freeze their elapsed duration. Waiting in the queue is excluded. Unknown estimates are represented explicitly rather than displaying a misleading zero.

Queue pause gates job admission without interrupting the current transfer. Cancel-all cancels pending work before it can start, while atomic finalization remains authoritative. Clearing completed history detaches row notifications and never deletes files. A retry creates a fresh job from the original immutable paths and profile settings, requires a matching active endpoint, and repeats existence checks and overwrite confirmation. Failed rows remain independent of the new attempt.

## Planned integrations

1. Extend the implemented SSH.NET transport with command execution and, later, tunnels through explicit capability contracts.
2. A FluentFTP adapter for FTP/FTPS, keeping the protocol and its capabilities explicit. The profile editor defaults to explicit FTPS on port 21.
3. A dedicated VT/ANSI terminal emulator. A shell transport requires an emulator to render interactive programs. Validate Unicode, PTY, `vim`, `tmux`, resizing, and multiple sessions before choosing the component.
4. Extend the implemented serial transfer queue with persistent history and protocol-neutral recovery policies.
5. A task automation runner independent of tabs and windows, with persisted steps, timeouts, history, and a retry policy suited to each operation.

The application contains no fake adapters or simulated connected states. Profiles used in screenshots and verification are fixtures belonging to the verifier and are never inserted into the user's profile store.

## Verification

`tools/Verify-Launcher.ps1` checks the startup script with GUID-scoped named events and mutexes, extracting only its helper functions. It covers existing-instance activation, delayed startup, concurrent launchers, and gate release after build or launch failures without compiling or opening the real application. The checks run on Windows PowerShell 5.1 and PowerShell 7.

The WPF verification script builds into `.local/verification-build` so it does not replace binaries in the application's normal output directory. Test data and reports remain in separate `.local/verify-*` directories. Persistence checks exercise short and persistent replacement locks, cancellation, read-only files, and preservation of existing JSON in the actual workspace.

Localization checks verify catalog coverage and format arguments, language preference persistence and fallback, dispatcher-safe live switching, translated dialogs and ViewModels, English diagnostics, and preservation of profiles, session state, and secrets. The suite captures the connection, settings, and transfer interface in all three languages using both themes. Its language preferences use an isolated test path and do not modify the user's saved selection.

`scripts/Verify-OhMySsh.ps1` runs the executable WPF verification suite using isolated profiles and QA-only session doubles. `scripts/Verify-Ssh.ps1` builds a separate SSH/SFTP integration executable and starts an isolated Python AsyncSSH server on loopback. The fixture uses temporary keys, credentials, and a controlled SFTP filesystem under `.local`, without modifying OpenSSH configuration, installing a Windows service, or contacting a VPS. See [SSH setup and verification](ssh-connections.md) and [SFTP transfers](sftp-transfers.md) for commands, prerequisites, and coverage.
