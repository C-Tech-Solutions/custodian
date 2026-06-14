# Changelog

All notable changes to Custodian are tracked here.

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
