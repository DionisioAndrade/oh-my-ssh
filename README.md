<p align="center">
  <img src="docs/assets/oh-my-ssh-banner.png" alt="oh my ssh — pixel art terminal with colorful lettering and connected servers" width="900" />
</p>

<h1 align="center">oh-my-ssh</h1>

<p align="center">
  <strong>Your servers. Your connections. All in one place.</strong>
</p>

<p align="center">
  A desktop app for organizing your servers and connection profiles.
</p>

<p align="center">
  <code>C#</code> &nbsp;·&nbsp; <code>.NET 10</code> &nbsp;·&nbsp; <code>WPF</code> &nbsp;·&nbsp; <code>Windows</code>
</p>

<p align="center">
  <a href="docs/architecture.md">Architecture</a> &nbsp;·&nbsp;
  <a href="LICENSE">Apache 2.0 License</a>
</p>

## Where does the name come from?

The name **oh-my-ssh** and the banner pay homage to **[Oh My Zsh](https://ohmyz.sh/)**.

## About the project

The project is under development.

## SSH connections

Connect through the manual form or a saved VPS profile using password or private-key authentication. Encrypted private keys request a passphrase in a native dialog. The application verifies server fingerprints before accepting a connection and returns to the connection screen if the session drops.

Credentials remain separate from profile JSON. Saved profiles can optionally remember credentials in Windows Credential Manager; manual connections keep credentials temporary. Connection timeouts, cancellation, disconnection, and application shutdown release the SSH transport.

See [SSH setup and verification](docs/ssh-connections.md) for connection instructions, server trust, tested key formats, and local integration tests.

## SFTP files and transfers

Browse the connected server's directories and upload or download individual files. The remote panel supports parent navigation, entered paths, refresh, Unicode filenames, sizes, and modification dates. A serial transfer queue displays progress, transfer speed, and estimated remaining time. Pause upcoming work, cancel individual files or the whole queue, retry failed or canceled transfers, and clear successful entries.

Transfers use temporary files and safe finalization to preserve existing destinations on failure or cancellation. Replacements require confirmation; upload replacement also requires the server's atomic rename support. See [SFTP transfers](docs/sftp-transfers.md) for behavior, cancellation limits, and verification.

One VPS session is supported at a time. An interactive terminal, FTP/FTPS, persistent transfer history, recursive transfers, and automations are subsequent milestones.
