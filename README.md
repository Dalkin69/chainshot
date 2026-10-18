<p align="center">
  <img src="extension/icons/icon128.png" width="96" alt="ChainShot logo">
</p>

<h1 align="center">ChainShot</h1>

<p align="center">
  <b>Timestamped, hash-chained screenshots for forensic and investigation reports.</b><br>
  Every capture is hashed, chained to the previous one, sealed and independently verifiable.
</p>

<p align="center">
  <a href="https://github.com/Dalkin69/chainshot/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/Dalkin69/chainshot/actions/workflows/ci.yml/badge.svg"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-2b4c7e">
  <img alt="Windows PowerShell 5.1 and 7" src="https://img.shields.io/badge/PowerShell-5.1%20%7C%207-2b4c7e">
  <img alt="Python 3.9+" src="https://img.shields.io/badge/python-3.9%2B-2b4c7e">
  <img alt="Chrome / Edge" src="https://img.shields.io/badge/browser-Chrome%20%7C%20Edge%20%7C%20Brave-2b4c7e">
  <a href="PRIVACY.md"><img alt="No telemetry" src="https://img.shields.io/badge/telemetry-none-1f7a4d"></a>
  <a href="#built-with-ai"><img alt="Built with AI" src="https://img.shields.io/badge/built%20with-AI%20(Claude)-c99a06"></a>
</p>

<p align="center"><a href="https://dalkin69.github.io/chainshot/"><b>Website</b></a> · <a href="README.fr.md">Lire en français</a></p>

---

A screenshot pasted into a report proves nothing on its own: it can be edited, re-dated or
swapped. ChainShot turns each capture into a piece of evidence that anyone can check later:

- a **banner under the image** (never over it) with the entry number, case, tag, UTC and local time, examiner and machine;
- a **SHA-256** of the PNG written to an append-only journal;
- a **hash chain** that also covers the time, tag and notes, so editing, removing or reordering any entry is detected;
- **seals** that certify the journal at a given moment, optionally with an **RFC 3161 timestamp** from an independent authority;
- a **verifier that needs nothing but Python**, so a client or auditor does not have to trust the tool that took the captures.

It was born during a digital forensics course, as a small PowerShell script used to document
an Autopsy analysis, and grew into a toolkit.

> **Private by design, open source.** No telemetry, no analytics, no account, no update check: ChainShot never
> connects to the internet on its own, and your captures never leave your computer. The only network request is
> the optional RFC 3161 timestamp, which sends the SHA-256 of a seal file and nothing else. The whole source is
> here under the MIT license, and every release is built from it by the public GitHub Actions workflow, with
> SHA-256 checksums. Details: [PRIVACY.md](PRIVACY.md).

## Contents

