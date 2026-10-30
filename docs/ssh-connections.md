# SSH connections

## Requirements

Run oh-my-ssh on Windows with the .NET 10 desktop runtime, or install the .NET 10 SDK to build from source. The remote server must offer SSH version 2 and allow the chosen account to authenticate with a password or private key.

```powershell
./scripts/Start-OhMySsh.ps1
```

If the application is already running, this script restores its existing window without rebuilding or disconnecting its VPS session. Close the application before rerunning the script to compile and load source changes. When no instance is running, the script builds the selected configuration, opens the native application, and waits briefly for its activation channel. Concurrent launcher scripts share a separate launch gate so they do not rebuild over a starting instance. An unready instance produces a bounded retry message rather than being stopped.

## Connect to a VPS

1. Enter a hostname, SSH port, username, and protocol in the manual form, or choose a saved VPS.
2. For private-key authentication, set the local private-key path in the advanced connection settings. An empty path selects password authentication.
3. Set the connection timeout in the advanced settings if needed. The default is 30 seconds; valid values are 1–300 seconds. The transport deadline starts after credential entry and includes the host-verification dialog.
4. Select Connect and provide the requested VPS password or private-key password (passphrase). The private-key password protects the key file; leave it empty for an unencrypted key.
5. For an unknown server, compare its SHA-256 host fingerprint with a value obtained from the server administrator through a trusted channel. Select Trust once, Trust and remember, or Cancel.
6. The VPS dashboard opens after authentication succeeds. Disconnect returns to the connection screen and preserves saved profiles.

Cancel stops an in-progress attempt. Failed attempts leave the form available for retry. Unexpected connection loss returns to the connection screen; reconnecting always requires a new explicit action.

Some SSH.NET handshake and private-key decoding stages use synchronous library work. Cancellation releases the connection interface promptly; any stage already executing is tracked until it returns, and its late result is discarded. Application shutdown waits for that cleanup. The configured timeout is not a guarantee of interrupting an expensive private-key decoding operation in the middle of its computation.

## Credentials

Saved profiles offer an explicit option to remember credentials in Windows Credential Manager. Credentials are saved only after authentication succeeds. Manual connections cannot remember credentials; saving a profile is a separate action.

Profile JSON contains non-secret connection settings and the private-key path. Secrets collected through the authentication dialog and private-key contents are never written to that JSON file. Credential entries are scoped to the profile's connection settings. Editing the host, port, username, or key configuration does not reuse a credential for the old configuration. Failed cached credentials are invalidated before retry.

The private-key field is a file path, not a password field. Leave it empty for password authentication. The authentication dialog does not echo this metadata or put it in a tooltip. If a password was previously entered into the key-path field, edit the saved profile to remove that value and enter the password in the protected authentication prompt instead. Existing profile metadata is not automatically reinterpreted, migrated, or deleted.

### Reveal and copy

The authentication dialog starts with a masked VPS password or private-key password. **Show** and **Copy password** (or **Copy key password**) require a fresh native Windows account-password check for each action. Only credentials validated for the same Windows user running the application authorize disclosure. Canceling, using another account, a failed password, an unsupported credential type, or an inactive/closed dialog leaves the secret hidden and does not copy it. This check protects disclosure; SSH connection and opt-in vault reuse keep their existing behavior.

The check uses the Windows account password. Windows Hello PIN/biometric and passwordless-only accounts are not supported by this password-verification path; unavailable verification fails closed. The Windows password is used only for native local verification and is not stored by the application or sent to the VPS.

For a local Windows account, the native prompt fixes the username to `COMPUTER\user` and requests that account's local Windows password. The application validates it against the computer's local account database, then requires the authenticated token's SID to match the user running the application. A Microsoft account password or SSH/VPS password does not substitute for this local account password.

