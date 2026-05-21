# Custodian Disk Analyzer

Custodian is a Windows disk usage analyzer built for server-friendly use cases where third-party tools are blocked. It ships as a self-updating desktop app, a portable desktop app, a CLI, and an optional legacy EXE installer.

## Features

- Recursive scanner for local folders, removable drives, and UNC/network shares.
- Optional NTFS MFT scanner for local NTFS volumes when Windows allows raw volume enumeration; it falls back safely through the app's Auto mode.
- Explorer-style WPF UI with folder tree, sortable grids, largest files/folders, extension summaries, treemap blocks, save/load, CSV/JSON export, and Recycle Bin deletes with confirmation.
- Desktop auto-updates through GitHub Releases with manual checks from Help > Check for Updates and a startup notification when a new stable update is available.
- CLI for scheduled/server automation.
- Portable `.custodian-scan` SQLite save files.

## CLI

```powershell
custodian scan C:\Data --out data.custodian-scan
custodian scan \\server\share --export share.csv --silent
custodian export data.custodian-scan --format json --out data.json
```

## Build

```powershell
dotnet restore
dotnet build
dotnet test
.\scripts\publish-portable.ps1
```

The portable output is written under `artifacts\portable\Custodian`.

## Updates

The primary desktop installer/update package is built with Velopack:

```powershell
.\scripts\publish-velopack.ps1 -Version 1.0.0
```

Release assets are written under `artifacts\velopack`. Publish those assets to GitHub Releases so installed apps can discover updates:

```powershell
$env:GITHUB_TOKEN = "<token with release access>"
.\scripts\upload-velopack-github.ps1 -Publish
```

The repo pins `vpk` 0.0.626 because newer tool builds require a .NET 9 runtime that is not installed in the current development environment.
The app follows the update channel embedded in the installed Velopack package. The scripts default to the `win` channel; pass `-Channel <name>` when producing or uploading another channel, or set `CUSTODIAN_UPDATE_CHANNEL` only for deliberate local/managed overrides.
Automatic startup checks are throttled. Managed deployments that need authenticated GitHub release checks can set `CUSTODIAN_GITHUB_TOKEN` in the user or machine environment instead of embedding a token in the app.
The packaging script cleans `artifacts\velopack` by default for repeatable local builds; pass `-PreserveExistingReleases` when retaining previous release assets for delta generation.
For local update validation without GitHub Releases, run `scripts\prepare-local-update-test.ps1`, install the preserved baseline setup, then set `CUSTODIAN_UPDATE_SOURCE` to the generated local release folder before launching the installed test build.

## Installer

The installer script is `installer\Custodian.iss` and targets Inno Setup 6. It packages the portable publish output and creates Start Menu shortcuts for the GUI and CLI. This path remains available for manual or legacy installs, but it is not the auto-update channel.
