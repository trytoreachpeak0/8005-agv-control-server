#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2RunLedger.psm1: two processes appending at the same time lose no line and break
    no line.

.DESCRIPTION
    Two real pwsh processes, a throwaway ledger, a few seconds. No rig.

    This is the one property the ledger exists for. The file control-server#164 worked from lost two
    lines when two background batches appended at once -- 2b's exit and 3b's entry (PR #176) -- and a
    lost line is worse than no ledger at all, because the runs on either side of it still look
    accounted for. Nothing about that failure was visible while it happened.

    A real-rig run holds the machine-wide desktop lock for its whole length, so two of them cannot
    overlap on this machine today and a naive append would pass every real run. That is exactly why
    this check uses processes of its own rather than runs: the property has to hold because of how
    the append is written, not because of a lock that belongs to something else and could be scoped
    differently tomorrow.

    Cases:

      - two processes, 300 lines each -> 600 lines, every one parsable, 300 from each tag, no
        sequence number missing or repeated;
      - a line carrying a line break is refused rather than written (it would read back as two
        records, one of them broken);
      - the default path is under LOCALAPPDATA and outside any checkout, and the override the
        cases above use is the only thing that moves it.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2RunLedger.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$modulePath = Join-Path $PSScriptRoot 'L2RunLedger.psm1'
Import-Module $modulePath -Force

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "l2-run-ledger-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch -Force
$childPath = Join-Path $scratch 'append-many.ps1'
# The filler makes each line ~500 bytes: a short line can slip through an unsafe append intact by
# luck, and this check is not about luck.
Set-Content -LiteralPath $childPath -Encoding utf8NoBOM -Value @'
param([string]$ModulePath, [string]$LedgerPath, [string]$Tag, [int]$Count)
Import-Module $ModulePath -Force
$filler = 'x' * 400
for ($i = 1; $i -le $Count; $i++) {
    $null = Write-L2RunLedgerEvent -Event 'start' -RunId "$Tag-$i" -Path $LedgerPath `
        -Fields @{ tag = $Tag; seq = $i; filler = $filler }
}
'@

$perProcess = 300

function Invoke-TwoWriters([string]$LedgerPath) {
    $arguments = @{
        FilePath     = 'pwsh'
        NoNewWindow  = $true
        PassThru     = $true
        ArgumentList = @()
    }
    $processes = foreach ($tag in 'A', 'B') {
        Start-Process @arguments -ArgumentList @(
            '-NoProfile', '-File', $childPath, $modulePath, $LedgerPath, $tag, $perProcess)
    }
    $processes | Wait-Process -Timeout 180
}

$cases = @()

$cases += @{
    Name  = 'two processes appending at once lose no line and break no line'
    Check = {
        $ledger = Join-Path $scratch 'concurrent.log'
        Invoke-TwoWriters $ledger
        # An append that fails outright leaves no file at all, and that has to read as "0 of 600"
        # rather than as this check throwing on its way to the answer.
        $lines = if (Test-Path -LiteralPath $ledger) { @(Get-Content -LiteralPath $ledger -Encoding utf8) } else { @() }
        $parsed = [System.Collections.Generic.List[object]]::new()
        $broken = 0
        foreach ($line in $lines) {
            try {
                $record = $line | ConvertFrom-Json
                if ($null -eq $record) { $broken++ } else { $parsed.Add($record) }
            } catch { $broken++ }
        }
        # Counted defensively, and that is not tidiness. Two appends landing on one offset produce a
        # line that is still valid JSON with the wrong fields in it -- measured: the unparsable count
        # stays at zero and it is `tag` that comes back null. Reading those fields trustingly made
        # this case die inside the check instead of failing it, so the run that needed explaining
        # reported a ContainsKey error rather than how many records were wrong.
        $byTag = @{ A = 0; B = 0 }
        $malformed = 0
        $sequences = @{ A = [System.Collections.Generic.List[int]]::new(); B = [System.Collections.Generic.List[int]]::new() }
        foreach ($record in $parsed) {
            $tag = if ($record.PSObject.Properties['tag']) { [string]$record.tag } else { '' }
            $seq = if ($record.PSObject.Properties['seq']) { $record.seq -as [int] } else { $null }
            if (-not $byTag.ContainsKey($tag) -or $null -eq $seq) { $malformed++; continue }
            $byTag[$tag]++
            $sequences[$tag].Add($seq)
        }
        $sequencesOk = $true
        foreach ($tag in 'A', 'B') {
            $seen = @($sequences[$tag] | Sort-Object -Unique)
            # Count first and on its own: an append that dropped every line of one writer leaves this
            # empty, and indexing it would throw where the check should simply say so.
            if ($seen.Count -ne $perProcess) { $sequencesOk = $false; continue }
            if ($seen[0] -ne 1 -or $seen[-1] -ne $perProcess) { $sequencesOk = $false }
        }
        @{ Ok     = ($lines.Count -eq (2 * $perProcess) -and $broken -eq 0 -and $malformed -eq 0 -and
                     $byTag.A -eq $perProcess -and $byTag.B -eq $perProcess -and $sequencesOk)
           Actual = "$($lines.Count) line(s) of $(2 * $perProcess), $broken unparsable, $malformed malformed, A=$($byTag.A) B=$($byTag.B), sequences intact=$sequencesOk" }
    }
}

$cases += @{
    Name  = 'a line carrying a line break is refused rather than written'
    Check = {
        $ledger = Join-Path $scratch 'linebreak.log'
        try {
            $null = Add-L2RunLedgerLine -Path $ledger -Line "one`ntwo"
            @{ Ok = $false; Actual = 'it was written' }
        } catch {
            $written = if (Test-Path -LiteralPath $ledger) { @(Get-Content -LiteralPath $ledger).Count } else { 0 }
            @{ Ok = ($_.Exception.Message -like '*one record is one line*' -and $written -eq 0)
               Actual = "threw: $($_.Exception.Message) / $written line(s) on disk" }
        }
    }
}

$cases += @{
    Name  = 'the default path is under LOCALAPPDATA and outside any checkout'
    Check = {
        $saved = $env:W2G_L2_RUN_LEDGER
        try {
            $env:W2G_L2_RUN_LEDGER = $null
            $default = Get-L2RunLedgerPath
            $underLocalAppData = -not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA) -and
                $default.StartsWith($env:LOCALAPPDATA, [StringComparison]::OrdinalIgnoreCase)
            $env:W2G_L2_RUN_LEDGER = Join-Path $scratch 'override.log'
            $overridden = Get-L2RunLedgerPath
            @{ Ok     = ($underLocalAppData -and $overridden -eq $env:W2G_L2_RUN_LEDGER -and $overridden -ne $default)
               Actual = "default '$default' / override honoured=$($overridden -eq (Join-Path $scratch 'override.log'))" }
        } finally { $env:W2G_L2_RUN_LEDGER = $saved }
    }
}

$wrong = 0
try {
    foreach ($case in $cases) {
        try { $result = & $case.Check } catch { $result = @{ Ok = $false; Actual = "threw: $($_.Exception.Message)" } }
        if (-not $result.Ok) { $wrong++ }
        Write-Host ("{0}  {1} -> {2}" -f $(if ($result.Ok) { 'ok  ' } else { 'BAD ' }), $case.Name, $result.Actual)
    }
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($wrong -gt 0) {
    Write-Host "L2RunLedger self-check: $wrong of $($cases.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2RunLedger self-check: all $($cases.Count) cases as expected."