Revealed text hides after 15 seconds, on focus loss, when the secret changes, or when connecting, canceling, or closing. The temporary display has no undo history or built-in selection/copy menu; use the guarded copy button. Explicit secret copies opt out of Windows clipboard history/cloud upload and schedule owned-entry cleanup after 30 seconds, retrying when the clipboard is busy. Cleanup is armed before publishing, including when Windows reports an error after partially publishing the copy. Earlier pending entries keep their own cleanup deadlines if a later copy fails. A later unrelated clipboard entry is preserved. Cleanup is best effort; other software can still read the current clipboard while its authorized secret entry exists, and application/native immutable string copies cannot be guaranteed to be erased from process memory.

**Copy IP** copies the configured server address, including a hostname or IPv6 address, without the SSH username or port. It requires no Windows authentication and performs no DNS lookup. The disclosure controls operate on the password/passphrase entered in the current dialog; they do not read a private-key path as a secret or provide a new saved-vault inspection interface.

The application does not load an SSH agent, OpenSSH configuration files, or the user's OpenSSH known_hosts file. This version supports password and private-key authentication; keyboard-interactive challenges and multi-factor authentication are outside this delivery.

## Local storage

| Data | Location |
| --- | --- |
| Saved, non-secret profiles | `%LocalAppData%\OhMySsh\connections.json` |
| Trusted server identities | `%LocalAppData%\OhMySsh\known-hosts.json` |
| Interface language preference | `%LocalAppData%\OhMySsh\settings.json` |
| Application diagnostics | `%LocalAppData%\OhMySsh\logs\application.log` |
| Opt-in saved credentials | Windows Credential Manager, scoped to the profile and connection settings |

## Server identities

Trust once applies to the current attempt. Trust and remember stores the server identity separately from credentials and profiles in the application-local data folder. A known fingerprint must match on subsequent attempts.

Changed host keys are rejected. Confirm a legitimate rotation with the server administrator before deliberately removing the old endpoint entry from the application's known-host file while the app is closed. Do not clear trust records simply to dismiss an unexpected warning. A malformed trust file must be repaired before connecting.

## Key formats

The integration fixture exercises unencrypted OpenSSH Ed25519, passphrase-encrypted OpenSSH Ed25519, and unencrypted RSA PEM private keys. SSH.NET supports additional formats, including PKCS#8 and PuTTY keys; these are not all covered by the fixture. See the [upstream format list](https://www.nuget.org/packages/SSH.NET/2026.0.0) for the library's capabilities.

Missing or unreadable files, malformed keys, and incorrect passphrases produce an actionable error. Key files remain on disk at the user-selected path and are never imported into profile storage.

## Logs and limitations

Application-generated connection events, errors, and diagnostics use English. Diagnostic messages avoid raw transport exception details, authentication secrets, and terminal contents. The interface supports English, Brazilian Portuguese, and Spanish.

Only one active VPS session is supported. SSH/SFTP profiles establish SSH transport and can use [SFTP browsing and transfers](sftp-transfers.md) when the server provides that subsystem, plus the [interactive VT/ANSI terminal](terminal.md) through a remote PTY. FTP/FTPS profiles remain unsupported by this adapter.

## Verify locally

Run the WPF regression checks:

```powershell
./scripts/Verify-OhMySsh.ps1
```

Run the real SSH integration checks with Python 3.10 or later. The first run can install the pinned, test-only AsyncSSH dependency into a project-local virtual environment:

```powershell
./scripts/Verify-Ssh.ps1 -InstallFixtureDependencies
```

Later runs reuse that environment:

```powershell
./scripts/Verify-Ssh.ps1
```

The integration runner starts a controlled SSH/SFTP server bound to loopback and generates temporary credentials, keys, and files under `.local`. It checks successful authentication, key/passphrase handling, host verification, cancellation, timeout, disconnection, retry, resource cleanup, and isolated credential persistence, together with the [SFTP transfer scenarios](sftp-transfers.md#verification). It does not modify the user's SSH configuration or connect to a real VPS. Generated fixture credentials are test data and must not be reused on servers.
