# Custodian Utility-Native UI Redesign

## Summary

Rebuild the WPF UI into a dense, Folder Size-inspired admin utility without copying proprietary assets or exact visuals. The app should feel fast, informative, and smooth: scan controls are always visible, the selected folder context is obvious, tables show size/share at a glance, charts support drill-down, and destructive actions move into safe secondary surfaces.

## Key Changes

- Replace the prototype layout with a three-zone workspace:
  - Top command bar: path combo, browse, scan/stop, mode selector, allocated toggle, save/open/export menu, elevation/engine badge.
  - Left sidebar: drive/scan target list with usage bars, then folder tree showing name, formatted size, and percent share.
  - Main workspace: selected-folder header, details grid, chart/summary pane, and status/timing footer.
- Redesign data presentation:
  - Main grid rows show icon, name, logical size, allocated size, percent-of-parent bar, file/folder counts, extension/type, and full path.
  - Largest files/folders and extension summaries become quick switch views inside the main workspace, not disconnected tabs.
  - Add sortable columns, double-click to open/drill into folders, right-click context menu, keyboard copy, and selection-preserving refresh.
- Improve charts and summaries:
  - Replace the rough wrap-panel treemap with a deterministic proportional bar/list visualization for top children in v1 of the UI pass.
  - Add compact summary tiles for total size, file count, folder count, skipped entries, engine, elapsed time, and phase timing.
  - Chart/list selection should sync to the details grid where practical.
- Move file actions to safe secondary UX:
  - Context menu: Open, Reveal in Explorer, Copy Path, Copy Rows, Export Selection, Move to Recycle Bin.
  - Delete remains confirmation-only and is not a primary toolbar button.
- Add UI-focused view models:
  - Introduce presentation models for folder tree nodes, grid rows, drive rows, summary metrics, and command states.
  - Keep scanner/core models unchanged except for any minimal display helpers needed to avoid recomputing percentages repeatedly.

## Test Plan

- Build and unit tests must still pass.
- Add view-model tests for percent calculations, sorted row generation, extension/type display, and selected-folder row projection.
- Manual UI validation:
  - Launch portable app elevated.
  - Scan `C:\` in `Auto`, `Allocated` unchecked.
  - Confirm scan stays responsive, progress/status updates are visible, and final engine/timing diagnostics are readable.
  - Drill through tree, grid, and chart/list views without layout jumps.
  - Confirm right-click actions work and delete is secondary plus confirmation-gated.
  - Confirm save/load/export still work.
- Publish a new portable UI build after validation.

## Assumptions

- Use pure WPF controls/styles for this pass; no new UI framework dependency.
- Use built-in Windows/Segoe icon glyphs or lightweight XAML shapes, not generated proprietary-looking assets.
- Optimize for light-theme server/admin use first.
- Prioritize dense utility workflow over a modern dashboard look.
- Avoid exact Folder Size copying; use it as a product-quality reference for information density and interaction flow only.
