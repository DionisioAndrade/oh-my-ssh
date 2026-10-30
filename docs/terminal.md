# Interactive SSH terminal

## Open and close

Connect to a VPS, open **Terminal** from the sidebar or the file workspace's terminal tab, then select **Open terminal**. This explicitly starts one remote shell and requests an `xterm-256color` PTY on the already authenticated SSH connection. It does not run a local shell or request another set of credentials.

The full terminal page and docked terminal tab share that shell and its screen. Selecting or editing another saved profile cannot redirect it. Changing the visible terminal's size updates the emulator and remote PTY; rapid size changes are coalesced. Zero or invalid dimensions are ignored. The native viewport supports 2–500 columns and 2–200 rows.

**Close terminal**, VPS disconnection, transport loss, and application shutdown cancel terminal work and release the channel, input queue, emulator buffers, and subscriptions. A shell that exits or fails leaves a clear status and requires another explicit **Open terminal** action. Previous screen contents are cleared when the shell closes. Input and commands are never replayed, and a new shell never opens automatically after failure or reconnection.

## Rendering and interaction

The renderer is a native WPF control backed by XTerm.NET 3.0.0, rather than a text box or embedded browser. It renders the terminal's current cells, cursor, attributes, scrolling regions, and alternate screen. It supports standard ANSI colors, 256-color sequences, and true color, with a dark terminal palette readable within either application theme.

UTF-8 decoding retains state across SSH reads. Wide CJK characters, combining characters, and emoji use the emulator's cell-width handling. The appearance of individual glyphs depends on installed fonts; the default font stack is Cascadia Mono with Consolas fallback.

Printable text, Enter, Backspace, navigation keys, function keys, and Ctrl/Alt combinations go to the remote program. Application cursor modes and bracketed paste follow the remote application's terminal modes. Tab goes to the remote program; **Ctrl+Tab** or the context menu's focus action moves focus out of the terminal. The application-wide shortcuts do not take Ctrl+K or function keys away from a focused terminal.

- **Ctrl+Shift+C** copies the selection; **Ctrl+C** remains available for remote interrupts.
- **Ctrl+Shift+V** or the context menu pastes; **Ctrl+V** remains a remote key combination.
- Mouse selection, the context menu, wheel scrolling, and Shift+PageUp/PageDown provide access to bounded scrollback. When a remote program requests mouse reporting, hold Shift to use local selection and scrolling.
- Paste accepts at most 1 MiB of UTF-8 text. The emulator applies its paste filtering and adds bracketed-paste delimiters when requested by the remote program.

Remote escape sequences cannot read or write the Windows clipboard, open host links, show host notifications, execute local programs, or transfer files through this terminal. Sixel, Kitty graphics, iTerm2 inline images, and OSC 52 clipboard access are disabled. File transfer remains in the SFTP interface.

## Responsiveness and limits

The output reader parses bytes away from the WPF dispatcher. The surface samples a dirty flag at most 30 times per second and draws a bounded viewport. Scrollback is limited to 2,000 lines; brushes and glyph caches also have fixed limits. Hidden terminal surfaces stop rendering. Closing a shell clears its screen and discards queued input.

The adapter handles full-history resize defects in the pinned engine through its public buffer APIs. Width reductions use bounded intermediate stages so temporary reflow allocations remain proportional to the retained grid and history; only the final dimensions are reported to the remote program. Resizing preserves the live screen and bounded history. Changing the column count clears the current text selection.

The application input queue has a 2 MiB budget, allowing framing and terminal replies around an accepted 1 MiB paste. The SSH adapter has a 512 KiB output ring; exceeding either limit closes the shell with an actionable status instead of silently dropping or replaying data. A smaller command output or paste can be used after explicitly reopening the terminal.

SSH.NET's public `ShellStream` API creates the channel and completes its synchronous shell handshake before returning it. Its internal opening-time buffer is not configurable as a hard limit. The application attaches its bounded reader immediately after creation, but a server sending excessive data before acknowledging the shell can temporarily grow that library-owned buffer. Cancellation stops the visible opening attempt promptly; a canceled handshake is still tracked and retired safely. Closing the parent VPS transport interrupts its pending shell setup before credentials and clients are disposed.

Terminal contents, typed commands, clipboard data, and raw parser/transport exceptions are never added to application diagnostics. Status logs remain in English while the interface supports English, Brazilian Portuguese, and Spanish.

## Engine selection

The selected dependency is [XTerm.NET 3.0.0](https://www.nuget.org/packages/XTerm.NET/3.0.0), a headless .NET 10 terminal emulator under the MIT License. Its source is pinned to [tag 3.0.0](https://github.com/tomlm/XTerm.NET/tree/3.0.0), commit `453ed957885f2e87c777e9b4f0b88a3582065eab`. It supplies the VT state machine, incremental UTF-8, normal/alternate buffers, colors, Unicode widths, key encoding, and paste modes. The project supplies rendering, selection integration, bounded I/O, session ownership, and host-side behavior restrictions.

Older XtermSharp was considered but did not meet the required scrollback/reflow capability. Experimental WPF terminal controls were not selected in place of a stable engine. This approach preserves the native WPF application and avoids a browser, JavaScript runtime, or local terminal process in the product. The dependency's [verbatim license](../src/OhMySsh.Desktop/Assets/LICENSE.XTerm.NET.txt) is included in application output and listed in [third-party notices](../THIRD_PARTY_NOTICES.md).

## Verification

Run the WPF checks, including emulator behavior, routed keys, native rendering, session ownership, lifecycle, high-volume output, localization, and both themes:

```powershell
./scripts/Verify-OhMySsh.ps1
```

Prepare the optional controlled real-program fixture, then run the SSH/SFTP and terminal integration suite:

```powershell
./scripts/Verify-Ssh.ps1 -InstallTerminalFixture
./scripts/Verify-Ssh.ps1
```

For terminal integration checks alone:

```powershell
./scripts/Verify-Ssh.ps1 -TerminalOnly
```

The fixture uses an isolated AsyncSSH loopback server and checksum-pinned portable MSYS2 Bash, Vim, tmux, socat, and coreutils packages under `.local`. Socat provides a byte-transparent POSIX PTY, and stty delivers actual size changes to the child programs. The bridge and extra Python dependencies are test-only. It does not install Windows services, change the system PATH or SSH configuration, read the user's shell/editor configuration, or contact a VPS. Temporary sessions, sockets, processes, credentials, captures, and reports belong to the controlled fixture. No portable runtime or test dependency is packaged with the desktop application.

Multiple simultaneous VPS sessions, local shells, scripting, automation, terminal recording, and escape-sequence file transfer remain outside this delivery.
