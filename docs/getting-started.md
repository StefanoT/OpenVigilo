# Getting started

[Back to Vigilo](../README.md) · [Local model setup](local-model-setup.md) · [Classic Outlook setup](outlook.md)

## Windows downloads

Every `v<major>.<minor>.<patch>` GitHub release provides two Windows x64 choices:

- `Vigilo-Setup-<version>-x64.exe` is the recommended per-user installer. It
  creates a Start menu shortcut and offers a default-enabled option to start
  Vigilo when the current user signs in.
- `Vigilo-Portable-<version>-x64.zip` contains the same self-contained,
  multi-file application but does not install or configure autostart.

Both packages are currently unsigned. Windows SmartScreen may show **Windows
protected your PC**. Do not disable Microsoft Defender. You can inspect the
public build in `.github/workflows/release.yml` and verify the download against
`SHA256SUMS.txt` with built-in PowerShell:

```powershell
(Get-FileHash .\Vigilo-Setup-0.6.2-x64.exe -Algorithm SHA256).Hash.ToLowerInvariant()
Get-Content .\SHA256SUMS.txt
```

A matching checksum verifies file integrity but does not suppress SmartScreen
warnings or establish publisher identity.

Neither package embeds an AI model. On first use, Vigilo's existing setup flow
asks before downloading the selected model into `%LOCALAPPDATA%\Vigilo\Models`
and validates completed downloads before they are used. Windows startup
registers only `Vigilo.App.exe --background`: Vigilo keeps the main window
hidden, exposes setup/runtime state from the tray, and coordinates the local
in-process model or configured local server itself. A portable copy can be
started manually with `--background`, but it never registers itself.

## First-run behavior

1. The app creates `%LOCALAPPDATA%\Vigilo`.
2. `AppBootstrapper` checks the SQLite schema version, applies the EF Core baseline migration to an empty database, and validates relational integrity. It stops without applying migrations when the database reports an unknown/newer version or contains tables without migration history.
3. The main window opens from the tray.
4. Settings are initially incomplete, so scans are skipped until at least one account has an email address, IMAP username, and app password saved; IMAP host and port are validated when settings are saved. Open the dedicated Settings window to switch or add accounts, edit the account name, and apply Yahoo, Gmail, iCloud Mail, AOL Mail, Fastmail, Zoho Mail, or custom IMAP defaults. The IMAP username defaults to the email address unless you override it.
5. The model may be missing. In that state, emails can still be ingested, but classification is deferred and retried with backoff; when every automatic attempt is spent, the email is surfaced as a Needs review item instead of idling as Failed.
6. Selecting Download / repair model prompts the user before downloading the configured model files.
