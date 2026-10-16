# ChainShot for Windows

PowerShell module (Windows PowerShell 5.1 and PowerShell 7) with a tray application.

```powershell
powershell -ExecutionPolicy Bypass -File .\Install-ChainShot.ps1 -Startup
New-ChainShotCase -Path D:\Cases\DEMO-01 -Case 'DEMO-01' -Examiner 'Your name'
```

Then start **ChainShot** from the Start menu and use `Ctrl+Alt+P` (screen), `Ctrl+Alt+W` (window)
or `Ctrl+Alt+R` (region). `Get-Help <command> -Full` documents every command; the
[main README](../README.md#reference) lists them.

- No admin rights are needed: the installer copies the module into your Documents folder.
- On screens scaled to 125 % or 150 %, captures use physical pixels (per-monitor DPI awareness).
- If a hotkey is already used by another program, a notification says so; the tray menu still works.
- `Export-ChainShotReport` needs Python 3.9+ (`py`, `python` or `python3` on the PATH).

Tests: `Invoke-Pester ./tests` (capture and tray tests run on Windows only).
