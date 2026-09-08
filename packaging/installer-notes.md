# Vigilo Windows packaging

Run the release packager from the repository root:

```powershell
.\build\package.ps1 -Version 0.1.0
```

For CI or an exact release tag, use `-Tag v0.1.0`. The script rejects tags
outside the `v<major>.<minor>.<patch>` format and versions that Windows
Installer cannot represent safely.

The script restores locked application dependencies, publishes the app once,
and uses that same self-contained, multi-file `win-x64` directory for both
WiX and the portable ZIP. It produces:

```text
artifacts\Vigilo-Setup-0.1.0-x64.exe
artifacts\Vigilo-Portable-0.1.0-x64.zip
artifacts\SHA256SUMS.txt
artifacts\Vigilo-0.1.0-win-x64.cdx.json
```

The WiX 5 bundle contains an internal MSI. The MSI is intentionally an
intermediate rather than a release asset. Its stable package and bundle
upgrade codes provide major upgrades and clean downgrade rejection.

## Installer behavior

- Installation is per-user under `%LOCALAPPDATA%\Programs\Vigilo`; elevation
  is not required.
- Application data, configuration, logs, runtime data, and models remain under
  `%LOCALAPPDATA%\Vigilo`, outside the installation directory. Upgrades and
  uninstall therefore preserve them.
- The installer creates a Start menu shortcut and a normal uninstall entry.
- The default-enabled startup checkbox owns only the `Vigilo` value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
- The startup command is `"<installed path>\Vigilo.App.exe" --background`.
  The runtime itself is never registered for Windows startup.
- AppSearch reads the existing Run value before an upgrade. A missing value on
  upgrade remains disabled; a present value remains enabled. The component and
  registry identity are stable, and uninstall removes only that value.
- The portable archive performs no installation and never changes autostart.

WiX Toolset 5.0.2 is pinned because it is the last major release before the
Open Source Maintenance Fee introduced in WiX 6. WiX 7 also requires an
explicit EULA-acceptance gesture, which a repository build must not make on a
maintainer's behalf.

## Verification

`SHA256SUMS.txt` contains exactly the setup EXE and portable ZIP. The packaging
script recomputes both hashes before it succeeds. It also rejects publish
output containing model files, symbols, installer files, temporary downloads,
or partial files.

The release workflow uploads the installer, portable archive, checksums, and
CycloneDX SBOM as a diagnostic artifact. A separate least-privilege job, and
only that job, receives `contents: write` to attach them to the tagged GitHub
Release. Public repositories also receive GitHub SBOM attestations.