- [Components](#components)
- [Screenshots](#screenshots)
- [How it works](#how-it-works)
- [Quick start](#quick-start)
- [Delivering a case: evidence package](#delivering-a-case-evidence-package)
- [Verifying a case (client, auditor)](#verifying-a-case-client-auditor)
- [Case folder](#case-folder)
- [Reference](#reference)
- [What ChainShot proves, and what it does not](#what-chainshot-proves-and-what-it-does-not)
- [Testing](#testing)
- [Built with AI](#built-with-ai)
- [Contributing, security, license](#contributing-security-license)

## Components

| Component | Platform | What it does |
| --- | --- | --- |
| [**ChainShot app**](app/) (`ChainShot.exe`) | Windows 10/11, single file, nothing to install | The main Windows tool: create cases, screen/window/region captures with global hotkeys, tags, notes, drag-and-drop file hashing, journal with filter and preview, verification, seals with RFC 3161, HTML report, evidence package, tray icon; command line included |
| [**PowerShell module**](windows/) | Windows PowerShell 5.1 or 7 | The same features from PowerShell, for scripts and command-line users, with its own tray app |
| [**Browser extension**](extension/) | Chrome, Edge, Brave (Chromium 116+), Firefox 140+ | Captures the visible area or the **whole page** with its URL and title, optional **MHTML archive** of the page (Chromium), into a case in your Downloads folder or [straight into the case open in the app](docs/browser-link.md) |
| [**`chainshot.py`**](python/) | Any OS, Python 3.9+, no dependency | Reference implementation: verify, seal with RFC 3161, HTML report, CSV export, add files and notes |

All four write the **same journal format** ([specification](docs/journal-format.md)) and are tested
against the same vectors, so a case started in the app can be continued from PowerShell or the extension
and verified anywhere.

## Screenshots

**The app**: the case, its verification badge, the tag and note of the next capture, the journal with a filter,
and the preview of the selected capture with its hash.

<img src="docs/img/app-window.png" alt="ChainShot for Windows: case header with the verification badge, tag and note fields, capture buttons, journal and preview of a web page captured in an Edge window" width="860">

**A modified file is caught**: the same case after one byte range of a capture was changed. The badge, the row
and the status bar say which evidence no longer matches its SHA-256.

<img src="docs/img/app-altered.png" alt="ChainShot showing VERIFICATION FAILED and FILE HASH MISMATCH on the modified capture" width="860">

| New case | Tray menu (hotkeys work from any application) |
| --- | --- |
| <img src="docs/img/app-new-case.png" alt="New case dialog: case name, examiner, folder" width="440"> | <img src="docs/img/app-tray-menu.png" alt="Tray menu: open, capture screen, last window or region, tag, note, verify, report" width="260"> |

**The browser extension**, saving into its own case or into the case open in the app:

| Web capture with its banner | Popup | Popup, linked to the app |
| --- | --- | --- |
| <img src="docs/img/sample-capture-app.png" alt="A web page captured by the extension, with the evidence banner below it" width="430"> | <img src="docs/img/extension-popup.png" alt="Extension popup, cases in the Downloads folder" width="230"> | <img src="docs/img/extension-popup-app.png" alt="Extension popup saving into the ChainShot app case" width="230"> |

**The HTML report**, readable by anyone, regenerated from the journal:

<img src="docs/img/html-report.png" alt="HTML report generated by chainshot.py: verification banner, seals, journal with thumbnails and hashes" width="820">

## How it works

```mermaid
flowchart LR
    A[Capture<br>screen, window, region, tab] --> B[PNG + banner<br>below the image]
    B --> C[SHA-256 of the file]
    C --> D[Journal entry<br>time, tag, note, hash]
    D --> E["chain(n) = SHA-256(chain(n-1) + entry hash)"]
    E --> F[Seal<br>N entries, last chain]
    F --> G[RFC 3161 timestamp<br>optional, independent TSA]
    F --> H[Seal SHA-256 written<br>in the signed report]
    E --> I[chainshot.py verify<br>by anyone, anywhere]
```

1. **Capture**: the image gets a banner *below* it, so no captured pixel is hidden.
2. **Hash**: the SHA-256 of the final PNG is recorded with its size.
3. **Chain**: the entry (time, type, tag, file, hash, examiner, host, URL, title, note) is turned into a canonical
   text, hashed, and combined with the previous chain value. Changing anything in any entry breaks every following link.
4. **Seal**: at any point (end of a session, before delivery), a seal records how many entries exist, the last
   chain value and the hash of the journal at that point. Its SHA-256 is the value you put in your signed report
   or send to your client by another channel.
5. **Timestamp** (optional): the seal's hash is sent to an RFC 3161 Time-Stamping Authority, which signs it with
   its own clock. This removes the main weakness of local timestamps: your computer's clock.
6. **Verify**: anyone re-runs `chainshot.py verify` on the folder.

## Quick start

### Windows: the ChainShot app

1. Install it with a package manager, or download `chainshot-app-*.zip` from the latest
   [release](https://github.com/Dalkin69/chainshot/releases), extract it and start `ChainShot.exe` (nothing to install,
   no admin rights). The executable is not code-signed yet: if SmartScreen warns you, click **More info > Run anyway**
   ([why](app/README.md#install)).

   ```powershell
   winget install Dalkin69.ChainShot                                   # once accepted in the winget catalog
   scoop bucket add chainshot https://github.com/Dalkin69/chainshot; scoop install chainshot
   ```

2. **New case...**: give the case a name or reference and the examiner name.
3. Type a **tag** (for example `P17`), then capture with the buttons or the hotkeys, from any application:

| Hotkey | Capture |
| --- | --- |
| `Ctrl+Alt+P` | screen under the mouse |
| `Ctrl+Alt+W` | foreground window (its title and process are recorded) |
| `Ctrl+Alt+R` | region: the screen freezes, draw a rectangle, `Esc` cancels |

4. Drop files on the window to hash them (an export from Autopsy, a disk image), add **notes**, then **Verify**,
   **Seal + timestamp**, open the **HTML report** and, to deliver, **Export package**.

Closing the window keeps ChainShot in the notification area with its hotkeys. To remove it: **Settings > Apps >
Installed apps > ChainShot > Uninstall** (or *Options > Uninstall from this computer*); it removes everything ChainShot
added and can delete `ChainShot.exe`. Case folders are never touched. The [app guide](app/README.md)
covers the options and the command line (`ChainShot.exe verify`, `capture`, `seal --tsa`...).

### Windows: PowerShell module

For PowerShell users and scripts: the module does everything the app does, and both share the current
case and tag, so you can mix them. Download `chainshot-powershell-*.zip`, extract it, then:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-ChainShot.ps1          # add -Startup to launch its tray at logon
New-ChainShotCase -Path D:\Cases\DEMO-01 -Case 'DEMO-01 Workstation PC-042' -Examiner 'Your name'
Set-ChainShotTag P17
Invoke-ChainShot -Mode Window -Delay 3 -Note 'Autopsy - Installed Programs'
Add-ChainShotFile -Path D:\Export\capture.pcap -Note 'Network capture exported from Autopsy'
Add-ChainShotNote 'Started keyword search on the user profile'
Test-ChainShotCase
New-ChainShotSeal
Export-ChainShotReport -Open        # needs Python 3.9+
Start-ChainShotTray                 # tray icon with the same hotkeys (do not run it together with the app)
```

The module is listed in **Settings > Apps > Installed apps** as *ChainShot PowerShell module*; uninstalling it there
(or running `Uninstall-ChainShot.ps1`) removes the module and its shortcuts.

### Browser extension (Chrome, Edge, Brave, Firefox)

Store listings are on their way. Until then:

1. Chrome, Edge, Brave: download `chainshot-extension-*.zip` from the [releases](https://github.com/Dalkin69/chainshot/releases)
   and extract it (or use the `extension/` folder of a clone). Open `chrome://extensions` (or `edge://extensions`), enable
   **Developer mode**, click **Load unpacked** and select the folder.
2. Firefox 140+: `chainshot-extension-firefox-*.zip`; for a quick try, `about:debugging` > *This Firefox* > *Load Temporary
   Add-on* and select the zip (removed when Firefox closes until the add-on is published).
3. Click the ChainShot icon, create a case, then capture with the button or `Alt+Shift+S`. Tick **Full page** to capture
   the whole page (it is scrolled and stitched; fixed headers appear once) and **Page archive** to save an MHTML copy of
   the page next to the image (Chromium only; asked once).

The case is written to `Downloads/ChainShot/<case>-<UTC>/`. It contains the same files as a Windows case and is verified
the same way. The extension has no host permission: it can capture a tab only when you click its button or press its
shortcut, and it sends nothing over the network.

**Into the app's case.** In the popup, choose **ChainShot app**: captures, notes and the tag then go into the case open in
ChainShot (Windows: the app offers to register the link at its first start; Linux and macOS: `python3 chainshot.py register-browser`
and `chainshot.py use <case>`). Web captures, screen captures, files and notes end up in one journal. See
[docs/browser-link.md](docs/browser-link.md).

> If your browser is set to *"Ask where to save each file"*, turn it off while using ChainShot, otherwise every file
> opens a save dialog.

### Python CLI (any OS)

```bash
python chainshot.py init ./case --case "DEMO-01" --examiner "Your name"
python chainshot.py add ./case evidence.E01 --tag P00 --note "Disk image before analysis"
python chainshot.py note ./case "Started timeline analysis" --tag P05
python chainshot.py verify ./case
python chainshot.py seal ./case --tsa                 # RFC 3161 timestamp (default TSA: FreeTSA)
python chainshot.py report ./case                     # ./case/report/index.html
python chainshot.py export-csv ./case                 # read-only CSV view
python chainshot.py package ./case --tsa              # zip for delivery (see below)
```

Install it as a command with `pipx install "git+https://github.com/Dalkin69/chainshot#subdirectory=python"`,
or just copy the single file.

## Delivering a case: evidence package

**Export package** (app), `ChainShot.exe package`, `chainshot.py package` or `Export-ChainShotPackage` builds one zip:

```
DEMO-01-package-20261017T091500Z.zip
├── DEMO-01/              the case folder: journal, captures, files, seals, report
├── chainshot.py          the independent verifier (standard Python, no dependency)
├── VERIFY.txt            what to check and how, with the seal SHA-256 and the journal SHA-256
└── SHA256SUMS.txt        SHA-256 of every file of the package
```

The case must verify to be packaged; the app offers to seal it first (with an RFC 3161 timestamp). The zip name and its
SHA-256 are then recorded in the journal: send that SHA-256 to the recipient by another channel, or write it in your report.

## Verifying a case (client, auditor)

You received a case folder and a report that states a **seal SHA-256**. You do not need ChainShot's capture tools:

```bash
python chainshot.py verify path/to/case                                   # files, chain, seals
python chainshot.py verify path/to/case --tsa-ca freetsa-cacert.pem       # + RFC 3161 signature (needs openssl)
sha256sum path/to/case/seals/seal-*.json                                  # compare with the value in the report
```

| Check | What it proves |
| --- | --- |
| every entry `OK` | each file is exactly the one recorded; no entry was edited, removed, inserted or reordered |
| seal `OK` | the journal still matches what was sealed, even if entries were added afterwards |
| seal SHA-256 = value in the signed report | the case is the one the examiner delivered |
| RFC 3161 imprint `OK`, signature `valid` | the seal existed at the TSA's time, independently of the examiner's clock |

More details in [docs/verification.md](docs/verification.md).

## Case folder

```
DEMO-01/
├── chainshot-case.json       case name, examiner, creation time
├── journal.jsonl             one JSON entry per line, append-only (the evidence of record)
├── captures/                 0002_20261016T080102345Z_P17.png ...
├── files/                    files copied into the case (optional)
├── seals/                    seal-<UTC>.json and seal-<UTC>.tsr (RFC 3161)
└── report/index.html         generated view, not evidence
```

Entry `#1` of every journal is the *Case created* note, so the first capture is `#2 (capture 1)`: the `#` number is
the journal entry, the one to quote in a report.

Do **not** open `journal.jsonl` in Excel and save it: that rewrites the file. Use `chainshot.py export-csv` for a spreadsheet view.

## Reference

<details>
<summary><b><code>ChainShot.exe</code> commands</b></summary>

| Command | Purpose |
| --- | --- |
| `ChainShot.exe [--minimized]` | open the application (in the tray with `--minimized`) |
| `new-case <folder> --case NAME [--examiner NAME]` | create a case and select it |
| `tag [TAG]` | show or set the tag of the next entries |
| `capture [--mode screen\|all\|window\|region] [--rect x,y,w,h] [--delay s] [--tag] [--note]` | capture |
| `add <file>... [--copy] [--tag] [--note]` | hash files into the journal |
| `note TEXT [--tag]` | text-only entry |
| `verify [<case>] [--json]` | verify; exit code 0 = intact, 1 = problem, 2 = error |
| `seal [<case>] [--tsa [URL]]` | seal, optionally with an RFC 3161 timestamp |
| `report [<case>] [--out FILE] [--open]` | HTML report |
| `use [<case>]` | show or set the current case (shared with the module and `chainshot.py`) |
| `package [<case>] [--out FILE.zip] [--seal] [--tsa [URL]]` | evidence package for delivery |
| `register-browser [--extension-id ID] [--remove]` | browser link for the extension (also done by the app at startup) |
| `uninstall [--keep-settings] [--delete-exe] [--gui]` | remove the shortcut, start with Windows, browser link, Installed apps entry and settings (not the cases); `--delete-exe` also deletes ChainShot.exe |
| `selftest <vectors.json>` | check the implementation against the shared vectors |

Commands use the current case unless `--case <folder>` is given. From PowerShell, add `| Out-Host` so the prompt
waits for the result.
</details>

<details>
<summary><b>PowerShell commands</b></summary>

| Command | Purpose |
| --- | --- |
| `New-ChainShotCase -Path -Case [-Examiner]` | create a case and select it |
| `Set-ChainShotCase -Path` | select an existing case |
| `Set-ChainShotTag -Tag` | tag for the next entries (persists across reboots) |
| `Get-ChainShotStatus` | current case, tag, number of entries |
| `Invoke-ChainShot [-Mode Screen\|AllScreens\|Window\|Region] [-Rectangle 'x,y,w,h'] [-Delay s] [-Note] [-Tag]` | capture |
| `Add-ChainShotFile -Path [-Copy] [-Note] [-Tag]` | hash a file into the journal |
| `Add-ChainShotNote -Text [-Tag]` | text-only entry |
| `Read-ChainShotJournal` | journal entries as objects |
| `Test-ChainShotCase [-Quiet]` | verify files, chain and seals |
| `New-ChainShotSeal` | seal the journal |
| `Export-ChainShotReport [-Open]` | HTML report (calls `chainshot.py`) |
| `Export-ChainShotPackage [-Path] [-Seal] [-Timestamp] [-TsaUrl]` | evidence package (calls `chainshot.py`) |
| `Start-ChainShotTray` | tray application and hotkeys |
| `Install-ChainShotShortcut [-Startup]` | Start menu / Startup shortcuts for the PowerShell tray |

Every command takes `-CasePath` to work on a case other than the current one.
</details>

<details>
<summary><b><code>chainshot.py</code> commands</b></summary>

| Command | Purpose |
| --- | --- |
| `init <case> --case NAME [--examiner NAME]` | create a case |
| `add <case> <file> [--tag] [--note] [--copy]` | hash a file into the journal |
| `note <case> TEXT [--tag]` | text-only entry |
| `verify <case> [--tsa-ca PEM] [--json]` | verify; exit code 0 = intact, 1 = problem, 2 = error |
| `seal <case> [--tsa [URL]]` | seal, optionally with an RFC 3161 timestamp |
| `report <case> [-o FILE] [--tsa-ca PEM]` | HTML report |
| `export-csv <case> [-o FILE]` | CSV view of the journal |
| `tsr-info <file.tsr>` | show the content of a timestamp |
| `package <case> [-o FILE.zip] [--seal] [--tsa [URL]]` | evidence package: case + `chainshot.py` + `VERIFY.txt` + `SHA256SUMS.txt` |
| `use [<case>]`, `tag [TAG]` | show or set the current case and tag (`state.json`, shared with the app and the module) |
| `register-browser [--dir DIR] [--extension-id ID] [--remove]` | let the extension save into the current case (`chainshot.py` as native messaging host) |

`verify` also checks journals produced by the original `Capture-Preuve.ps1` script (`journal_preuves.csv`, format v0).
</details>

<details>
<summary><b>Journal entry example</b></summary>

```json
{"v":"1","seq":"2","utc":"2026-10-16T08:01:02.345Z","type":"screenshot","tag":"P17",
 "file":"captures/0002_20261016T080102345Z_P17.png","size":"183442",
 "sha256":"9f2c...","examiner":"Analyst","host":"LAB-PC","source":"chainshot-ps/1.0.0",
 "url":"","title":"Autopsy 4.21.0","note":"Installed Programs",
 "local":"2026-10-16T10:01:02+02:00","extra":{"mode":"Window","process":"autopsy64"},"chain":"4b1e..."}
```
</details>

## What ChainShot proves, and what it does not

**It proves** that a file is exactly the one recorded in the journal, that the journal was not altered after a seal,
and, with RFC 3161, that the seal existed at a time certified by a third party.

**It does not prove**:

- that what was on screen was true: ChainShot records what you saw, not the integrity of the system you looked at;
- the time of an entry by itself: entry times come from the examiner's clock. Keep it synchronised, and use RFC 3161
  seals to anchor the journal to an external time: each timestamp also records how far the local clock was from the
  authority's (`RFC 3161 time ... (local clock +0.4 s)`), and ChainShot warns beyond one minute;
- anything if the seal hash is not kept outside the folder: someone able to rewrite the whole folder can build a new
  consistent chain. The seal SHA-256 must be in your signed report, or sent to the client when you deliver.

ChainShot makes evidence **tamper-evident**, not tamper-proof. Store cases on media you control, and follow your
organisation's chain-of-custody procedure. See [SECURITY.md](SECURITY.md).

## Testing

Every push runs the full test suite on GitHub Actions ([workflow](.github/workflows/ci.yml)):

| Suite | Runs on | Covers |
| --- | --- | --- |
| App end-to-end | Windows (real desktop) | `ChainShot.exe` built from source: vectors, **real captures with the banner** (screen, rectangle, an Edge window), cases verified by `chainshot.py` and the module in both directions, shared settings, tampering, **RFC 3161** against an OpenSSL authority, browser link protocol and registration, evidence package verified by its own `chainshot.py`, HTML report, the window itself (the screenshots above come from it) |
| Python `unittest` | Linux, Windows, macOS; Python 3.9 and 3.13 | journal, tampering scenarios, seals, legacy v0, report, **RFC 3161 with a real OpenSSL timestamp authority**, clock offset, browser link host, evidence packages |
| Pester | Windows PowerShell 5.1, PowerShell 7 on Windows and Linux | journal, tampering, seals, **real screen/window/rectangle captures**, **tray app and hotkey registration**, interoperability with Python in both directions |
| Selenium | Firefox | Firefox build of the extension checked by Mozilla's add-on linter, then capture, full page and **browser link** with `chainshot.py` in a real Firefox |
| Playwright | Chromium | extension loaded in a real browser: capture, files written by the downloads API, verification by `chainshot.py`, tamper detection, popup, **browser link** with `chainshot.py` as native messaging host |
| Shared vectors | all four | `tests/vectors/canonical-vectors.json`: identical canonical text, entry hashes and chain values |

Run them locally:

```bash
python -m unittest discover -s python/tests -v
pwsh -c "Invoke-Pester ./windows/tests"
npm install && npx playwright install chromium && npm run test:extension
dotnet build app/ChainShot.csproj -c Release && python tests/app_e2e.py app/bin/Release/ChainShot.exe   # Windows
```

## Built with AI

This project was designed and written with **Claude** (Anthropic's AI assistant), under the direction of
[Dalkin69](https://github.com/Dalkin69), who defined the needs from real forensic coursework, chose the
features and validated the result. The AI wrote the code, tests and documentation; every component is covered by
the automated tests described above, which run on real Windows, Linux and macOS machines and in a real browser.

As with any tool used for evidence, review it before relying on it, and report problems through the issues.

## Contributing, security, license

- Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md). Changes to the journal format must update the
  specification, the four implementations and the shared vectors together.
- Security issues: see [SECURITY.md](SECURITY.md).
- License: [MIT](LICENSE).
