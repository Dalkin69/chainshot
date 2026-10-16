# SPDX-License-Identifier: MIT
# ChainShot PowerShell module - timestamped, hash-chained forensic screenshots.
#
# Journal format: docs/journal-format.md. Every entry written here must hash
# exactly like the Python reference implementation (python/chainshot.py) and
# the browser extension; tests/vectors/canonical-vectors.json is the contract.
#
# Works on Windows PowerShell 5.1 and PowerShell 7+. Screen capture and the
# tray application are Windows-only; journal, file hashing, verification and
# sealing also work on Linux and macOS with PowerShell 7.
#
# This file is kept pure ASCII on purpose: Windows PowerShell 5.1 reads
# BOM-less scripts with the ANSI code page.

Set-StrictMode -Version 2.0

$script:Version       = '1.1.0'
$script:ToolId        = "chainshot-ps/$($script:Version)"
$script:FormatVersion = '1'
$script:JournalName   = 'journal.jsonl'
$script:CaseFileName  = 'chainshot-case.json'
$script:Genesis       = '0' * 64
$script:ChainedFields = @('v', 'seq', 'utc', 'type', 'tag', 'file', 'size', 'sha256',
                          'examiner', 'host', 'source', 'url', 'title', 'note')
$script:EntryTypes    = @('screenshot', 'browser', 'file', 'note', 'seal', 'timestamp')
$script:Utf8NoBom     = New-Object System.Text.UTF8Encoding($false)
$script:Invariant     = [System.Globalization.CultureInfo]::InvariantCulture
$script:IsWin         = ($PSVersionTable.PSVersion.Major -lt 6) -or $IsWindows
# ChainShot.exe owns 'ChainShot.lnk'; the PowerShell tray gets its own Start menu entry.
$script:ShortcutName  = 'ChainShot Tray (PowerShell).lnk'

# --------------------------------------------------------------------------
# Hashing primitives (must match python/chainshot.py byte for byte)
# --------------------------------------------------------------------------

function ConvertTo-HexString([byte[]]$Bytes) {
    $sb = New-Object System.Text.StringBuilder ($Bytes.Length * 2)
    foreach ($b in $Bytes) { [void]$sb.Append($b.ToString('x2')) }
    return $sb.ToString()
}

function Get-StringSha256([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-HexString $sha.ComputeHash($script:Utf8NoBom.GetBytes($Text)) }
    finally { $sha.Dispose() }
}

function Get-BytesSha256([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ConvertTo-HexString $sha.ComputeHash($Bytes) }
    finally { $sha.Dispose() }
}

function Get-FileDigest([string]$Path) {
    # Returns sha256, md5 and size of a file in one object.
    $item = Get-Item -LiteralPath $Path
    [pscustomobject]@{
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        Md5    = (Get-FileHash -LiteralPath $Path -Algorithm MD5).Hash.ToLowerInvariant()
        Size   = [string]$item.Length
    }
}

function Get-FieldValue($Entry, [string]$Name) {
    # Reads a field from a hashtable or a PSCustomObject; missing fields are ''.
    if ($Entry -is [System.Collections.IDictionary]) {
        if ($Entry.Contains($Name) -and $null -ne $Entry[$Name]) { return [string]$Entry[$Name] }
        return ''
    }
    $p = $Entry.PSObject.Properties[$Name]
    if ($p -and $null -ne $p.Value) { return [string]$p.Value }
    return ''
}

function ConvertTo-SafeValue([string]$Value) {
    # CR and LF are replaced by one space each, as in the reference implementation.
    if ($null -eq $Value) { return '' }
    return $Value.Replace("`r", ' ').Replace("`n", ' ')
}

function Get-ChainShotCanonicalString {
    <#
    .SYNOPSIS
        Canonical text of a journal entry: one key=value line per chained field.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Entry)
    $lines = foreach ($f in $script:ChainedFields) { '{0}={1}' -f $f, (ConvertTo-SafeValue (Get-FieldValue $Entry $f)) }
    return ($lines -join "`n")
}

function Get-ChainShotEntryHash {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Entry)
    return Get-StringSha256 (Get-ChainShotCanonicalString -Entry $Entry)
}

function Get-ChainShotNextChain {
    <#
    .SYNOPSIS
        chain(n) = SHA-256( chain(n-1) + entryHash(n) ), hex lowercase.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PreviousChain, [Parameter(Mandatory)]$Entry)
    return Get-StringSha256 ($PreviousChain + (Get-ChainShotEntryHash -Entry $Entry))
}

function Format-Utc([datetime]$Utc) {
    return $Utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", $script:Invariant)
}

function Format-Local([datetime]$Utc) {
    return $Utc.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", $script:Invariant)
}

# --------------------------------------------------------------------------
# State: current case and current tag (per user)
# --------------------------------------------------------------------------

function Get-StatePath {
    if ($script:IsWin) { $base = Join-Path $env:APPDATA 'ChainShot' }
    else { $base = Join-Path $HOME '.config/chainshot' }
    if (-not (Test-Path -LiteralPath $base)) { New-Item -ItemType Directory -Path $base -Force | Out-Null }
    return (Join-Path $base 'state.json')
}

function Get-State {
    $p = Get-StatePath
    if (Test-Path -LiteralPath $p) {
        try { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { Write-Verbose "Unreadable state file: $_" }
    }
    return [pscustomobject]@{ CasePath = ''; Tag = '' }
}

function Save-State($State) {
    $json = $State | ConvertTo-Json
    [System.IO.File]::WriteAllText((Get-StatePath), $json, $script:Utf8NoBom)
}

function Resolve-CasePath([string]$CasePath) {
    if (-not $CasePath) { $CasePath = (Get-State).CasePath }
    if (-not $CasePath) { throw 'No case selected. Run New-ChainShotCase or Set-ChainShotCase first.' }
    $full = [System.IO.Path]::GetFullPath($CasePath)
    if (-not (Test-Path -LiteralPath (Join-Path $full $script:CaseFileName))) {
        throw "Not a ChainShot case folder (no $($script:CaseFileName)): $full"
    }
    return $full
}

function Get-CaseMeta([string]$CasePath) {
    $p = Join-Path $CasePath $script:CaseFileName
    return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json)
}

