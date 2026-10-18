# ChainShot PowerShell module

PowerShell module (Windows PowerShell 5.1 and PowerShell 7) with a tray application. It is fully supported
and recommended for scripts and command-line users; for a graphical application, use [`ChainShot.exe`](../app/).
Both share the current case and tag (`%APPDATA%\ChainShot\state.json`).

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-ChainShot.ps1 -Startup
New-ChainShotCase -Path D:\Cases\DEMO-01 -Case 'DEMO-01' -Examiner 'Your name'
```

Then start **ChainShot Tray (PowerShell)** from the Start menu and use `Ctrl+Alt+P` (screen), `Ctrl+Alt+W` (window)
or `Ctrl+Alt+R` (region). `Get-Help <command> -Full` documents every command; the
[main README](../README.md#reference) lists them.

- No admin rights are needed: the installer copies the module into your Documents folder and lists it in
  Settings > Apps > Installed apps (*ChainShot PowerShell module*), from where it can be uninstalled.
- The shortcuts start `ChainShotTray.exe`, a small launcher that the installer compiles from `ChainShotTray.cs` with
  the C# compiler of the .NET Framework (part of Windows), so the Start menu and Task Manager > Startup apps show
  *ChainShot* and its icon rather than *powershell.exe*.
- On screens scaled to 125 % or 150 %, captures use physical pixels (per-monitor DPI awareness).
- If a hotkey is already used by another program, a notification says so; the tray menu still works.
  The app and the PowerShell tray use the same hotkeys: run one of them at a time.
- `Export-ChainShotReport` and `Export-ChainShotPackage` (evidence zip for delivery) need Python 3.9+
  (`py`, `python` or `python3` on the PATH).

Tests: `Invoke-Pester ./tests` (capture and tray tests run on Windows only).
