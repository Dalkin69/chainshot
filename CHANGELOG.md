# Changelog

All notable changes are listed here. The project follows [Semantic Versioning](https://semver.org/).

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