# --------------------------------------------------------------------------
# Journal I/O
# --------------------------------------------------------------------------

function ConvertFrom-JournalLine([string]$Line) {
    # PowerShell 7's ConvertFrom-Json turns ISO-8601 strings into DateTime objects,
    # which would change the canonical text. Parse with date handling disabled
    # (Newtonsoft in PowerShell 7; Windows PowerShell 5.1 never converts them).
    if ($PSVersionTable.PSVersion.Major -lt 6) { return ($Line | ConvertFrom-Json) }
    $settings = New-Object Newtonsoft.Json.JsonSerializerSettings
    $settings.DateParseHandling = [Newtonsoft.Json.DateParseHandling]::None
    $obj = [Newtonsoft.Json.JsonConvert]::DeserializeObject($Line, [Newtonsoft.Json.Linq.JObject], $settings)
    return (ConvertFrom-JToken $obj)
}

function ConvertFrom-JToken($Token) {
    # Note: PowerShell adapts JObject as a dictionary, so test types with -is, not .Type.
    if ($Token -is [Newtonsoft.Json.Linq.JObject]) {
        $h = [ordered]@{}
        foreach ($p in $Token.Properties()) { $h[$p.Name] = ConvertFrom-JToken $p.Value }
        return [pscustomobject]$h
    }
    if ($Token -is [Newtonsoft.Json.Linq.JArray]) {
        $items = foreach ($t in $Token.Children()) { ConvertFrom-JToken $t }
        return , @($items)
    }
    if ($Token -is [Newtonsoft.Json.Linq.JValue]) { return $Token.Value }
    return $null
}

function Read-ChainShotJournal {
    <#
    .SYNOPSIS
        Returns the journal entries of a case as objects, in order.
    #>
    [CmdletBinding()]
    param([string]$CasePath)
    $CasePath = Resolve-CasePath $CasePath
    $j = Join-Path $CasePath $script:JournalName
    if (-not (Test-Path -LiteralPath $j)) { return , @() }
    $entries = New-Object System.Collections.Generic.List[object]
    $n = 0
    foreach ($line in [System.IO.File]::ReadAllLines($j, $script:Utf8NoBom)) {
        $n++
        if (-not $line.Trim()) { continue }
        try { $entries.Add((ConvertFrom-JournalLine $line)) }
        catch { throw "$($script:JournalName) line ${n}: invalid JSON" }
    }
    return , $entries.ToArray()
}

function Invoke-WithJournalLock([string]$CasePath, [scriptblock]$Action) {
    # Exclusive lock file so two ChainShot processes never interleave appends.
    $lockPath = Join-Path $CasePath '.journal.lock'
    $stream = $null
    for ($i = 0; $i -lt 50 -and -not $stream; $i++) {
        try { $stream = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $stream) { throw "Journal is locked by another process: $lockPath" }
    try { return & $Action }
    finally { $stream.Dispose() }
}

function Add-JournalEntry {
    # Internal: appends one entry; seq, utc, chain and identity fields are computed here.
    param(
        [Parameter(Mandatory)][string]$CasePath,
        [Parameter(Mandatory)][hashtable]$Fields,
        [hashtable]$Extra,
        [datetime]$UtcNow = ([datetime]::UtcNow)
    )
    if ($script:EntryTypes -notcontains $Fields.type) { throw "Unknown entry type: $($Fields.type)" }
    Invoke-WithJournalLock $CasePath {
        $existing = Read-ChainShotJournal -CasePath $CasePath
        if ($existing.Count -gt 0) { $prev = Get-FieldValue $existing[-1] 'chain' } else { $prev = $script:Genesis }
        $meta = Get-CaseMeta $CasePath
        $entry = [ordered]@{}
        foreach ($f in $script:ChainedFields) { $entry[$f] = '' }
        $entry.v        = $script:FormatVersion
        $entry.seq      = [string]($existing.Count + 1)
        $entry.utc      = Format-Utc $UtcNow
        $entry.examiner = [string]$meta.examiner
        $entry.host     = [System.Environment]::MachineName
        $entry.source   = $script:ToolId
        foreach ($k in $Fields.Keys) {
            if ($script:ChainedFields -contains $k) { $entry[$k] = ConvertTo-SafeValue ([string]$Fields[$k]) }
        }
        $entry.local = Format-Local $UtcNow
        if ($Extra) { $entry.extra = $Extra }
        $entry.chain = Get-ChainShotNextChain -PreviousChain $prev -Entry $entry
        $line = ($entry | ConvertTo-Json -Compress -Depth 5) + "`n"
        [System.IO.File]::AppendAllText((Join-Path $CasePath $script:JournalName), $line, $script:Utf8NoBom)
        [pscustomobject]$entry
    }
}

# --------------------------------------------------------------------------
# Public commands: cases, tags, notes, files
# --------------------------------------------------------------------------

function New-ChainShotCase {
    <#
    .SYNOPSIS
        Creates a case folder and makes it the current case.
    .EXAMPLE
        New-ChainShotCase -Path D:\Cases\TP-FOR-01 -Case 'TP Forensic - PC-COMPTA01' -Examiner 'Louis'
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Case,
        [string]$Examiner = [System.Environment]::UserName
    )
    $full = [System.IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath (Join-Path $full $script:CaseFileName)) { throw "A case already exists in $full" }
    New-Item -ItemType Directory -Force -Path (Join-Path $full 'captures') | Out-Null
    $meta = [ordered]@{
        format = 'chainshot-case'; v = $script:FormatVersion; case = $Case; examiner = $Examiner
        created_utc = (Format-Utc ([datetime]::UtcNow)); tool = $script:ToolId
    }
    [System.IO.File]::WriteAllText((Join-Path $full $script:CaseFileName), (($meta | ConvertTo-Json) + "`n"), $script:Utf8NoBom)
    $null = Add-JournalEntry -CasePath $full -Fields @{ type = 'note'; tag = 'INIT'; note = "Case created: $Case" }
    Set-ChainShotCase -Path $full | Out-Null
    Get-ChainShotStatus
}

