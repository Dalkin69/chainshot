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
    Also start the tray application at logon.

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

Import-Module (Join-Path $targets[0] 'ChainShot.psd1') -Force
Install-ChainShotShortcut -Startup:$Startup
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Green
Write-Host '  New-ChainShotCase -Path D:\Cases\MyCase -Case "My case" -Examiner "Your name"'
Write-Host '  Start the tray from the Start menu (ChainShot), then Ctrl+Alt+P / W / R.'
