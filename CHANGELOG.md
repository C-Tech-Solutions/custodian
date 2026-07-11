# Changelog

All notable changes to Custodian are tracked here.

## Unreleased

_No unreleased changes._

## 1.5.3 - 2026-07-11

### Security

- Kept whole-package Velopack checksum validation while limiting Authenticode publisher checks to Custodian-owned executables and libraries, allowing signed releases to include normal Microsoft and third-party framework dependencies.

### Fixed

- Corrected WinVerifyTrust action-identifier marshaling and limited publisher verification to Custodian-owned binaries so installed updates work normally after the one-time 1.5.3 Setup recovery.
- Kept Custodian open and displayed an update error when package verification or updater startup fails instead of silently dismissing the restart prompt and leaving shutdown state active.

### Upgrade note

- Custodian 1.5.1 and 1.5.2 users must run the signed 1.5.3 Setup executable once because the broken verifier in those installed versions cannot install its own replacement. Automatic updates work normally again after 1.5.3 is installed.

## 1.5.2 - 2026-07-11

### Fixed

- Restored readable tooltip contrast across all desktop themes by ensuring tooltip text uses the tooltip foreground instead of the global text color.

## 1.5.1 - 2026-07-10

### Changed

- Improved saved-scan persistence with invariant timestamp parsing, prepared SQLite inserts, and durable temporary-file replacement so large saves are faster without weakening crash safety.
- Reduced avoidable path normalization and traversal allocations during MFT scans.
- Enabled warnings-as-errors across the solution and removed dead or ad-hoc diagnostic code.
- Expanded the manual validation checklist for Nextcloud and Dropbox discovery, recursive scans, save/open, and provider metadata exports in the GUI and TUI.

### Security

- Hardened updates by restricting source overrides to local folders, validating Velopack checksum metadata, and verifying the expected Authenticode signer on Custodian-owned binaries before installation.
- Fully qualified Windows shell launches, used a trusted elevation working directory, and added confirmation before launching executable or script paths from loaded scan files.
- Sanitized control characters in file logs and added a security reporting and release-integrity policy.

### Fixed

- Restored completed iPhone and other portable-device scans immediately after completion and preserved active/cached scan identity when Windows rotates WPD target identifiers.
- Rejected ambiguous portable-device cache fallback matches so scans cannot be restored to the wrong storage target.

## 1.5.0 - 2026-06-27

### Added

- Added a standalone full-screen terminal UI with scan/open/save/export workflows, terminal-native charts, session cache restore, Recycle Bin inspection, Android/MTP scan and copy actions, update checks, and elevation settings.
- Added a permanent delete action for local filesystem selections, with stronger confirmation copy and result wording separate from Recycle Bin deletes.
- Added provider-aware Nextcloud and Dropbox target discovery that scans local sync roots through the existing cloud-provider flow and preserves provider metadata in saved scans and exports.
- Added chart multi-select for pie, treemap, and bar charts, syncing selected chart slices into the detail grid so existing file actions can operate on matching rows.
- Added detail-grid `Delete` and `Shift+Delete` shortcuts for Recycle Bin and permanent delete actions.

### Changed

- Moved Windows platform services into a shared project so the desktop app and TUI use the same MTP, Recycle Bin, update, elevation, logging, and settings behavior.
- Updated portable, Velopack, and legacy installer packaging to include `tui\Custodian.Tui.exe`.
- Updated clean delete, permanent delete, and move-outside-root operations to remove affected rows from the active scan without forcing a full rescan.
- Updated scan projections after clean mutations so detail rows, folder trees, summaries, charts, largest lists, extension rows, breadcrumbs, footer totals, and target status refresh together.
- Updated the left Targets list to recompute affected usage bars and labels after file operations that can change drive free space.

### Fixed

- Prevented configured Nextcloud roots from being duplicated by stale profile-folder fallback targets.
- Kept Recycle Bin delete UI updates consistent with permanent delete after successful shell operations.

## 1.4.0 - 2026-06-24

### Added

- Added an MIT license.
- Added GitHub Actions CI for build, test, and vulnerable-package scanning.
- Added Azure Artifact Signing support for Velopack and portable release builds.
- Added read-only Android phone storage scanning through Windows Portable Devices / MTP in the desktop app.
- Added portable-device source metadata to saved scan files while preserving compatibility with existing `.custodian-scan` files.
- Added phone scan actions to open selected phone folders/files in Explorer and copy selected phone files or folders to the PC.
- Added Help > What's New? to open release notes for the running app version.
- Added a one-time first-launch prompt that highlights What's New? after each app version update.
- Added README screenshots and expanded release documentation.

### Changed

- Updated the app icon with the new disk-analysis scan mark.
- Updated scan navigation to support non-filesystem paths for portable-device scan trees.
- Updated release packaging documentation for 1.4.0.

### Security

- Neutralized spreadsheet formula triggers in both full CSV exports and selected-row CSV exports.
- Confirm before opening network or remote paths from saved scan data, and classify remote paths before probing file or directory existence.
- Built SQLite scan-store connection strings with `SqliteConnectionStringBuilder`.
- Redacted credential-bearing update-source override values from logs.
- Documented GitHub upload token exposure in the Velopack upload helper.

### Fixed

- Removed ad-hoc debug logging from Recycle Bin enumeration.

## 1.3.0 - 2026-06-16

### Added

- Added pie chart zoom and panning controls with reset support.
- Added a Help menu toggle to automatically check for and download updates on startup while still asking before restart/install.

### Changed

- Replaced static UI glyphs with vector icon resources for more reliable rendering.

### Fixed

- Prevented startup update status changes from overwriting active scan or Recycle Bin footer status.

## 1.2.0 - 2026-06-14

### Added

- Added a session-only scan cache for recent scan roots so completed drive scans can be restored without rescanning while Custodian remains open.
- Added cached and scanning status badges to drive targets.
- Added a Start Scan empty state for uncached drives.
- Added View > Always open as administrator, which configures Windows to request elevation before launching Custodian.

### Changed

- Switching between cached drives now reuses prepared scan UI data to avoid unnecessary workspace blanking and skeleton flicker.
- Active scans can continue in the background while the user views another cached drive.
- Recycle Bin navigation now preserves scan state and avoids stale background scan UI updates.

### Fixed

- Preserved previous cached scan data when a refresh is cancelled or fails.
- Prevented stale async scan restores from overwriting newer target selections.
- Bounded the session scan cache to the most recent entries to avoid unbounded memory growth.