function Set-ChainShotCase {
    <#
    .SYNOPSIS
        Selects an existing case folder as the current case.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $full = Resolve-CasePath $Path
    $s = Get-State
    $s = [pscustomobject]@{ CasePath = $full; Tag = (Get-FieldValue $s 'Tag') }
    Save-State $s
    Get-ChainShotStatus
}

function Set-ChainShotTag {
    <#
    .SYNOPSIS
        Sets the tag (e.g. P17) applied to the next captures. Persists across reboots.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Tag)
    $s = Get-State
    Save-State ([pscustomobject]@{ CasePath = (Get-FieldValue $s 'CasePath'); Tag = $Tag })
    Get-ChainShotStatus
}

function Get-ChainShotStatus {
    <#
    .SYNOPSIS
        Shows the current case, tag and number of entries.
    #>
    [CmdletBinding()]
    param()
    $s = Get-State
    $case = Get-FieldValue $s 'CasePath'
    $count = 0; $name = ''
    if ($case -and (Test-Path -LiteralPath (Join-Path $case $script:CaseFileName))) {
        $count = (Read-ChainShotJournal -CasePath $case).Count
        $name = (Get-CaseMeta $case).case
    }
    [pscustomobject]@{ Case = $name; CasePath = $case; Tag = (Get-FieldValue $s 'Tag'); Entries = $count; Version = $script:Version }
}

function Get-CurrentTag([string]$Tag) {
    if ($PSBoundParameters.ContainsKey('Tag') -and $Tag) { return $Tag }
    return (Get-FieldValue (Get-State) 'Tag')
}

function Add-ChainShotNote {
    <#
    .SYNOPSIS
        Appends a text-only entry (an action or observation) to the journal.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text, [string]$Tag, [string]$CasePath)
    $CasePath = Resolve-CasePath $CasePath
    if (-not $PSBoundParameters.ContainsKey('Tag')) { $Tag = Get-CurrentTag }
    Add-JournalEntry -CasePath $CasePath -Fields @{ type = 'note'; tag = $Tag; note = $Text }
}

function Get-StoredPath([string]$CasePath, [string]$FullPath) {
    # Paths inside the case folder are stored relative with '/', others absolute.
    $root = $CasePath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($FullPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $FullPath.Substring($root.Length).Replace('\', '/')
    }
    return $FullPath
}

function Add-ChainShotFile {
    <#
    .SYNOPSIS
        Hashes a file (exported evidence, disk image, report) and appends it to the journal.
    .PARAMETER Copy
        Copy the file into <case>\files\ before hashing it.
    .EXAMPLE
        Add-ChainShotFile -Path D:\Export\interception -Tag P23 -Note 'Ethereal capture exported from Autopsy'
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path, [string]$Tag, [string]$Note = '',
        [switch]$Copy, [string]$CasePath
    )
    $CasePath = Resolve-CasePath $CasePath
    if (-not $PSBoundParameters.ContainsKey('Tag')) { $Tag = Get-CurrentTag }
    $src = (Resolve-Path -LiteralPath $Path).ProviderPath
    if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw "Not a file: $src" }
    $target = $src
    if ($Copy) {
        $dir = Join-Path $CasePath 'files'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $target = Join-Path $dir (Split-Path $src -Leaf)
        if (Test-Path -LiteralPath $target) { throw "Already in case: $target" }
        Copy-Item -LiteralPath $src -Destination $target
    }
    $d = Get-FileDigest $target
    Add-JournalEntry -CasePath $CasePath -Fields @{
        type = 'file'; tag = $Tag; file = (Get-StoredPath $CasePath $target); size = $d.Size; sha256 = $d.Sha256; note = $Note
    } -Extra @{ md5 = $d.Md5 }
}

# --------------------------------------------------------------------------
# Verification and seals
# --------------------------------------------------------------------------

function Get-JournalPrefixSha256([string]$CasePath, [int]$Count) {
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $CasePath $script:JournalName))
    if ($Count -eq 0) { return Get-BytesSha256 ([byte[]]@()) }
    $seen = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 10) {
            $seen++
            if ($seen -eq $Count) {
                $prefix = New-Object byte[] ($i + 1)
                [System.Array]::Copy($bytes, $prefix, $i + 1)
                return Get-BytesSha256 $prefix
            }
        }
    }
    throw "Journal has fewer than $Count lines"
}

