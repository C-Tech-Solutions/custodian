# Custodian — Contributor & Agent Guide

Windows disk-usage analyzer for locked-down server/enterprise environments. .NET 10, WPF GUI + terminal UI + reusable Core library + console CLI. User-facing docs are in `README.md`; this file is the engineering map.

## Layout

- `src/Custodian.Core/` — all scanning, analysis, and data logic. **No UI, no Velopack.** Well-factored and well-tested; keep it that way.
  - `Scanning/` — `DiskScanner` (orchestrator), `RecursiveScanProvider`, `MftScanProvider` (+ NTFS MFT parser). Auto mode tries MFT on volume roots, falls back to recursive.
  - `Model/` — `FileSystemEntry` (tree node; `Children` is **get-only** — see JSON note below), `ScanResult`, `SkippedEntry`, `ScanGlobalIndex`.
  - `Presentation/` — `ScanViewProjector`, `RecycleBinViewProjector`, row/dataset DTOs the UI binds to.
  - `Analysis/` — largest files/folders, extension summaries, `ScanGlobalIndexBuilder` (precomputed views).
  - `Storage/ScanStore.cs` — `.custodian-scan` save/load (SQLite). **This is the only load format.**
  - `Export/ScanExporter.cs` — CSV / JSON. **Export only** — never read back by the app.
- `src/Custodian.App/` — WPF GUI. The composition root is `Program.cs` (custom `[STAThread] Main`, set via `<StartupObject>`).
  - `MainWindow.xaml.cs` is a large monolith (~2.6k lines). **Do not add new logic here** — add a ViewModel or a `Services/` class and bind to it. Incrementally extracting from this file is ongoing work.
  - `Services/` — WPF-specific orchestration such as `ThemeManager` and `WhatsNewMenuService`. Shared shell/update/portable-device logic belongs in `Custodian.Platform.Windows`.
- `src/Custodian.Platform.Windows/` — Windows-only services shared by the WPF app and TUI.
  - `Services/` — Velopack updates, elevation settings, portable-device discovery/explorer integration, Recycle Bin operations, and UI settings persistence.
  - `Logging/` — shared rolling file logging; see below.
  - Types are internal and exposed to `Custodian.App`, `Custodian.Tui`, and `Custodian.Tests` through `InternalsVisibleTo`; do not broaden this API surface unless the assembly is intentionally becoming a public package.
- `src/Custodian.Tui/` — Terminal.Gui interface for interactive terminal use. Keep terminal view glue here, but move reusable Windows integration into `Custodian.Platform.Windows` and reusable scan/presentation logic into `Custodian.Core`.
- `src/Custodian.Cli/` — `scan` / `export` commands for automation.
- `tests/Custodian.Tests/` — xUnit. Covers Core (analysis, projectors, MFT parser, storage roundtrip, export), shared Windows helpers where practical, and focused TUI helpers. Full UI flows remain manual.

## Build / test

```powershell
dotnet build Custodian.slnx     # zero-warning build is the expectation
dotnet test                     # all green; add tests with new Core behavior
```

## Conventions

- **Logging** (`src/Custodian.Platform.Windows/Logging/`): use `AppLogging.CreateLogger<T>()` / `CreateLogger(category)` to get an `ILogger`. It writes rolling files to `%LOCALAPPDATA%\Custodian\logs\`. `Program.cs` calls `AppLogging.Initialize()` / `Shutdown()`. **Do not use `Debug.WriteLine` or ad-hoc file logging** — every `catch` should either log (`LogError`/`LogWarning`) or funnel through a helper that logs (e.g. `MainWindow.ShowOperationError`). Before init (or in tests) the logger is a no-op, so it's always safe to call.
- **`ScanStore` connections use `Pooling=False`** intentionally — pooled SQLite connections hold the file handle and break save-overwrite (`File.Delete`)/rename. Don't remove it.
- **JSON export is lossy on read-back**: `FileSystemEntry.Children` is get-only, so `System.Text.Json` serializes the tree out but cannot deserialize it. This is fine because the app never loads JSON (it loads `.custodian-scan`). Don't write tests that assume JSON deserialize fidelity.
- **Velopack is pinned to `0.0.626`** (and `vpk` likewise) because newer builds need a .NET 9 runtime not present in the dev/deploy environment. Don't bump it casually — see `README.md` Updates section.
- LocalAppData root for app state is `%LOCALAPPDATA%\Custodian\` (`ui.json`, `logs\`).
