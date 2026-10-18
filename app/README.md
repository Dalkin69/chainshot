# ChainShot for Windows (`ChainShot.exe`)

The ChainShot application: create case folders, take timestamped screenshots with an evidence
banner, hash files, write notes, verify, seal and produce the HTML report, from one window.

- **One file, nothing to install**: `ChainShot.exe` runs on Windows 10 (1903+) and Windows 11, which
  include .NET Framework 4.8. No administrator rights are needed.
- **Same journal format** as the PowerShell module, `chainshot.py` and the browser extension: a case can
  be continued with any of them and verified anywhere with `python chainshot.py verify <case>`.
- **Command line included** for scripts (see below).
- **Private**: no telemetry, no account, no update check; the only network request is the optional RFC 3161
  timestamp, which sends a hash ([PRIVACY.md](../PRIVACY.md)). Open source, MIT license.

## Install

Package managers: `winget install Dalkin69.ChainShot` (once accepted in the winget catalog) or
`scoop bucket add chainshot https://github.com/Dalkin69/chainshot; scoop install chainshot`. By hand:

1. Download `chainshot-app-<version>.zip` from the [releases](https://github.com/Dalkin69/chainshot/releases),
   check it against `SHA256SUMS.txt`, and extract it to a folder of your choice (for example
   `%LOCALAPPDATA%\Programs\ChainShot`).
2. Start `ChainShot.exe`. In **Options**, choose **Add ChainShot to the Start menu** and, if you want, **Start with Windows**.

> **Windows SmartScreen.** The executable is not code-signed (a certificate costs money every year), so the
> first launch may show *"Windows protected your PC"*. Click **More info**, then **Run anyway**. You can check
> what you run: the SHA-256 of the zip is in `SHA256SUMS.txt`, the source is in this repository and the
> release is built by the public GitHub Actions workflow. Some antivirus programs are wary of any unsigned
> program that registers global hotkeys and captures the screen; that is what ChainShot does, on purpose.

`ChainShot.exe.config` is optional: it improves scaling when windows move between monitors with different
zoom levels. Keep it next to the executable.

## Use

| Step | How |
| --- | --- |
| Create a case | **New case...**: a name or reference, the examiner, the parent folder. The case folder is created as `<parent>\<name>-<date>`. |
| Tag | Type the step or question you are documenting (`P17`) in **Tag**. It is written in the banner, the journal and the file name. |
| Capture | **Screen**, **Window**, **Region** buttons, or the hotkeys below from any application. The text in **Note for the next capture** goes in the banner and the journal, then is cleared. |
| Hash files | **Add files...** or drag files onto the window: exports, logs, disk images. By default they are copied into `files/` (Options). |
| Note | **Add note...**: an action or an observation, chained like everything else. |
| Verify | **Verify** re-hashes every file and checks the chain and the seals. The badge in the header shows the result. |
| Seal | **Seal** freezes the journal; **Seal + timestamp** also gets an RFC 3161 timestamp from a time-stamp authority (internet needed, default FreeTSA). Write the seal SHA-256 (copied to the clipboard) in your report. |
| Report | **HTML report** writes `report\index.html` and opens it in your web browser. |
| Deliver | **Export package** writes one zip: the case, `chainshot.py` to verify it, `VERIFY.txt` and `SHA256SUMS.txt`. It offers to seal first; the zip SHA-256 is copied to the clipboard and recorded in the journal. |
| Review | **Filter** (`Ctrl+L`) narrows the journal to a tag, a word, a URL. The preview pane (`F3`) shows the selected capture and its details; double-click opens the file. |
| Web pages | In the ChainShot browser extension, choose **ChainShot app**: its captures are saved into the case open here ([browser link](../docs/browser-link.md)). |

| Hotkey (any application) | Capture |
| --- | --- |
| `Ctrl+Alt+P` | screen under the mouse |
| `Ctrl+Alt+W` | active window (title and process are recorded) |
| `Ctrl+Alt+R` | region: the screen freezes, draw a rectangle, `Esc` cancels |

When you close the window, ChainShot keeps running in the notification area so the hotkeys stay
active (Options to change it; right-click the icon > **Exit** to quit). Windows 11 puts new icons in the
overflow menu: to keep it visible, go to **Settings > Personalization > Taskbar > Other system tray icons**
and turn ChainShot on.

The **Window** button captures the window you were using before switching to ChainShot. With a **Delay**,
you have time to click the window you want.

If the hotkeys are already taken (for example by the PowerShell tray, `Start-ChainShotTray`), the status
bar says so: close the other program and restart ChainShot.

## Command line

`ChainShot.exe` is a Windows program: from PowerShell, pipe its output (`| Out-Host`) so that the prompt
waits for the result. From `cmd.exe`, use `start /wait ChainShot.exe ...`.

```powershell
.\ChainShot.exe new-case D:\Cases\DEMO-01 --case "DEMO-01 Workstation PC-042" --examiner "Analyst" | Out-Host
.\ChainShot.exe tag P17 | Out-Host
.\ChainShot.exe capture --mode window --delay 3 --note "Autopsy - Installed Programs" | Out-Host
.\ChainShot.exe capture --rect 0,0,1280,720 | Out-Host
.\ChainShot.exe add D:\Export\capture.pcap --copy --note "Exported from Autopsy" | Out-Host
.\ChainShot.exe note "Keyword search started" | Out-Host
.\ChainShot.exe verify | Out-Host                     # exit code 0 intact, 1 problem, 2 error
.\ChainShot.exe seal --tsa | Out-Host                 # --tsa <url> for another authority
.\ChainShot.exe report --open | Out-Host
.\ChainShot.exe package --seal | Out-Host             # zip next to the case folder (--out <file.zip>)
.\ChainShot.exe use D:\Cases\DEMO-01 | Out-Host        # current case (shared with the module)
.\ChainShot.exe register-browser | Out-Host           # browser link (done by the app at startup)
```

Commands work on the current case (the one open in the app, also used by the PowerShell module) or
on `--case <folder>`. `ChainShot.exe --help` lists them all.

## Settings

Stored in `%APPDATA%\ChainShot`:

- `state.json`: current case and tag, **shared with the PowerShell module** (`Set-ChainShotCase`, `Set-ChainShotTag`);
- `gui.json`: recent cases and tags, default examiner and folder, TSA URL, options;
- `com.chainshot.host.json`: the browser link manifest (with `HKCU\Software\<browser>\NativeMessagingHosts\com.chainshot.host`;
  Options > *Accept captures from the browser extension* adds or removes them).

Deleting them resets the application; cases are never stored there.

## Uninstall

ChainShot has no installer and writes nothing outside your user profile. As soon as it has changed something on
the computer (browser link, start with Windows, Start menu shortcut), it is listed in **Settings > Apps > Installed
apps**, so it can be removed from there like any other program.

| What | Where | When |
| --- | --- | --- |
| Settings | `%APPDATA%\ChainShot` (`gui.json`, `state.json`) | first start |
| Browser link | `HKCU\Software\<browser>\NativeMessagingHosts\com.chainshot.host` and its manifests in `%APPDATA%\ChainShot` | if you accept it (asked at first start) |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ChainShot` | Options > Start with Windows |
| Start menu shortcut | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\ChainShot.lnk` | Options > Add ChainShot to the Start menu |
| Installed apps entry | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ChainShot` | with any of the three above |

To remove all of it: **Settings > Apps > Installed apps > ChainShot > Uninstall**, or *Options > Uninstall from this
computer*. ChainShot lists what it will remove, removes it, then offers to delete `ChainShot.exe` (with the other files
of the zip and their folder, when nothing else is in it). Command line: `ChainShot.exe uninstall --delete-exe`.
Case folders are never touched.

Scoop runs `ChainShot.exe uninstall` when you uninstall ChainShot. With winget, run *Options > Uninstall from this
computer* first, then `winget uninstall Dalkin69.ChainShot`.

## Build from source

```powershell
dotnet build app/ChainShot.csproj -c Release            # -> app/bin/Release/ChainShot.exe
python tests/app_e2e.py app/bin/Release/ChainShot.exe   # end-to-end tests
```

C# 7.3 on .NET Framework 4.8, Windows Forms, no package at run time. The source is in [`src/`](src):
`Journal.cs` (format, verification, seals), `Rfc3161.cs`, `Report.cs`, `Capture.cs`, `MainForm.cs`, `Program.cs` (command line).
