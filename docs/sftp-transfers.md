# SFTP browsing and transfers

## Requirements

Connect to a VPS using an SSH or SFTP profile, as described in [SSH connections](ssh-connections.md). The server must provide the SFTP subsystem and grant the account permission to list, read, or write the requested paths.

SFTP follows the authenticated VPS settings and the same server-identity policy. Its session-owned transport verifies the exact host fingerprint already accepted for the active connection. It does not request a separate trust decision or silently accept a changed key.

## Browse remote files

The remote panel loads the server's initial working directory after connection. It displays names, file types, sizes, and modification dates. Open a directory with a double-click or Enter, use the parent button, enter a remote path, or refresh the listing.

Remote paths use POSIX `/` separators independently of Windows local paths. Unicode filenames and spaces are supported. Loading, an empty directory, and a failed request have separate states. A failed navigation keeps the last successful listing. A server that does not provide SFTP reports an SFTP-specific error while the SSH session remains available.

## Upload and download

- To upload, select one or more local files with Ctrl or Shift and use the upload button above the local panel. If no file is selected, choose files in the native multi-file dialog.
- Drag files from the local panel or Windows Explorer onto the remote panel or transfer queue. A highlighted drop target appears when the active session can accept files. A drop adds uploads; it does not move or delete local files.
- To download, select a remote file and use the download button. Choose its destination in the native save dialog.
- Existing destinations require confirmation before replacement. Declining cancels that job and preserves the file.
- Directories are navigable but cannot be transferred recursively in this delivery.

The filename suggested for a download must be safe for Windows. Remote names cannot introduce path separators, reserved device names, or alternate data streams into the local destination. The application uses the destination chosen in the file dialog rather than treating a server-provided name as a local path.

Each upload batch captures the active VPS and loaded remote directory before inspecting the files. Navigation or profile selection cannot redirect it. Duplicate local paths in a batch are ignored. Folders and malformed paths are skipped with a visible notice; valid file paths that become missing or inaccessible produce their own failed entries. Disconnect or cancel-all stops additions from an unfinished batch. A paused queue accepts batches without starting their files.

## Filters and pending work

Use the queue filter to show all transfers, active work, completed files, failures, or cancellations. Active work includes both queued and running jobs. Filters change only the visible rows; they do not change execution order or affect pause, cancel-all, or clearing completed entries.

The pending summary counts all queued and running jobs, regardless of the selected filter, and shows the known bytes remaining after subtracting transferred bytes. Upload sizes come from local file metadata; download sizes initially come from the selected remote listing and update during streaming. A known empty file contributes zero bytes. Unknown sizes are identified separately rather than reported as empty files. Counts and sizes update as jobs progress or finish.

## Failure details and completed-file shortcuts

Use the information button on a failed row to open its details. The native dialog identifies the file, direction, VPS, source and destination, explains the failure, and suggests an action. Permission failures, missing paths, destination conflicts, unavailable SFTP, connection loss, unsafe names, unsupported atomic replacement, and common local I/O failures have specific guidance.

User-facing reasons and diagnostic messages are selected from defined error categories. Upstream exception messages, inner exceptions, passwords, passphrases, and key contents are never copied into the details or logs. Unknown failures receive a generic explanation. The source and destination paths remain visible so the user can identify the affected operation.

Completed downloads provide a direct shortcut to select the downloaded file in Windows File Explorer. Completed uploads provide a shortcut to their local source folder. The additional-actions menu offers opening the local folder and copying the exact remote path; downloaded files can also be selected from that menu. These actions do not reconnect to a VPS or execute a remote command. Missing or inaccessible local files produce a safe error rather than opening a different path.

## Progress and cancellation

The file workspace and Transfers page show the same queue: filename, server, direction, transferred and total bytes, percentage, speed, estimated remaining time, and status. Hover over the remaining-time value to see elapsed time. Jobs are queued, running, completed, failed, or canceled. One file runs at a time; the queue is kept in memory and is not restored after closing the application.