function Test-ChainShotCase {
    <#
    .SYNOPSIS
        Verifies every file hash, the hash chain and the seals of a case.
    .DESCRIPTION
        Returns one object per entry and per seal, then prints a summary.
        RFC 3161 timestamps are checked for presence only; use chainshot.py verify --tsa-ca
        to check their signature.
    .OUTPUTS
        Objects with Kind, Seq, Ok, Problems, File. Use -Quiet to get only $true / $false.
    #>
    [CmdletBinding()]
    param([string]$CasePath, [switch]$Quiet)
    $CasePath = Resolve-CasePath $CasePath
    $entries = Read-ChainShotJournal -CasePath $CasePath
    $results = New-Object System.Collections.Generic.List[object]
    $prev = $script:Genesis
    $chains = @()
    $i = 0
    foreach ($e in $entries) {
        $i++
        $problems = @()
        if ((Get-FieldValue $e 'v') -ne $script:FormatVersion) { $problems += "unsupported format version" }
        if ((Get-FieldValue $e 'seq') -ne [string]$i) { $problems += "sequence is '$(Get-FieldValue $e 'seq')', expected $i" }
        $expected = Get-ChainShotNextChain -PreviousChain $prev -Entry $e
        if ((Get-FieldValue $e 'chain') -ne $expected) { $problems += 'CHAIN BROKEN (entry edited, removed or reordered)' }
        $file = Get-FieldValue $e 'file'
        if ($file) {
            if ([System.IO.Path]::IsPathRooted($file)) { $p = $file } else { $p = Join-Path $CasePath $file }
            if (-not (Test-Path -LiteralPath $p)) { $problems += 'FILE MISSING' }
            else {
                $d = Get-FileDigest $p
                if ($d.Sha256 -ne (Get-FieldValue $e 'sha256')) { $problems += 'FILE HASH MISMATCH (file modified)' }
                elseif ((Get-FieldValue $e 'size') -and $d.Size -ne (Get-FieldValue $e 'size')) { $problems += 'SIZE MISMATCH' }
            }
        }
        $results.Add([pscustomobject]@{ Kind = 'entry'; Seq = $i; Utc = (Get-FieldValue $e 'utc'); Tag = (Get-FieldValue $e 'tag');
                                        Ok = ($problems.Count -eq 0); Problems = ($problems -join ' / '); File = $file })
        $prev = Get-FieldValue $e 'chain'
        $chains += $prev
    }

    $sealDir = Join-Path $CasePath 'seals'
    if (Test-Path -LiteralPath $sealDir) {
        foreach ($sp in (Get-ChildItem -LiteralPath $sealDir -Filter 'seal-*.json' | Sort-Object Name)) {
            $problems = @()
            try {
                $seal = Get-Content -LiteralPath $sp.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
                $n = [int]$seal.entries
                if ($n -gt $chains.Count -or ($n -gt 0 -and $chains[$n - 1] -ne $seal.last_chain)) {
                    $problems += 'last_chain does not match the journal'
                } elseif ((Get-JournalPrefixSha256 $CasePath $n) -ne $seal.journal_prefix_sha256) {
                    $problems += 'journal prefix hash differs (journal rewritten)'
                }
            } catch { $problems += "unreadable seal: $_" }
            $tsr = [System.IO.Path]::ChangeExtension($sp.FullName, '.tsr')
            $note = ''
            if (Test-Path -LiteralPath $tsr) { $note = 'RFC 3161 timestamp present (check with chainshot.py verify --tsa-ca)' }
            $results.Add([pscustomobject]@{ Kind = 'seal'; Seq = $null; Utc = ''; Tag = $sp.Name; Ok = ($problems.Count -eq 0);
                                            Problems = ($problems -join ' / '); File = $note })
        }
    }

    $allOk = -not ($results | Where-Object { -not $_.Ok })
    if ($Quiet) { return [bool]$allOk }
    $results
    $journal = Join-Path $CasePath $script:JournalName
    if (Test-Path -LiteralPath $journal) {
        Write-Host ("SHA-256 of {0}: {1}" -f $script:JournalName, (Get-FileHash -LiteralPath $journal -Algorithm SHA256).Hash.ToLowerInvariant())
    }
    if ($allOk) { Write-Host 'RESULT: ALL EVIDENCE INTACT' -ForegroundColor Green }
    else { Write-Host 'RESULT: VERIFICATION FAILED' -ForegroundColor Red }
}

function New-ChainShotSeal {
    <#
    .SYNOPSIS
        Seals the journal: records how many entries exist, the last chain value and
        the hash of the journal at that point. Refuses to seal a journal that does not verify.
    .DESCRIPTION
        The seal survives later appends. Its SHA-256 is what you write in your signed
        report. For an RFC 3161 timestamp, use: python chainshot.py seal <case> --tsa
    #>
    [CmdletBinding()]
    param([string]$CasePath, [string]$Tag = 'SEAL')
    $CasePath = Resolve-CasePath $CasePath
    if (-not (Test-ChainShotCase -CasePath $CasePath -Quiet)) { throw 'The journal does not verify; refusing to seal it. Run Test-ChainShotCase.' }
    $entries = Read-ChainShotJournal -CasePath $CasePath
    $n = $entries.Count
    $now = [datetime]::UtcNow
    if ($n -gt 0) { $last = Get-FieldValue $entries[-1] 'chain' } else { $last = $script:Genesis }
    $meta = Get-CaseMeta $CasePath
    $seal = [ordered]@{
        format = 'chainshot-seal'; v = $script:FormatVersion; case = [string]$meta.case; utc = (Format-Utc $now)
        entries = [string]$n; last_chain = $last; journal_prefix_sha256 = (Get-JournalPrefixSha256 $CasePath $n)
        sealed_by = [string]$meta.examiner; tool = $script:ToolId
    }
    $dir = Join-Path $CasePath 'seals'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $name = 'seal-' + $now.ToString("yyyyMMdd'T'HHmmssfff'Z'", $script:Invariant) + '.json'
    $path = Join-Path $dir $name
    [System.IO.File]::WriteAllText($path, (($seal | ConvertTo-Json) + "`n"), $script:Utf8NoBom)
    $d = Get-FileDigest $path
    $null = Add-JournalEntry -CasePath $CasePath -Fields @{
        type = 'seal'; tag = $Tag; file = "seals/$name"; size = $d.Size; sha256 = $d.Sha256; note = "Seal of entries 1-$n"
    }
    [pscustomobject]@{ Seal = $path; Entries = $n; Sha256 = $d.Sha256 }
}

# --------------------------------------------------------------------------
# Windows capture
# --------------------------------------------------------------------------

$script:NativeSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ChainShotNative
{
    public static class Win32
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT rect, int size);

        // Per-monitor DPI awareness so that captures use physical pixels on scaled screens.
        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
        }

        // Visible bounds of a window (without the invisible resize border on Windows 10/11).
        public static int[] GetWindowBounds(IntPtr hWnd)
        {
            RECT r;
            if (DwmGetWindowAttribute(hWnd, 9, out r, Marshal.SizeOf(typeof(RECT))) != 0) { GetWindowRect(hWnd, out r); }
            return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
        }

        public static string GetTitle(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static int GetProcessId(IntPtr hWnd)
        {
            uint pid; GetWindowThreadProcessId(hWnd, out pid); return (int)pid;
        }
    }

    // Global hotkeys on a dedicated thread with its own message loop.
    // The UI thread polls TryDequeue() from a timer: no cross-thread callbacks into PowerShell.
    public sealed class HotkeyListener : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        const uint WM_HOTKEY = 0x0312, WM_QUIT = 0x0012;
        readonly Queue<int> pressed = new Queue<int>();
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly Thread thread;
        uint threadId;
        public bool[] Registered;

        // ids, modifiers (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_NOREPEAT=0x4000) and virtual keys.
        public HotkeyListener(int[] ids, uint[] modifiers, uint[] keys)
        {
            Registered = new bool[ids.Length];
            thread = new Thread(() =>
            {
                threadId = GetCurrentThreadId();
                for (int i = 0; i < ids.Length; i++) Registered[i] = RegisterHotKey(IntPtr.Zero, ids[i], modifiers[i] | 0x4000, keys[i]);
                ready.Set();
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0)
                {
                    if (m.message == WM_HOTKEY) lock (pressed) pressed.Enqueue(m.wParam.ToInt32());
                }
                for (int i = 0; i < ids.Length; i++) if (Registered[i]) UnregisterHotKey(IntPtr.Zero, ids[i]);
            });
            thread.IsBackground = true;
            thread.Start();
            ready.WaitOne(5000);
        }

        public int TryDequeue()
        {
            lock (pressed) return pressed.Count > 0 ? pressed.Dequeue() : 0;
        }

        public void Dispose()
        {
            if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
        }
    }
}
'@

