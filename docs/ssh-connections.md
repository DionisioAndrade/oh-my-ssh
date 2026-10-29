# SSH connections

## Requirements

Run oh-my-ssh on Windows with the .NET 10 desktop runtime, or install the .NET 10 SDK to build from source. The remote server must offer SSH version 2 and allow the chosen account to authenticate with a password or private key.

```powershell
./scripts/Start-OhMySsh.ps1
```

## Connect to a VPS

1. Enter a hostname, SSH port, username, and protocol in the manual form, or choose a saved VPS.
2. For private-key authentication, set the local private-key path in the advanced connection settings. An empty path selects password authentication.
3. Set the connection timeout in the advanced settings if needed. The default is 30 seconds; valid values are 1–300 seconds. The transport deadline starts after credential entry and includes the host-verification dialog.
4. Select Connect and provide the requested password or key passphrase. Unencrypted private keys use an empty passphrase.
5. For an unknown server, compare its SHA-256 host fingerprint with a value obtained from the server administrator through a trusted channel. Select Trust once, Trust and remember, or Cancel.
6. The VPS dashboard opens after authentication succeeds. Disconnect returns to the connection screen and preserves saved profiles.

Cancel stops an in-progress attempt. Failed attempts leave the form available for retry. Unexpected connection loss returns to the connection screen; reconnecting always requires a new explicit action.

Some SSH.NET handshake and private-key decoding stages use synchronous library work. Cancellation releases the connection interface promptly; any stage already executing is tracked until it returns, and its late result is discarded. Application shutdown waits for that cleanup. The configured timeout is not a guarantee of interrupting an expensive private-key decoding operation in the middle of its computation.

## Credentials

Saved profiles offer an explicit option to remember credentials in Windows Credential Manager. Credentials are saved only after authentication succeeds. Manual connections cannot remember credentials; saving a profile is a separate action.

Profile JSON contains non-secret connection settings and the private-key path. Private-key contents, passwords, and passphrases are never written to that JSON file. Credential entries are scoped to the profile's connection settings. Editing the host, port, username, or key configuration does not reuse a credential for the old configuration. Failed cached credentials are invalidated before retry.

The application does not load an SSH agent, OpenSSH configuration files, or the user's OpenSSH known_hosts file. This version supports password and private-key authentication; keyboard-interactive challenges and multi-factor authentication are outside this delivery.

## Local storage

| Data | Location |
| --- | --- |
| Saved, non-secret profiles | `%LocalAppData%\OhMySsh\connections.json` |
| Trusted server identities | `%LocalAppData%\OhMySsh\known-hosts.json` |
| Application diagnostics | `%LocalAppData%\OhMySsh\logs\application.log` |
| Opt-in saved credentials | Windows Credential Manager, scoped to the profile and connection settings |

## Server identities

Trust once applies to the current attempt. Trust and remember stores the server identity separately from credentials and profiles in the application-local data folder. A known fingerprint must match on subsequent attempts.

Changed host keys are rejected. Confirm a legitimate rotation with the server administrator before deliberately removing the old endpoint entry from the application's known-host file while the app is closed. Do not clear trust records simply to dismiss an unexpected warning. A malformed trust file must be repaired before connecting.

## Key formats

The integration fixture exercises unencrypted OpenSSH Ed25519, passphrase-encrypted OpenSSH Ed25519, and unencrypted RSA PEM private keys. SSH.NET supports additional formats, including PKCS#8 and PuTTY keys; these are not all covered by the fixture. See the [upstream format list](https://www.nuget.org/packages/SSH.NET/2026.0.0) for the library's capabilities.

Missing or unreadable files, malformed keys, and incorrect passphrases produce an actionable error. Key files remain on disk at the user-selected path and are never imported into profile storage.

## Logs and limitations

Application-generated connection events, errors, and diagnostics use English. Diagnostic messages avoid raw transport exception details and authentication secrets. Existing product navigation labels may remain in Portuguese.

Only one active VPS session is supported. SSH/SFTP profiles establish the SSH transport; SFTP file operations and a VT/ANSI terminal are later tasks. FTP/FTPS profiles remain unsupported by this adapter.

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

The integration runner starts a controlled SSH server bound to loopback and generates temporary credentials and keys under `.local`. It checks successful authentication, key/passphrase handling, host verification, cancellation, timeout, disconnection, retry, resource cleanup, and isolated credential persistence. It does not modify the user's SSH configuration or connect to a real VPS. Generated fixture credentials are test data and must not be reused on servers.
