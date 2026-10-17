# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
    Removes the ChainShot PowerShell module installed by Install-ChainShot.ps1 (current user).

.DESCRIPTION
    Deletes the module folders for Windows PowerShell 5.1 and PowerShell 7, the Start menu
    and Startup shortcuts of the tray application and, unless -KeepSettings is given, the
    current case and tag (%APPDATA%\ChainShot\state.json). Case folders are never touched.
    ChainShot.exe has its own command: ChainShot.exe uninstall.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Uninstall-ChainShot.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$KeepSettings)

$ErrorActionPreference = 'Stop'
$docs = [Environment]::GetFolderPath('MyDocuments')
$items = @(
    (Join-Path $docs 'WindowsPowerShell\Modules\ChainShot'),
    (Join-Path $docs 'PowerShell\Modules\ChainShot'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'ChainShot Tray (PowerShell).lnk'),
    (Join-Path ([Environment]::GetFolderPath('Startup')) 'ChainShot Tray (PowerShell).lnk')
)
if (-not $KeepSettings -and $env:APPDATA) { $items += (Join-Path $env:APPDATA 'ChainShot\state.json') }

Get-Module ChainShot | Remove-Module -Force -ErrorAction SilentlyContinue
foreach ($i in $items) {
    if (Test-Path -LiteralPath $i) {
        if ($PSCmdlet.ShouldProcess($i, 'Remove')) {
            Remove-Item -LiteralPath $i -Recurse -Force
            Write-Host "Removed: $i"
        }
    }
}
Write-Host 'ChainShot PowerShell module removed. Case folders were not touched.' -ForegroundColor Green