function Initialize-ChainShotNative {
    if (-not ('ChainShotNative.Win32' -as [type])) {
        Add-Type -TypeDefinition $script:NativeSource -Language CSharp
    }
    if ($script:IsWin) {
        Add-Type -AssemblyName System.Windows.Forms, System.Drawing
        [ChainShotNative.Win32]::EnableDpiAwareness()
    }
}

function Assert-Windows {
    if (-not $script:IsWin) { throw 'Screen capture is only available on Windows. Use Add-ChainShotFile to log screenshots taken with another tool.' }
}

function Get-CaptureBitmap([System.Drawing.Rectangle]$Bounds) {
    $bmp = New-Object System.Drawing.Bitmap ($Bounds.Width, $Bounds.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.CopyFromScreen($Bounds.Left, $Bounds.Top, 0, 0, $Bounds.Size) } finally { $g.Dispose() }
    return $bmp
}

function Add-Banner([System.Drawing.Bitmap]$Shot, [string[]]$Lines) {
    # Draws the banner BELOW the capture so that no captured pixel is hidden.
    $lineHeight = 26
    $height = 12 + $lineHeight * $Lines.Count
    $out = New-Object System.Drawing.Bitmap ($Shot.Width, ($Shot.Height + $height))
    $g = [System.Drawing.Graphics]::FromImage($out)
    try {
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $g.DrawImage($Shot, 0, 0, $Shot.Width, $Shot.Height)
        $g.FillRectangle([System.Drawing.Brushes]::Black, 0, $Shot.Height, $Shot.Width, $height)
        $font = New-Object System.Drawing.Font ('Consolas', 13, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        try {
            for ($i = 0; $i -lt $Lines.Count; $i++) {
                if ($i -eq 0) { $brush = [System.Drawing.Brushes]::Yellow } else { $brush = [System.Drawing.Brushes]::Gainsboro }
                $g.DrawString($Lines[$i], $font, $brush, 10, ($Shot.Height + 8 + $i * $lineHeight))
            }
        } finally { $font.Dispose() }
    } finally { $g.Dispose() }
    return $out
}

function Select-ScreenRegion {
    <#
    .SYNOPSIS
        Freezes the screen and lets the user draw a rectangle. Returns the frozen bitmap
        and the selected rectangle, or $null if Esc is pressed.
    #>
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $frozen = Get-CaptureBitmap $vs
    $state = @{ Start = $null; Rect = [System.Drawing.Rectangle]::Empty; Done = $false }

    $form = New-Object System.Windows.Forms.Form
    $form.FormBorderStyle = 'None'; $form.StartPosition = 'Manual'; $form.TopMost = $true; $form.ShowInTaskbar = $false
    $form.Bounds = $vs; $form.Cursor = [System.Windows.Forms.Cursors]::Cross; $form.KeyPreview = $true
    $form.GetType().GetProperty('DoubleBuffered', [System.Reflection.BindingFlags]'Instance,NonPublic').SetValue($form, $true, $null)
    $shade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 0, 0, 0))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Gold, 2)

    $form.Add_Paint({
        param($s, $e)
        $e.Graphics.DrawImage($frozen, 0, 0, $vs.Width, $vs.Height)
        $r = $state.Rect
        $region = New-Object System.Drawing.Region (New-Object System.Drawing.Rectangle (0, 0, $vs.Width, $vs.Height))
        if ($r.Width -gt 0 -and $r.Height -gt 0) { $region.Exclude($r); $e.Graphics.DrawRectangle($pen, $r) }
        $e.Graphics.FillRegion($shade, $region)
        $region.Dispose()
    })
    $form.Add_MouseDown({ param($s, $e) $state.Start = $e.Location })
    $form.Add_MouseMove({
        param($s, $e)
        if ($null -ne $state.Start) {
            $x = [Math]::Min($state.Start.X, $e.X); $y = [Math]::Min($state.Start.Y, $e.Y)
            $state.Rect = New-Object System.Drawing.Rectangle ($x, $y, [Math]::Abs($e.X - $state.Start.X), [Math]::Abs($e.Y - $state.Start.Y))
            $form.Invalidate()
        }
    })
    $form.Add_MouseUp({ $state.Done = $true; $form.Close() })
    $form.Add_KeyDown({ param($s, $e) if ($e.KeyCode -eq 'Escape') { $state.Rect = [System.Drawing.Rectangle]::Empty; $form.Close() } })
    [void]$form.ShowDialog()
    $form.Dispose(); $shade.Dispose(); $pen.Dispose()

    if (-not $state.Done -or $state.Rect.Width -lt 4 -or $state.Rect.Height -lt 4) { $frozen.Dispose(); return $null }
    # Rectangle in virtual-screen coordinates, plus the frozen image to crop from.
    $abs = New-Object System.Drawing.Rectangle (($state.Rect.X + $vs.X), ($state.Rect.Y + $vs.Y), $state.Rect.Width, $state.Rect.Height)
    return [pscustomobject]@{ Frozen = $frozen; Rect = $state.Rect; Absolute = $abs }
}

