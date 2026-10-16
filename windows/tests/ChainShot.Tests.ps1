# SPDX-License-Identifier: MIT
# Pester tests for the ChainShot module. Compatible with Pester 4.10+ and 5.x.
# Windows-only tests (screen capture, tray) mark themselves as skipped elsewhere.
#
#   Invoke-Pester ./windows/tests

Describe 'ChainShot' {
    BeforeAll {
        $repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).ProviderPath
        Import-Module (Join-Path $repo 'windows/ChainShot/ChainShot.psd1') -Force
        $script:Repo = $repo
        # Parse with the module's date-safe reader: PowerShell 7's ConvertFrom-Json would
        # turn the ISO timestamps of the vectors into DateTime objects.
        $raw = Get-Content -Raw -Encoding UTF8 (Join-Path $repo 'tests/vectors/canonical-vectors.json')
        $script:Vectors = & (Get-Module ChainShot) { param($t) ConvertFrom-JournalLine $t } $raw
        $script:IsWin = ($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows
        $script:Python = @('python3', 'python', 'py') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
        # Isolated state file: never touch the user's real ChainShot settings.
        if ($script:IsWin) { $env:APPDATA = Join-Path ([IO.Path]::GetTempPath()) ("cs-appdata-" + [guid]::NewGuid()) }
        else { $env:HOME = Join-Path ([IO.Path]::GetTempPath()) ("cs-home-" + [guid]::NewGuid()) }
        New-Item -ItemType Directory -Force -Path $(if ($script:IsWin) { $env:APPDATA } else { $env:HOME }) | Out-Null
        function script:New-TestCase {
            # CHAINSHOT_TEST_OUT keeps the cases (CI uploads them as an artifact for inspection).
            $base = $env:CHAINSHOT_TEST_OUT
            if (-not $base) { $base = [IO.Path]::GetTempPath() }
            $dir = Join-Path $base ("cs-case-" + [guid]::NewGuid())
            New-ChainShotCase -Path $dir -Case 'TEST-PS' -Examiner 'Tester' | Out-Null
            return $dir
        }
        function script:Write-JournalLines($Case, $Lines) {
            [IO.File]::WriteAllText((Join-Path $Case 'journal.jsonl'), (($Lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
        }
    }

    Context 'Canonical form (shared vectors)' {
        It 'computes the canonical string, entry hash and chain of every vector' {
            $prev = $script:Vectors.genesis
            foreach ($v in $script:Vectors.vectors) {
                Get-ChainShotCanonicalString -Entry $v.entry | Should -BeExactly $v.canonical
                Get-ChainShotEntryHash -Entry $v.entry | Should -BeExactly $v.entry_hash
                $prev = Get-ChainShotNextChain -PreviousChain $prev -Entry $v.entry
                $prev | Should -BeExactly $v.chain
            }
        }
    }

    Context 'Journal' {
        It 'creates a case with an INIT entry and verifies it' {
            $c = New-TestCase
            (Read-ChainShotJournal -CasePath $c).Count | Should -Be 1
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeTrue
        }

        It 'hashes a file and keeps relative paths for files inside the case' {
            $c = New-TestCase
            $f = Join-Path $c 'files/evidence.txt'
            New-Item -ItemType Directory -Force -Path (Split-Path $f) | Out-Null
            [IO.File]::WriteAllText($f, 'evidence content')
            $e = Add-ChainShotFile -Path $f -Tag 'P23' -Note 'Accents: e e a, emoji ok'
            $e.file | Should -BeExactly 'files/evidence.txt'
            $e.sha256 | Should -BeExactly ((Get-FileHash $f -Algorithm SHA256).Hash.ToLower())
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeTrue
        }

        It 'detects a modified file' {
            $c = New-TestCase
            $f = Join-Path $c 'x.bin'; [IO.File]::WriteAllText($f, 'a')
            Add-ChainShotFile -Path $f | Out-Null
            [IO.File]::WriteAllText($f, 'b')
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeFalse
        }

        It 'detects an edited note, a removed entry and a reordering' {
            $c = New-TestCase
            Add-ChainShotNote -CasePath $c -Text 'original' | Out-Null
            Add-ChainShotNote -CasePath $c -Text 'second' | Out-Null
            $lines = [IO.File]::ReadAllLines((Join-Path $c 'journal.jsonl'))

            Write-JournalLines $c (@($lines[0], $lines[1].Replace('original', 'forged'), $lines[2]))
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeFalse

            Write-JournalLines $c (@($lines[0], $lines[2]))
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeFalse

            Write-JournalLines $c (@($lines[0], $lines[2], $lines[1]))
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeFalse

            Write-JournalLines $c $lines
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeTrue
        }

        It 'persists the current tag and applies it' {
            $c = New-TestCase
            Set-ChainShotTag -Tag 'P18' | Out-Null
            (Add-ChainShotNote -CasePath $c -Text 'x').tag | Should -BeExactly 'P18'
            (Get-ChainShotStatus).Tag | Should -BeExactly 'P18'
        }
    }

    Context 'Seals' {
        It 'seals, survives later appends, and refuses to seal a broken journal' {
            $c = New-TestCase
            $s = New-ChainShotSeal -CasePath $c
            $s.Entries | Should -Be 1
            Add-ChainShotNote -CasePath $c -Text 'after seal' | Out-Null
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeTrue

            $f = Join-Path $c 'y.bin'; [IO.File]::WriteAllText($f, 'a')
            Add-ChainShotFile -Path $f | Out-Null
            [IO.File]::WriteAllText($f, 'changed')
            { New-ChainShotSeal -CasePath $c } | Should -Throw
        }
    }

    Context 'Interoperability with the Python reference implementation' {
        It 'produces a case that chainshot.py verifies' {
            if (-not $script:Python) { Set-ItResult -Skipped -Because 'python not installed'; return }
            $c = New-TestCase
            $f = Join-Path $c 'files/e.txt'
            New-Item -ItemType Directory -Force -Path (Split-Path $f) | Out-Null
            [IO.File]::WriteAllText($f, 'interop')
            Add-ChainShotFile -Path $f -Note "quotes `" backslash \ tab `t" | Out-Null
            Add-ChainShotNote -CasePath $c -Text "multi`r`nline" | Out-Null
            New-ChainShotSeal -CasePath $c | Out-Null
            & $script:Python.Source (Join-Path $script:Repo 'python/chainshot.py') verify $c | Out-Null
            $LASTEXITCODE | Should -Be 0
        }

        It 'verifies a case written by chainshot.py' {
            if (-not $script:Python) { Set-ItResult -Skipped -Because 'python not installed'; return }
            $dir = Join-Path ([IO.Path]::GetTempPath()) ("cs-py-" + [guid]::NewGuid())
            $py = Join-Path $script:Repo 'python/chainshot.py'
            & $script:Python.Source $py init $dir --case PYCASE --examiner Py | Out-Null
            & $script:Python.Source $py note $dir 'from python' --tag P01 | Out-Null
            & $script:Python.Source $py seal $dir | Out-Null
            Test-ChainShotCase -CasePath $dir -Quiet | Should -BeTrue
        }
    }

    Context 'Windows capture' {
        It 'captures the screen with a banner and journals it' {
            if (-not $script:IsWin) { Set-ItResult -Skipped -Because 'Windows only'; return }
            $c = New-TestCase
            Set-ChainShotTag -Tag 'P99' | Out-Null
            $e = Invoke-ChainShot -Mode Screen -CasePath $c -Note 'automated test'
            $e.type | Should -BeExactly 'screenshot'
            $e.tag | Should -BeExactly 'P99'
            $png = Join-Path $c $e.file
            Test-Path $png | Should -BeTrue
            Add-Type -AssemblyName System.Drawing
            $img = [System.Drawing.Image]::FromFile($png)
            try {
                $b = $e.extra.bounds.Split(',')
                $img.Width | Should -Be ([int]$b[2])
                $img.Height | Should -BeGreaterThan ([int]$b[3])   # banner added below
            } finally { $img.Dispose() }
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeTrue
        }

        It 'captures all screens, a fixed rectangle and the foreground window' {
            if (-not $script:IsWin) { Set-ItResult -Skipped -Because 'Windows only'; return }
            $c = New-TestCase
            (Invoke-ChainShot -Mode AllScreens -CasePath $c).type | Should -BeExactly 'screenshot'
            $r = Invoke-ChainShot -Rectangle '0,0,200,100' -CasePath $c
            $r.extra.bounds | Should -BeExactly '0,0,200,100'
            try { Invoke-ChainShot -Mode Window -CasePath $c | Out-Null } catch { Write-Warning "Window capture unavailable on this runner: $_" }
            Test-ChainShotCase -CasePath $c -Quiet | Should -BeTrue
        }

        It 'creates a Start menu shortcut that uses the ChainShot icon' {
            if (-not $script:IsWin) { Set-ItResult -Skipped -Because 'Windows only'; return }
            Install-ChainShotShortcut | Out-Null
            $path = Join-Path ([Environment]::GetFolderPath('Programs')) 'ChainShot Tray (PowerShell).lnk'
            try {
                $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($path)
                $lnk.IconLocation | Should -Match 'chainshot\.ico,0$'
                Test-Path ($lnk.IconLocation -replace ',0$', '') | Should -BeTrue
            } finally { Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue }
        }

        It 'starts the tray application and registers its hotkeys' {
            if (-not $script:IsWin) { Set-ItResult -Skipped -Because 'Windows only'; return }
            if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { Set-ItResult -Skipped -Because 'needs an STA thread'; return }
            $r = Start-ChainShotTray -SmokeTest
            $r.MenuItems | Should -BeGreaterThan 10
            $r.HotkeysRegistered.Count | Should -Be 3
        }
    }
}
