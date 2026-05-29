# Custodian — Contributor & Agent Guide

Windows disk-usage analyzer for locked-down server/enterprise environments. .NET 10, WPF GUI + reusable Core library + console CLI. User-facing docs are in `README.md`; this file is the engineering map.

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
  - `Services/` — `RecycleBinService` (shell COM), `ThemeManager`, `UiSettingsStore`. The last two are **static with mutable static state** (XAML/`Application.Current.Resources` coupled) — leave them static for now.
  - `Logging/` — see below.
- `src/Custodian.Cli/` — `scan` / `export` commands for automation.
- `tests/Custodian.Tests/` — xUnit. Covers Core (analysis, projectors, MFT parser, storage roundtrip, export). No UI tests yet.

## Build / test

```powershell
dotnet build Custodian.slnx     # zero-warning build is the expectation
dotnet test                     # all green; add tests with new Core behavior
```

## Conventions

- **Logging** (`src/Custodian.App/Logging/`): use `AppLogging.CreateLogger<T>()` / `CreateLogger(category)` to get an `ILogger`. It writes rolling files to `%LOCALAPPDATA%\Custodian\logs\`. `Program.cs` calls `AppLogging.Initialize()` / `Shutdown()`. **Do not use `Debug.WriteLine` or ad-hoc file logging** — every `catch` should either log (`LogError`/`LogWarning`) or funnel through a helper that logs (e.g. `MainWindow.ShowOperationError`). Before init (or in tests) the logger is a no-op, so it's always safe to call.
- **`ScanStore` connections use `Pooling=False`** intentionally — pooled SQLite connections hold the file handle and break save-overwrite (`File.Delete`)/rename. Don't remove it.
- **JSON export is lossy on read-back**: `FileSystemEntry.Children` is get-only, so `System.Text.Json` serializes the tree out but cannot deserialize it. This is fine because the app never loads JSON (it loads `.custodian-scan`). Don't write tests that assume JSON deserialize fidelity.
- **Velopack is pinned to `0.0.626`** (and `vpk` likewise) because newer builds need a .NET 9 runtime not present in the dev/deploy environment. Don't bump it casually — see `README.md` Updates section.
- LocalAppData root for app state is `%LOCALAPPDATA%\Custodian\` (`ui.json`, `logs\`).
