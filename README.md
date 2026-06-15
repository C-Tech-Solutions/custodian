# Custodian Disk Analyzer

[![Latest release](https://img.shields.io/github/v/release/ctech1313/custodian?label=release)](https://github.com/ctech1313/custodian/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

Custodian is a Windows disk usage analyzer for machines where you need fast,
local inspection without depending on third-party desktop utilities. It ships as
a self-updating desktop app, a portable desktop app, and a CLI for repeatable
server or workstation workflows.

## Why Custodian?

- **Find space quickly** with folder trees, sortable detail grids, largest-file
  and largest-folder views, extension summaries, treemaps, pie charts, and bar
  charts.
- **Switch between drives without losing context** with a session scan cache
  that restores recent scan results, selected folder, detail mode, and chart
  scope while Custodian stays open.
- **Choose the right scanner** with Auto mode, recursive scanning for folders
  and network shares, and optional NTFS MFT scanning for local NTFS volumes.
- **Run in locked-down environments** with a portable build, a CLI, UNC path
  support, safe fallbacks when raw NTFS access is unavailable, and an optional
  always-open-as-administrator launch setting.
- **Review before deleting** with Recycle Bin management, restore, permanent
  delete, empty-bin actions, and confirmation prompts.
- **Keep results portable** with `.custodian-scan` SQLite save files plus CSV
  and JSON export from the desktop app or CLI.
- **Stay updated** through Velopack-powered GitHub Releases for installed
  desktop builds.

## Download

Get the latest release from:

https://github.com/ctech1313/custodian/releases/latest

Release assets include:

- `Custodian.DiskAnalyzer-win-Setup.exe` - installed desktop app with updates.
- `Custodian.DiskAnalyzer-win-Portable.zip` - portable desktop app and CLI.
- `Custodian.DiskAnalyzer-<version>-full.nupkg`, `RELEASES`, and
  `releases.win.json` - Velopack update assets for installed clients.

## Requirements

- Windows 10, Windows 11, or Windows Server with Desktop Experience for the WPF
  desktop app.
- Administrator launch is recommended for full local NTFS MFT access.
- The CLI can run in more constrained server workflows, including scheduled
  jobs, recursive scans, and export tasks.

## Desktop Workflow

1. Choose a drive, folder, or UNC path.
2. Pick Auto, Recursive, or MFT scanning.
3. Scan and inspect space by folder, file, extension, or chart slice.
4. Switch to another drive and return to any cached scan without rescanning.
5. Save the scan for later, export CSV/JSON, or manage deleted items through the
   Recycle Bin view.

Custodian warns when it is not running as administrator and can relaunch itself
elevated when you need MFT scanning to reach protected NTFS metadata.
Use View > Always open as administrator when you want Windows to request
elevation before future launches instead of starting normally and relaunching.

Drive targets show whether a scan is currently running or already cached for
the session. Uncached drives show a Start Scan prompt instead of clearing the
workspace without direction.

## CLI Examples

```powershell
custodian scan C:\Data --out data.custodian-scan
custodian scan \\server\share --export share.csv --silent
custodian export data.custodian-scan --format json --out data.json
```

## Build From Source

```powershell
dotnet restore
dotnet build
dotnet test
.\scripts\publish-portable.ps1
```

The portable output is written to `artifacts\portable\Custodian`.

## Release Packaging

The primary installer and update channel are built with Velopack:

```powershell
.\scripts\publish-velopack.ps1 -Version 1.2.0
```

Release assets are written under `artifacts\velopack`. Publish those assets to
GitHub Releases so installed apps can discover updates:

```powershell
$env:GITHUB_TOKEN = "<token with release access>"
.\scripts\upload-velopack-github.ps1 -Publish
```

The repo pins `vpk` 0.0.626 because newer tool builds require a .NET runtime
that may not be installed in the current development environment.

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for release notes.

## Update Configuration

Custodian follows the update channel embedded in the installed Velopack package.
The scripts default to the `win` channel; pass `-Channel <name>` when producing
or uploading another channel.

Installed apps check for updates on startup by default. Use Help > Automatically
download updates on startup to turn the launch-time check/download on or off;
Custodian still asks before restarting to install a downloaded update.

Optional environment overrides:

- `CUSTODIAN_UPDATE_CHANNEL` - deliberate local or managed channel override.
- `CUSTODIAN_GITHUB_TOKEN` - token for authenticated GitHub release checks in
  managed deployments.
- `CUSTODIAN_UPDATE_PRERELEASES` - set to `0` or `1` to override prerelease
  checks for non-stable channels.
- `CUSTODIAN_UPDATE_SOURCE` - local release folder for unsigned update testing.

For local update validation without GitHub Releases, run
`scripts\prepare-local-update-test.ps1`, install the preserved baseline setup,
set `CUSTODIAN_UPDATE_SOURCE` to the generated local release folder, and launch
the installed test build.

## Legacy Installer

The Inno Setup script at `installer\Custodian.iss` packages the portable publish
output and creates Start Menu shortcuts for the GUI and CLI. This path remains
available for manual or legacy installs, but it is not the auto-update channel.
