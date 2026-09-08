# Development and releases

[Back to Vigilo](../README.md) · [Current architecture](../DESIGN.md)

## Developer setup

### Prerequisites

- Windows.
- .NET SDK capable of building `net10.0` and `net10.0-windows` projects.
- Visual Studio 2026 or another IDE/editor with WPF and .NET desktop workload support.
- An IMAP-enabled email account and app password for real mailbox testing.
- Gmail accounts require a Google app password for IMAP authentication; the regular Google account password is rejected.
- Enough disk space for the ONNX model files if you use local classification.

### Restore, build, and test

From the repository root:

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\BuildAndTest.ps1
```

These entry points use a repository-specific named mutex to prevent concurrent
local processes from writing the same build outputs or using the same test
resources. The mutex coordinates processes across Codex tasks and terminals on
the same Windows machine. It does not isolate concurrent source edits, and CI
jobs in separate checkouts are unaffected. The coordinator explicitly bypasses
the mutex when GitHub Actions sets `GITHUB_ACTIONS=true`, so release packaging
and other automated workflow steps cannot wait on a local-development lock.
`BuildAndTest.ps1` holds one lock across both phases. To run any other validation
command under the same lock, use
`.\scripts\Invoke-ExclusiveBuild.ps1 <command> <arguments>`. The lock helper
itself has Pester coverage under `scripts\tests`.

Run the app:

```powershell
.\scripts\Invoke-ExclusiveBuild.ps1 dotnet run --project .\src\Vigilo.App\Vigilo.App.csproj
```

Create the same self-contained Windows packages and CycloneDX SBOM produced by
the release workflow:

```powershell
.\build\package.ps1 -Version 0.6.2 -Configuration Release -Runtime win-x64
```

The script performs a self-contained, untrimmed, multi-file publish once under
`artifacts\publish\win-x64`, then uses that same directory for both the WiX
installer and portable archive. It rejects models, symbols, installer
intermediates, and partial downloads in the publish tree. The public outputs
are:

```text
artifacts\Vigilo-Setup-0.6.2-x64.exe
artifacts\Vigilo-Portable-0.6.2-x64.zip
artifacts\SHA256SUMS.txt
artifacts\Vigilo-0.6.2-win-x64.cdx.json
```

`-Version` accepts exactly three numeric components and enforces Windows
Installer version limits. To exercise the tag-derived path used in CI, pass
`-Tag v0.6.2` instead. Generated publish directories, the internal MSI, WiX
intermediates, and final packages remain under the ignored `artifacts`
directory and must not be committed.

NuGet lock files for the application and test dependency roots are committed to the repository. Locked mode is enforced locally, in CI, and during packaging, so an ordinary restore cannot rewrite them. Intentional dependency or runtime-graph changes must use `.\scripts\Invoke-ExclusiveBuild.ps1 dotnet restore --force-evaluate` and include a review of the affected `packages.lock.json` file.

### Software Bill of Materials (SBOM)

Vigilo publishes a CycloneDX 1.7 JSON SBOM with every Windows release. The SBOM describes the `win-x64` application dependency graph, including direct and transitive NuGet dependencies and referenced Vigilo projects. Generation fails if expected runtime components such as MailKit, Microsoft Entity Framework Core SQLite, or Microsoft ONNX Runtime GenAI are absent, preventing an incomplete dependency graph from being published silently.

The SBOM is distributed as a separate `.cdx.json` asset so consumers can
inspect or scan it without extracting either package. `SHA256SUMS.txt` contains
exactly the setup EXE and portable ZIP because those are the downloadable
application binaries; the workflow verifies both hashes again after moving
the build outputs between jobs. The SBOM application version comes from the
release tag after removing the required leading `v`.

AI model files are not included in the release SBOM because Phi models are downloaded after installation and LiteRT-LM models are installed separately; they are not contents of the Vigilo release packages. For public repositories, GitHub Actions cryptographically attests both the setup EXE and portable ZIP against the SBOM. The attestation step is skipped for private repositories where GitHub does not make that feature available.

### Publish a GitHub release

The existing workflow in `.github\workflows\release.yml` runs when a tag whose
name starts with `v` is pushed, then rejects any tag that is not exactly
`v<major>.<minor>.<patch>`. It uses two jobs:

1. **Build** checks out the tagged commit, restores the locked solution, builds
   and tests in Release, and runs `build\package.ps1`. The resulting installer,
   portable ZIP, checksum manifest, and SBOM are retained as a workflow
   artifact for diagnostics.
2. **Publish** downloads that artifact into a clean runner, independently
   verifies `SHA256SUMS.txt`, creates public SBOM attestations when supported,
   and attaches the files to the GitHub Release. Only this job receives
   `contents: write`, `id-token: write`, and `attestations: write` permissions.

The application publish targets .NET 10 and `win-x64` in self-contained,
multi-file mode with `PublishSingleFile=false`, `PublishTrimmed=false`, and no
debug symbols. The .NET runtime and required native dependencies are included;
the AI model is not.

Create and push an annotated version tag from the commit to release:

```powershell
git tag -a v0.6.2 -m "Vigilo 0.6.2"
git push origin v0.6.2
```

The workflow creates a GitHub Release with generated release notes and attaches:

```text
Vigilo-Setup-0.6.2-x64.exe
Vigilo-Portable-0.6.2-x64.zip
SHA256SUMS.txt
Vigilo-0.6.2-win-x64.cdx.json
```

The tag is the official release-version source. The workflow removes the
leading `v` and uses the resulting version consistently in .NET metadata, WiX
metadata, the SBOM, and artifact filenames.

If a tag-triggered run fails before a GitHub Release is created, first commit
and push the fix to the release branch. Delete the failed tag locally and from
the remote, recreate it on the corrected commit, and push it again:

```powershell
git tag -d v0.6.2
git push origin :refs/tags/v0.6.2
git tag -a v0.6.2 -m "Vigilo 0.6.2"
git push origin v0.6.2
```

Do not move or reuse a tag after its GitHub Release has been published. Make a
new patch release instead. Rerunning an old workflow run does not incorporate
workflow changes committed after its tag because reruns use the original
tagged commit.

The GitHub Release assets are the intended end-user downloads. Both release
packages are deliberately unsigned and require no signing credentials, so
Windows may show a SmartScreen warning when a user downloads or launches them.

## License and contributions

Vigilo is distributed under the GNU General Public License v3.0; see
[`LICENSE`](../LICENSE) at the repository root. Contributions are licensed to
the project and its users under GPL-3.0 as well, so no separate contribution
agreement is required for a patch to land. Releases already published remain
under GPL-3.0 permanently; the license cannot be revoked for them after the
fact.
