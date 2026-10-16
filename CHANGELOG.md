# Changelog

All notable changes are listed here. The project follows [Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-10-17

### Added
- **ChainShot for Windows (`ChainShot.exe`)**, the new main Windows tool: a single executable
  (.NET Framework 4.8, built into Windows 10 and 11, no installation, no admin rights) with a graphical
  interface to create and open cases, capture the screen, a window or a region (buttons, delay, global
  hotkeys `Ctrl+Alt+P/W/R`), set the tag and the note of the next capture, hash files by drag and drop,
  add notes, verify, seal with an optional RFC 3161 timestamp and open the HTML report. Tray icon with
  notifications, start with Windows, Start menu shortcut with the ChainShot icon, recent cases.
- `ChainShot.exe` command line for scripts: `new-case`, `tag`, `capture`, `add`, `note`, `verify [--json]`,
  `seal [--tsa]`, `report`, `selftest`.
- A fourth implementation of the journal format (C#), checked against the shared vectors and, in CI,
  against `chainshot.py`, the PowerShell module and a local OpenSSL time-stamp authority.
- The app and the PowerShell module share the current case and tag (`%APPDATA%\ChainShot\state.json`).
- Release asset `chainshot-app-<version>.zip`; every CI run also publishes the tested `ChainShot.exe`.

### Changed
- Windows: the HTML report opens in the default web browser, even when `.html` files are
  associated with an editor such as VS Code.
- Windows: the Start menu shortcut and the tray use the ChainShot icon (`chainshot.ico`)
  instead of the PowerShell icon.
- Banners show the capture number next to the journal entry number: `#2 (capture 1)`.
- The PowerShell tray shortcut is now named **ChainShot Tray (PowerShell)**; **ChainShot** in the Start
  menu is the app. The PowerShell package is renamed `chainshot-powershell-<version>.zip`.
- All components share the version number 1.1.0 (checked by `tests/check_versions.py`).

## [1.0.0] - 2026-10-16

First public release.

### Added
- Journal format v1: canonical entries, SHA-256 hash chain covering time, tag and notes, seals
  with journal prefix hash, RFC 3161 timestamps ([specification](https://github.com/Dalkin69/chainshot/blob/main/docs/journal-format.md)).
- `chainshot.py` (Python 3.9+, standard library only): `init`, `add`, `note`, `verify`, `seal --tsa`,
  `report` (HTML), `export-csv`, `tsr-info`; verification of legacy v0 journals (`journal_preuves.csv`).
- PowerShell module for Windows PowerShell 5.1 and PowerShell 7: screen, all screens, window and
  region captures with an evidence banner below the image; tray application with global hotkeys
  (`Ctrl+Alt+P/W/R`); file hashing, notes, verification, seals, HTML report; per-user installer.
- Browser extension for Chromium browsers (Chrome, Edge, Brave): visible tab capture with URL and
  title, case folder written to `Downloads/ChainShot/`, in-browser chain check, `Alt+Shift+S` shortcut.
- Shared canonical vectors and CI on Linux, Windows and macOS, including real Windows captures,
  the tray application and the extension in Chromium.

### Origin
- Grew out of `Capture-Preuve.ps1`, a script written during a digital forensics course.