function Invoke-ChainShot {
    <#
    .SYNOPSIS
        Takes a timestamped screenshot, adds a banner, hashes it and appends it to the journal.
    .PARAMETER Mode
        Screen     = monitor under the mouse (default)
        AllScreens = every monitor
        Window     = foreground window (use -Delay to switch to it)
        Region     = draw a rectangle on a frozen copy of the screen (Esc cancels)
    .PARAMETER Rectangle
        Non-interactive region: 'x,y,width,height' in virtual-screen pixels.
    .EXAMPLE
        Invoke-ChainShot -Mode Window -Delay 3 -Note 'Autopsy - Installed Programs'
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('Screen', 'AllScreens', 'Window', 'Region')][string]$Mode = 'Screen',
        [string]$Rectangle, [int]$Delay = 0, [string]$Tag, [string]$Note = '', [string]$CasePath
    )
    Assert-Windows
    Initialize-ChainShotNative
    $CasePath = Resolve-CasePath $CasePath
    if (-not $PSBoundParameters.ContainsKey('Tag')) { $Tag = Get-CurrentTag }
    if ($Delay -gt 0) { Start-Sleep -Seconds $Delay }

    $title = ''; $extra = @{ mode = $Mode }
    $shot = $null
    if ($Rectangle) {
        $p = $Rectangle.Split(',') | ForEach-Object { [int]$_.Trim() }
        $bounds = New-Object System.Drawing.Rectangle ($p[0], $p[1], $p[2], $p[3])
        $extra.mode = 'Region'
    } else {
        switch ($Mode) {
            'AllScreens' { $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen }
            'Window' {
                $h = [ChainShotNative.Win32]::GetForegroundWindow()
                $b = [ChainShotNative.Win32]::GetWindowBounds($h)
                $bounds = New-Object System.Drawing.Rectangle ($b[0], $b[1], $b[2], $b[3])
                $title = [ChainShotNative.Win32]::GetTitle($h)
                try { $extra.process = (Get-Process -Id ([ChainShotNative.Win32]::GetProcessId($h)) -ErrorAction Stop).ProcessName } catch { $extra.process = '' }
            }
            'Region' {
                $sel = Select-ScreenRegion
                if (-not $sel) { Write-Warning 'Region capture cancelled.'; return }
                $bounds = $sel.Absolute
                $shot = $sel.Frozen.Clone($sel.Rect, $sel.Frozen.PixelFormat)
                $sel.Frozen.Dispose()
            }
            default {
                $bounds = [System.Windows.Forms.Screen]::FromPoint([System.Windows.Forms.Cursor]::Position).Bounds
            }
        }
    }
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw "Invalid capture area: $bounds" }
    $utc = [datetime]::UtcNow
    if (-not $shot) { $shot = Get-CaptureBitmap $bounds }
    $extra.bounds = '{0},{1},{2},{3}' -f $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height

    $meta = Get-CaseMeta $CasePath
    # Banner number = journal entry number (entry 1 is the case creation note);
    # the capture number counts screenshots only.
    $journal = @(Read-ChainShotJournal -CasePath $CasePath)
    $seq = $journal.Count + 1
    $shotNo = @($journal | Where-Object { @('screenshot', 'browser') -contains (Get-FieldValue $_ 'type') }).Count + 1
    $lines = @(('#{0} (capture {7}) | {1} | {2} | {3} UTC ({4}) | {5} @ {6}' -f $seq, $meta.case, $Tag,
                $utc.ToString('yyyy-MM-dd HH:mm:ss', $script:Invariant), $utc.ToLocalTime().ToString('HH:mm:ss zzz', $script:Invariant),
                $meta.examiner, [System.Environment]::MachineName, $shotNo))
    if ($title) { $lines += ('Window: {0} [{1}]' -f $title, $extra.process) }
    if ($Note) { $lines += ('Note: ' + $Note) }
    $final = Add-Banner $shot $lines
    $shot.Dispose()

    $safeTag = ($Tag -replace '[^\w\-]', '_')
    if (-not $safeTag) { $safeTag = 'untagged' }
    $name = '{0:D4}_{1}_{2}.png' -f $seq, $utc.ToString("yyyyMMdd'T'HHmmssfff'Z'", $script:Invariant), $safeTag
    $dir = Join-Path $CasePath 'captures'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir $name
    try { $final.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $final.Dispose() }

    $d = Get-FileDigest $path
    $extra.md5 = $d.Md5
    $entry = Add-JournalEntry -CasePath $CasePath -UtcNow $utc -Fields @{
        type = 'screenshot'; tag = $Tag; file = "captures/$name"; size = $d.Size; sha256 = $d.Sha256; title = $title; note = $Note
    } -Extra $extra
    if ($entry.seq -ne [string]$seq) { Write-Warning "Entry number changed during capture (banner shows #$seq, journal #$($entry.seq))." }
    $entry
}

# --------------------------------------------------------------------------
# Report (delegates to the Python reference implementation)
# --------------------------------------------------------------------------

function Get-ChainShotPython {
    $script = @((Join-Path $PSScriptRoot 'chainshot.py'), (Join-Path $PSScriptRoot '../../python/chainshot.py')) |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    $exe = @('py', 'python3', 'python') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
    if (-not $script -or -not $exe) { return $null }
    return [pscustomobject]@{ Exe = $exe.Source; Script = (Resolve-Path -LiteralPath $script).ProviderPath }
}

function Open-InBrowser([string]$Path) {
    # The .html file association may point to an editor (VS Code, Notepad++): open the
    # report with the browser registered for https links instead.
    $uri = ([System.Uri](Resolve-Path -LiteralPath $Path).ProviderPath).AbsoluteUri
    if ($script:IsWin) {
        try {
            $key = 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice'
            $progId = (Get-ItemProperty -Path $key -ErrorAction Stop).ProgId
            $command = (Get-ItemProperty -Path "Registry::HKEY_CLASSES_ROOT\$progId\shell\open\command" -ErrorAction Stop).'(default)'
            if ($command -match '^\s*"([^"]+)"') { $exe = $Matches[1] } else { $exe = ($command -split '\s+')[0] }
            if ($exe -and (Test-Path -LiteralPath $exe)) { Start-Process -FilePath $exe -ArgumentList ('"{0}"' -f $uri); return }
        } catch { Write-Verbose "Default browser not found, using the file association: $_" }
    }
    Invoke-Item -LiteralPath $Path
}

