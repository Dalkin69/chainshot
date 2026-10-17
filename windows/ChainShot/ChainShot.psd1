@{
    RootModule           = 'ChainShot.psm1'
    ModuleVersion        = '1.3.0'
    GUID                 = '7d3c1f0e-5b8a-4f62-9a51-2c4e8b6f0d13'
    Author               = 'Dalkin69'
    Copyright            = '(c) 2026 Dalkin69. MIT License.'
    Description          = 'Timestamped, hash-chained screenshots and file hashes for forensic reports. Tamper-evident journal, seals, tray app with hotkeys.'
    PowerShellVersion    = '5.1'
    CompatiblePSEditions = @('Desktop', 'Core')
    FunctionsToExport    = @(
        'New-ChainShotCase', 'Set-ChainShotCase', 'Set-ChainShotTag', 'Get-ChainShotStatus',
        'Invoke-ChainShot', 'Add-ChainShotFile', 'Add-ChainShotNote', 'Read-ChainShotJournal',
        'Test-ChainShotCase', 'New-ChainShotSeal', 'Export-ChainShotReport', 'Export-ChainShotPackage',
        'Start-ChainShotTray', 'Install-ChainShotShortcut',
        'Get-ChainShotCanonicalString', 'Get-ChainShotEntryHash', 'Get-ChainShotNextChain'
    )
    CmdletsToExport      = @()
    VariablesToExport    = @()
    AliasesToExport      = @()
    PrivateData          = @{
        PSData = @{
            Tags         = @('forensics', 'DFIR', 'screenshot', 'evidence', 'chain-of-custody', 'hash', 'Windows')
            LicenseUri   = 'https://github.com/Dalkin69/chainshot/blob/main/LICENSE'
            ProjectUri   = 'https://github.com/Dalkin69/chainshot'
        }
    }
}
