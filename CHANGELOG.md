# Changelog

All notable changes to Custodian are tracked here.

## Unreleased

No unreleased changes yet.

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