function Export-ChainShotReport {
    <#
    .SYNOPSIS
        Generates <case>\report\index.html with chainshot.py (Python 3.9+ required).
    #>
    [CmdletBinding()]
    param([string]$CasePath, [switch]$Open)
    $CasePath = Resolve-CasePath $CasePath
    $py = Get-ChainShotPython
    if (-not $py) { throw 'Python 3.9+ and chainshot.py are required for the HTML report (see README).' }
    & $py.Exe $py.Script report $CasePath
    $html = Join-Path $CasePath 'report/index.html'
    if ($Open -and (Test-Path -LiteralPath $html)) { Open-InBrowser $html }
    return $html
}

# --------------------------------------------------------------------------
# Tray application
# --------------------------------------------------------------------------

function New-TrayIcon {
    # Uses chainshot.ico shipped with the module; draws a fallback if it is missing.
    $ico = Join-Path $PSScriptRoot 'chainshot.ico'
    if (Test-Path -LiteralPath $ico) {
        return New-Object System.Drawing.Icon ($ico, [System.Windows.Forms.SystemInformation]::SmallIconSize)
    }
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $navy = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(43, 76, 126))
    $g.FillRectangle($navy, 1, 7, 30, 21)
    $g.FillRectangle($navy, 10, 3, 12, 6)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Gold, 3)
    $g.DrawEllipse($pen, 9, 11, 14, 14)
    $g.Dispose(); $pen.Dispose(); $navy.Dispose()
    return [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}

function Read-TextDialog([string]$Title, [string]$Prompt, [string]$Default = '') {
    $f = New-Object System.Windows.Forms.Form
    $f.Text = $Title; $f.Width = 420; $f.Height = 150; $f.StartPosition = 'CenterScreen'; $f.TopMost = $true
    $f.FormBorderStyle = 'FixedDialog'; $f.MaximizeBox = $false; $f.MinimizeBox = $false
    $l = New-Object System.Windows.Forms.Label -Property @{ Text = $Prompt; Left = 10; Top = 10; Width = 380 }
    $t = New-Object System.Windows.Forms.TextBox -Property @{ Text = $Default; Left = 10; Top = 34; Width = 380 }
    $ok = New-Object System.Windows.Forms.Button -Property @{ Text = 'OK'; Left = 230; Top = 70; Width = 75; DialogResult = 'OK' }
    $ca = New-Object System.Windows.Forms.Button -Property @{ Text = 'Cancel'; Left = 315; Top = 70; Width = 75; DialogResult = 'Cancel' }
    $f.Controls.AddRange(@($l, $t, $ok, $ca)); $f.AcceptButton = $ok; $f.CancelButton = $ca
    $r = $f.ShowDialog(); $text = $t.Text; $f.Dispose()
    if ($r -eq 'OK') { return $text }
    return $null
}

