# Manual Validation Checklist

- Run a recursive scan against a normal local folder and compare totals against the folder properties dialog.
- Run Auto mode against a local NTFS drive from an elevated shell and confirm the UI reports `NTFS MFT` or records a clear fallback warning.
- Run a scan against a UNC path and confirm it completes with the recursive engine.
- Save a `.custodian-scan`, close the app, reopen it, and load the file.
- Export CSV and JSON from both GUI and CLI.
- In a dev/raw run, use Help > Check for Updates and confirm it reports that updates require the installed Custodian app.
- Build an update package with `scripts\publish-velopack.ps1 -Version 1.0.0` and confirm Velopack emits release assets under `artifacts\velopack`.
- For unsigned local update validation, run `scripts\prepare-local-update-test.ps1`, install the preserved baseline setup, set `CUSTODIAN_UPDATE_SOURCE` to the local release folder, and confirm Help > Check for Updates prompts for the update.
- Publish with `scripts\publish-portable.ps1`, move `artifacts\portable\Custodian` to another folder, and launch the app and CLI from there.
- Build the EXE installer with Inno Setup 6 using `installer\Custodian.iss`, install it, launch from Start Menu, then uninstall.
