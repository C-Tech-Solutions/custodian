# Custodian Disk Analyzer

Custodian is a Windows disk usage analyzer built for server-friendly use cases where third-party tools are blocked. It ships as a portable desktop app, a CLI, and an optional EXE installer.

## Features

- Recursive scanner for local folders, removable drives, and UNC/network shares.
- Optional NTFS MFT scanner for local NTFS volumes when Windows allows raw volume enumeration; it falls back safely through the app's Auto mode.
- Explorer-style WPF UI with folder tree, sortable grids, largest files/folders, extension summaries, treemap blocks, save/load, CSV/JSON export, and Recycle Bin deletes with confirmation.
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

## Installer

The installer script is `installer\Custodian.iss` and targets Inno Setup 6. It packages the portable publish output and creates Start Menu shortcuts for the GUI and CLI.