function Start-ChainShotTray {
    <#
    .SYNOPSIS
        Starts the ChainShot tray application with global hotkeys.
    .DESCRIPTION
        Ctrl+Alt+P  screen under the mouse
        Ctrl+Alt+W  foreground window
        Ctrl+Alt+R  region
        The tray menu sets the tag, adds notes, verifies, seals and opens the report.
    .PARAMETER SmokeTest
        Builds the tray, registers the hotkeys, then exits after two seconds and returns
        a status object (used by automated tests).
    #>
    [CmdletBinding()]
    param([switch]$SmokeTest)
    Assert-Windows
    if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
        throw 'The tray needs an STA thread: start it with  powershell.exe -STA  or  pwsh -STA'
    }
    $created = $false
    $mutex = New-Object System.Threading.Mutex($true, 'Local\ChainShotTray', [ref]$created)
    if (-not $created -and -not $SmokeTest) { Write-Warning 'ChainShot tray is already running.'; return }
    Initialize-ChainShotNative

    $tray = New-Object System.Windows.Forms.NotifyIcon
    $tray.Icon = New-TrayIcon
    $tray.Visible = $true
    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $tray.ContextMenuStrip = $menu

    $notify = {
        param([string]$Text, [string]$Kind = 'Info')
        $tray.BalloonTipTitle = 'ChainShot'; $tray.BalloonTipText = $Text; $tray.BalloonTipIcon = $Kind
        $tray.ShowBalloonTip(3000)
    }
    $capture = {
        param([string]$Mode)
        try {
            $e = Invoke-ChainShot -Mode $Mode
            if ($e) { & $notify ("#{0} {1}`nSHA-256 {2}..." -f $e.seq, $e.file, $e.sha256.Substring(0, 16)) }
        } catch { & $notify ("Capture failed: {0}" -f $_.Exception.Message) 'Error' }
    }
    $refresh = {
        $st = Get-ChainShotStatus
        $tray.Text = ('ChainShot - {0} - tag {1}' -f $st.Case, $st.Tag)
        if ($tray.Text.Length -gt 63) { $tray.Text = $tray.Text.Substring(0, 63) }
    }
    $add = {
        param([string]$Text, [scriptblock]$Action)
        # Handlers run while Application.Run() is on this function's stack, so they
        # resolve $tray, $capture, $notify... dynamically; no closure needed.
        $item = $menu.Items.Add($Text)
        $item.Add_Click($Action)
    }

    & $add 'Capture screen       Ctrl+Alt+P' { Start-Sleep -Milliseconds 250; & $capture 'Screen' }
    & $add 'Capture window       Ctrl+Alt+W' { Start-Sleep -Milliseconds 250; & $capture 'Window' }
    & $add 'Capture region       Ctrl+Alt+R' { Start-Sleep -Milliseconds 250; & $capture 'Region' }
    [void]$menu.Items.Add('-')
    & $add 'Set tag...' {
        $t = Read-TextDialog 'ChainShot' 'Tag for the next captures (e.g. P17):' (Get-ChainShotStatus).Tag
        if ($null -ne $t) { Set-ChainShotTag -Tag $t | Out-Null; & $refresh }
    }
    & $add 'Add note to journal...' {
        $t = Read-TextDialog 'ChainShot' 'Note (action, observation):'
        if ($t) { try { $e = Add-ChainShotNote -Text $t; & $notify "#$($e.seq) note recorded" } catch { & $notify $_.Exception.Message 'Error' } }
    }
    & $add 'Hash a file...' {
        $d = New-Object System.Windows.Forms.OpenFileDialog
        if ($d.ShowDialog() -eq 'OK') {
            try { $e = Add-ChainShotFile -Path $d.FileName; & $notify "#$($e.seq) $($d.SafeFileName) hashed" } catch { & $notify $_.Exception.Message 'Error' }
        }
    }
    [void]$menu.Items.Add('-')
    & $add 'Verify case' {
        try {
            if (Test-ChainShotCase -Quiet) { & $notify 'All evidence intact.' }
            else { & $notify 'VERIFICATION FAILED: run Test-ChainShotCase for details.' 'Error' }
        } catch { & $notify $_.Exception.Message 'Error' }
    }
    & $add 'Seal journal' {
        try { $s = New-ChainShotSeal; & $notify ("Sealed {0} entries`nSHA-256 {1}" -f $s.Entries, $s.Sha256) } catch { & $notify $_.Exception.Message 'Error' }
    }
    & $add 'Open HTML report' { try { Export-ChainShotReport -Open | Out-Null } catch { & $notify $_.Exception.Message 'Error' } }
    & $add 'Open case folder' { try { Invoke-Item -LiteralPath (Get-ChainShotStatus).CasePath } catch { & $notify $_.Exception.Message 'Error' } }
    & $add 'Switch case...' {
        $d = New-Object System.Windows.Forms.FolderBrowserDialog
        $d.Description = 'Select a ChainShot case folder'
        if ($d.ShowDialog() -eq 'OK') { try { Set-ChainShotCase -Path $d.SelectedPath | Out-Null; & $refresh } catch { & $notify $_.Exception.Message 'Error' } }
    }
    [void]$menu.Items.Add('-')
    $exitItem = $menu.Items.Add('Exit')
    $exitItem.Add_Click({ [System.Windows.Forms.Application]::ExitThread() })

    # Hotkey ids: 1 = screen, 2 = window, 3 = region. MOD_CONTROL|MOD_ALT = 3. Keys P, W, R.
    $listener = New-Object ChainShotNative.HotkeyListener (
        [int[]](1, 2, 3), [uint32[]](3, 3, 3), [uint32[]](0x50, 0x57, 0x52))
    $failed = @()
    for ($i = 0; $i -lt 3; $i++) { if (-not $listener.Registered[$i]) { $failed += @('Ctrl+Alt+P', 'Ctrl+Alt+W', 'Ctrl+Alt+R')[$i] } }

    $timer = New-Object System.Windows.Forms.Timer
    $timer.Interval = 60
    $modes = @{ 1 = 'Screen'; 2 = 'Window'; 3 = 'Region' }
    $timer.Add_Tick({
        $id = $listener.TryDequeue()
        while ($id -ne 0) { & $capture $modes[$id]; $id = $listener.TryDequeue() }
    })
    $timer.Start()

    if ($SmokeTest) {
        $stopper = New-Object System.Windows.Forms.Timer
        $stopper.Interval = 2000
        $stopper.Add_Tick({ $stopper.Stop(); [System.Windows.Forms.Application]::ExitThread() })
        $stopper.Start()
    } else {
        try { & $refresh } catch { $tray.Text = 'ChainShot - no case selected' }
        if ($failed) { & $notify ("Hotkeys already used by another program: {0}. Use the tray menu." -f ($failed -join ', ')) 'Warning' }
        else { & $notify 'Ready: Ctrl+Alt+P screen, Ctrl+Alt+W window, Ctrl+Alt+R region.' }
    }

    try { [System.Windows.Forms.Application]::Run() }
    finally {
        $timer.Stop(); $listener.Dispose()
        $tray.Visible = $false; $tray.Dispose()
        if ($created) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
    if ($SmokeTest) {
        return [pscustomobject]@{ MenuItems = $menu.Items.Count; HotkeysRegistered = @($listener.Registered); Failed = $failed }
    }
}

function Install-ChainShotShortcut {
    <#
    .SYNOPSIS
        Creates Start menu (and optionally Startup) shortcuts that launch the tray application.
    #>
    [CmdletBinding()]
    param([switch]$Startup)
    Assert-Windows
    $exe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $manifest = Join-Path $PSScriptRoot 'ChainShot.psd1'
    $arguments = '-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -Command "Import-Module ''{0}''; Start-ChainShotTray"' -f $manifest
    $ws = New-Object -ComObject WScript.Shell
    $targets = @([Environment]::GetFolderPath('Programs'))
    if ($Startup) { $targets += [Environment]::GetFolderPath('Startup') }
    foreach ($dir in $targets) {
        $lnk = $ws.CreateShortcut((Join-Path $dir $script:ShortcutName))
        $lnk.TargetPath = $exe; $lnk.Arguments = $arguments; $lnk.WindowStyle = 7
        $ico = Join-Path $PSScriptRoot 'chainshot.ico'
        if (Test-Path -LiteralPath $ico) { $lnk.IconLocation = "$ico,0" }
        $lnk.Description = 'ChainShot - timestamped, hash-chained forensic screenshots'
        $lnk.Save()
        Write-Host "Shortcut created: $(Join-Path $dir $script:ShortcutName)"
    }
}

Export-ModuleMember -Function @(
    'New-ChainShotCase', 'Set-ChainShotCase', 'Set-ChainShotTag', 'Get-ChainShotStatus',
    'Invoke-ChainShot', 'Add-ChainShotFile', 'Add-ChainShotNote', 'Read-ChainShotJournal',
    'Test-ChainShotCase', 'New-ChainShotSeal', 'Export-ChainShotReport',
    'Start-ChainShotTray', 'Install-ChainShotShortcut',
    'Get-ChainShotCanonicalString', 'Get-ChainShotEntryHash', 'Get-ChainShotNextChain'
)
