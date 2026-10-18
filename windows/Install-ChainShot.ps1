# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
    Installs the ChainShot PowerShell module for the current user (no admin rights needed).

.DESCRIPTION
    Copies the module (and chainshot.py, used for HTML reports) into the user's module
    folders for Windows PowerShell 5.1 and PowerShell 7, then creates a Start menu
    shortcut for the tray application. Run it from the extracted release folder or
    from a clone of the repository.

.PARAMETER Startup
    Also start the tray application at logon (kept when an earlier install already did).

    The module is listed in Settings > Apps > Installed apps as "ChainShot PowerShell module",
    from where it can be uninstalled (or run Uninstall-ChainShot.ps1).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Install-ChainShot.ps1 -Startup
#>
[CmdletBinding()]
param([switch]$Startup)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'ChainShot'
if (-not (Test-Path (Join-Path $source 'ChainShot.psd1'))) { throw "Module not found next to this script: $source" }

# chainshot.py ships next to the module in releases; in a clone it lives in ../python.
$py = @((Join-Path $source 'chainshot.py'), (Join-Path $PSScriptRoot '..\python\chainshot.py')) |
    Where-Object { Test-Path $_ } | Select-Object -First 1

$docs = [Environment]::GetFolderPath('MyDocuments')
$targets = @(
    (Join-Path $docs 'WindowsPowerShell\Modules\ChainShot'),   # Windows PowerShell 5.1
    (Join-Path $docs 'PowerShell\Modules\ChainShot')           # PowerShell 7
)
foreach ($t in $targets) {
    if (Test-Path $t) { Remove-Item -Recurse -Force $t }
    New-Item -ItemType Directory -Force -Path $t | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $t -Recurse -Force
    if ($py -and -not (Test-Path (Join-Path $t 'chainshot.py'))) { Copy-Item $py $t }
    Get-ChildItem -Recurse $t | Unblock-File
    Write-Host "Installed: $t"
}

# Uninstaller next to the module, so Windows can run it from Installed apps.
$uninstaller = Join-Path $targets[0] 'Uninstall-ChainShot.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-ChainShot.ps1') -Destination $uninstaller -Force

# Keep "start at logon" from an earlier install: the shortcut is rebuilt with the new launcher.
$startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'ChainShot Tray (PowerShell).lnk'
if (Test-Path -LiteralPath $startupLnk) { $Startup = $true }

Import-Module (Join-Path $targets[0] 'ChainShot.psd1') -Force
Install-ChainShotShortcut -Startup:$Startup

# Settings > Apps > Installed apps (current user, no admin rights).
$version = (Import-PowerShellDataFile (Join-Path $targets[0] 'ChainShot.psd1')).ModuleVersion
$launcher = Join-Path $targets[0] 'ChainShotTray.exe'
$icon = if (Test-Path -LiteralPath $launcher) { "$launcher,0" } else { Join-Path $targets[0] 'chainshot.ico' }
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ChainShot.PowerShell'
New-Item -Path $key -Force | Out-Null
$values = [ordered]@{
    DisplayName          = 'ChainShot PowerShell module'
    DisplayVersion       = $version
    Publisher            = 'ChainShot contributors'
    DisplayIcon          = $icon
    InstallLocation      = $targets[0]
    URLInfoAbout         = 'https://dalkin69.github.io/chainshot/'
    HelpLink             = 'https://github.com/Dalkin69/chainshot/issues'
    UninstallString      = "`"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`" -Pause"
    QuietUninstallString = "`"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`""
}
foreach ($k in $values.Keys) { Set-ItemProperty -Path $key -Name $k -Value $values[$k] }
foreach ($k in 'NoModify', 'NoRepair') { Set-ItemProperty -Path $key -Name $k -Value 1 -Type DWord }
$size = [int]((Get-ChildItem -Recurse -File $targets[0] | Measure-Object Length -Sum).Sum / 1KB)
Set-ItemProperty -Path $key -Name EstimatedSize -Value $size -Type DWord
Write-Host 'Listed in Settings > Apps > Installed apps: ChainShot PowerShell module'
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Green
Write-Host '  New-ChainShotCase -Path D:\Cases\MyCase -Case "My case" -Examiner "Your name"'
Write-Host '  Start the tray from the Start menu (ChainShot Tray (PowerShell)), then Ctrl+Alt+P / W / R.'
Write-Host '  To remove the module later: .\Uninstall-ChainShot.ps1'