Speed uses recent byte measurements and refreshes during periods without progress. The remaining-time estimate needs enough data to calculate a rate and changes as throughput changes. Empty files, insufficient samples, stalled transfers, finalization, and finished jobs show no remaining-time estimate. Elapsed time starts when processing the job begins, excludes its time waiting in the queue, and stops when the job finishes.

Cancel can stop a queued job or the streaming phase of a running job. Progress stays below 100 percent until destination finalization succeeds, including for empty files. Finalization is a short commit boundary: a cancellation arriving after it begins cannot reverse a completed replacement. The interface disables cancellation when it knows finalization has started and reports the actual committed outcome.

Disconnecting or losing the VPS connection cancels pending operations and clears remote session state. Queue rows retain their outcomes. Choosing another saved profile does not change the server or paths of a queued or running transfer. If a canceled operation makes the SFTP channel unusable, the error explains that reconnection is required.

## Queue controls and retries

- **Pause queue** prevents the next file from starting. A transfer already running continues to completion. Files can still be added or canceled while the queue is paused. Resume the queue to process pending files in order.
- **Cancel all** cancels pending files and requests cancellation of the current transfer. The same atomic-finalization boundary applies: a destination already being committed cannot be rolled back by a late cancellation.
- **Clear completed** removes successful entries from the list. It does not remove local or remote files. Failed and canceled entries remain available for inspection or retry.
- **Retry** creates a new full transfer attempt for a failed or canceled entry, preserving its original source, destination, direction, and VPS settings. The previous entry retains its result. Retry restarts from the beginning and does not resume a partial file.

Retry requires an active connection matching the original hostname, port, username, protocol, and private-key settings. Reconnecting to the same VPS permits a manual retry; selecting another profile cannot redirect it. A duplicate queued or running attempt for the same paths prevents another retry from starting. After an error requiring reconnection, reconnect before retrying.

Each retry checks the destination again and asks for overwrite confirmation if needed. Changes to the local or remote source are read by the new attempt; missing or inaccessible sources fail without replacing the existing destination. Disconnecting clears the pause state, and the application does not automatically retry old entries.

## Destination safety and temporary files

Uploads and downloads stream through bounded buffers into a uniquely named temporary file in the destination directory. Existing destination contents are preserved until successful finalization. Failed or canceled transfers attempt to remove only the temporary file created by that operation.

Downloads finalize with a local filesystem move. Uploads without overwrite use a no-overwrite SFTP rename. Upload replacements require the server's atomic POSIX rename extension; if it is unavailable, the transfer fails while preserving the existing destination. The application never deletes the old destination as a replacement strategy.

A lost connection or a server-side permission change can prevent removal of a remote temporary file. Such files may require later cleanup on the server. The failed job remains unsuccessful even if some or all of its bytes were streamed. Remote deletion outside this internal cleanup is not exposed in the application.

If the connection disappears after the server commits a rename but before acknowledging it, the final remote outcome cannot be determined reliably. The job reports failure or connection loss rather than claiming success. Inspect the destination after reconnecting before retrying an overwrite.

## Verification

```powershell
./scripts/Verify-OhMySsh.ps1
./scripts/Verify-Ssh.ps1
```

The first command runs WPF regressions and captures both themes. The second uses the isolated loopback SSH/SFTP fixture, with file fixtures under `.local`. For first-time fixture dependency setup, use `./scripts/Verify-Ssh.ps1 -InstallFixtureDependencies`.

Verification covers browsing, Unicode and spaces, empty files and directories, content comparison, bounded streaming, permission and missing-path errors, overwrite decisions, cancellation, partial-file cleanup, disconnection, and stale session results. Desktop checks also cover speed and time estimates with a controlled monotonic clock, queue controls, retry destination safety, typed failure details, completed-file shortcuts, filters and pending totals, multi-file selection and drops, and both visual themes. Shell actions are recorded by QA doubles; verification does not launch File Explorer or access user files. No real VPS or user SSH configuration is used.

## Out of scope

Recursive transfers, resumable transfers, directory synchronization, remote deletion, permission editing, FTP/FTPS, terminal emulation, automation workflows, and persistent transfer history.
