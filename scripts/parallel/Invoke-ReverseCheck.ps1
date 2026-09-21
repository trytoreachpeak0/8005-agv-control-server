#Requires -Version 7

<#
.SYNOPSIS
    Reverse check for control-server#262 acceptance items 3 and 4, run against the real file
    on disk rather than against an in-memory copy.

.DESCRIPTION
    Test-ParallelInstance.ps1 corrupts a parsed copy of the definition. That covers the
    validator, but it does not cover the thing the acceptance items actually ask about: take
    the RouteGraph configuration away *from the file the deployment reads*, name a vehicle
    other than agv02/agv03 *in that file*, and the deployment must refuse.

    So each case here edits instance-factory01-v2.json in place and reports, in order:

      1. the key's value before and after, and `git diff --stat`, so that an injection which
         did not land is visible as itself rather than looking like a weak assertion;
      2. whether Assert-ParallelInstanceDefinition threw, and with which message -- so the
         answer to "which conjunct made it red" is in the output, not inferred;
      3. the file restored from a byte-for-byte backup taken before the first edit, verified
         by git blob hash. Restoring with `git checkout --` would also discard any uncommitted
         real work in the same file.

    A case that passes proves the deployment refuses that corruption. The run as a whole only
    means something because the untouched file is asserted to be accepted first.

.EXAMPLE
    pwsh -File scripts/parallel/Invoke-ReverseCheck.ps1
#>
[CmdletBinding()]
param(
    [string] $DefinitionPath = (Join-Path $PSScriptRoot 'instance-factory01-v2.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$resolved = (Resolve-Path -LiteralPath $DefinitionPath).Path
$relative = [IO.Path]::GetRelativePath($repoRoot, $resolved).Replace('\', '/')

function Get-BlobHash {
    $hash = (& git -C $repoRoot hash-object $resolved).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'git hash-object failed.' }
    return $hash
}

function Write-Section { param([string] $Text) Write-Host ''; Write-Host "=== $Text ===" -ForegroundColor Cyan }

$backupPath = Join-Path ([IO.Path]::GetTempPath()) "instance-backup-$([guid]::NewGuid().ToString('N')).json"
[IO.File]::Copy($resolved, $backupPath, $true)
$baselineHash = Get-BlobHash

Write-Host "File      : $relative"
Write-Host "Blob      : $baselineHash"
Write-Host "Backup    : $backupPath"

$failed = 0
$passed = 0

function Restore-Definition {
    [IO.File]::Copy($backupPath, $resolved, $true)
    $now = Get-BlobHash
    if ($now -ne $baselineHash) {
        throw "Restore failed: blob is $now, expected $baselineHash."
    }
}

function Write-DefinitionFile {
    param($Tree)
    # utf8 without BOM, LF: .gitattributes stores *.json with LF, and a BOM would survive every
    # check this repository has -- the compiler eats it and dotnet format cannot see it.
    $json = (ConvertTo-Json -InputObject $Tree -Depth 12).Replace("`r`n", "`n")
    [IO.File]::WriteAllText($resolved, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Invoke-Case {
    param(
        [string] $Name,
        [scriptblock] $Mutate,
        [string] $Observe,
        [string] $ExpectFragment,
        [switch] $ExpectAccepted
    )

    Write-Section $Name
    Write-Host "  key    : $Observe"

    $tree = Get-Content -LiteralPath $resolved -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    $mutated = & $Mutate $tree
    Write-DefinitionFile $mutated

    # Proof the injection reached the file, not just a variable.
    $afterHash = Get-BlobHash
    if ($afterHash -eq $baselineHash) {
        Write-Host '  FAIL   the edit did not change the file on disk' -ForegroundColor Red
        $script:failed++
        Restore-Definition
        return
    }
    Write-Host "  blob   : $baselineHash -> $afterHash"
    # git diff only speaks about tracked files. Until this definition is committed the blob
    # hashes above are the whole evidence that the edit landed, and saying so beats printing an
    # empty numstat that reads like "nothing changed".
    $stat = (& git -C $repoRoot diff --numstat -- $relative) -join ' '
    Write-Host ("  numstat: " + ($stat ? $stat : '(file not tracked yet; the blob hashes above are the evidence)'))

    $reloaded = Read-ParallelInstanceDefinition -Path $resolved
    $threw = $false
    $message = ''
    try {
        $null = Assert-ParallelInstanceDefinition -Definition $reloaded
    } catch {
        $threw = $true
        $message = $_.Exception.Message
    }

    if ($ExpectAccepted) {
        if ($threw) {
            Write-Host "  FAIL   expected acceptance, got: $message" -ForegroundColor Red
            $script:failed++
        } else {
            Write-Host '  PASS   accepted, as expected' -ForegroundColor Green
            $script:passed++
        }
    } elseif (-not $threw) {
        Write-Host '  FAIL   the corrupted file was ACCEPTED' -ForegroundColor Red
        $script:failed++
    } else {
        $reasons = @($message -split "`r?`n" | Where-Object { $_ -like '  - *' })
        Write-Host "  refused with $($reasons.Count) reason(s):"
        $reasons | ForEach-Object { Write-Host "         $($_.Trim())" }
        if ($message -like "*$ExpectFragment*") {
            Write-Host "  PASS   refused for the expected reason ('$ExpectFragment')" -ForegroundColor Green
            $script:passed++
        } else {
            Write-Host "  FAIL   refused, but not for '$ExpectFragment'" -ForegroundColor Red
            $script:failed++
        }
    }

    Restore-Definition
    Write-Host "  restored, blob back to $baselineHash"
}

# ---------------------------------------------------------------------------------
# Case 0. The rewrite itself must not change the verdict. Without this, every case
# below could be passing because ConvertTo-Json reshaped the file, not because of the
# injection -- and the whole run would prove nothing about the checks.
# ---------------------------------------------------------------------------------
Write-Section 'case 0: round-trip with no injection is still accepted'
$tree = Get-Content -LiteralPath $resolved -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
Write-DefinitionFile $tree
$roundTripHash = Get-BlobHash
Write-Host "  blob   : $baselineHash -> $roundTripHash (reformatted, same content)"
try {
    $null = Assert-ParallelInstanceDefinition -Definition (Read-ParallelInstanceDefinition -Path $resolved)
    Write-Host '  PASS   the reformatted file is accepted' -ForegroundColor Green
    $passed++
} catch {
    Write-Host "  FAIL   the reformat alone was refused: $($_.Exception.Message)" -ForegroundColor Red
    $failed++
}
Restore-Definition

# --------------------------------------------- acceptance item 3: RouteGraph ---

Invoke-Case -Name 'case 1: the whole routeGraph section removed' `
    -Observe 'routeGraph' `
    -Mutate { param($t) $t.Remove('routeGraph'); $t } `
    -ExpectFragment 'routeGraph must be an object'

Invoke-Case -Name 'case 2: routeGraph present but empty -- the "configured" look without a decision' `
    -Observe 'routeGraph' `
    -Mutate { param($t) $t['routeGraph'] = @{}; $t } `
    -ExpectFragment 'routeGraph.enabled must be stated explicitly'

Invoke-Case -Name 'case 3: routeGraph.enabled written as the string "false"' `
    -Observe 'routeGraph.enabled' `
    -Mutate { param($t) $t['routeGraph']['enabled'] = 'false'; $t } `
    -ExpectFragment 'must be a JSON boolean'

# ------------------------------------------- acceptance item 4: the vehicle ---

Invoke-Case -Name 'case 4: agvId is agv01, the vehicle the MVP is driving' `
    -Observe 'journeyRuntime.agvId' `
    -Mutate { param($t) $t['journeyRuntime']['agvId'] = '老厂前线新多仓位1'; $t } `
    -ExpectFragment 'driving in production'

Invoke-Case -Name "case 5: vehicleKey is agv01's while agvId still says agv02" `
    -Observe 'journeyRuntime.vehicleKey' `
    -Mutate { param($t) $t['journeyRuntime']['vehicleKey'] = 'BROKERX-0c20ff0600d644869a6a80c186065d85'; $t } `
    -ExpectFragment 'driving in production'

Invoke-Case -Name "case 6: agvId is 'AGV02' -- plausible, and not any car's RIoT name" `
    -Observe 'journeyRuntime.agvId' `
    -Mutate { param($t) $t['journeyRuntime']['agvId'] = 'AGV02'; $t } `
    -ExpectFragment 'does not match the RIoT deviceName of agv02'

Invoke-Case -Name 'case 7: agvId is a list with agv01 mixed in' `
    -Observe 'journeyRuntime.agvId' `
    -Mutate { param($t) $t['journeyRuntime']['agvId'] = @('老厂前线新多仓位2', '老厂前线新多仓位1'); $t } `
    -ExpectFragment 'must be a single string'

Invoke-Case -Name 'case 8: agvId names agv03 while vehicleKey still names agv02' `
    -Observe 'journeyRuntime.agvId' `
    -Mutate { param($t) $t['journeyRuntime']['agvId'] = '老厂前线新多仓位3'; $t } `
    -ExpectFragment 'The name and the key must describe the same car'

# -------------------------------------------------- isolation table, row 1 ---

Invoke-Case -Name 'case 9: MesIngest pointed back at the production catalog' `
    -Observe 'mesIngest.baseUrl' `
    -Mutate { param($t) $t['mesIngest']['baseUrl'] = 'http://127.0.0.1:5088'; $t['fakeMesIngest']['port'] = 5088; $t } `
    -ExpectFragment 'the production MesIngest'

Invoke-Case -Name 'case 10: the service installs over the MVP' `
    -Observe 'serviceName' `
    -Mutate {
        param($t)
        $t['serviceName'] = '8005 AGV ControlServer'
        $t['installRoot'] = 'C:\Program Files\8005 AGV\ControlServer'
        $t
    } `
    -ExpectFragment 'must not install over the MVP service'

Invoke-Case -Name 'case 11: the transport takes the MVP port' `
    -Observe 'onboardPort' `
    -Mutate { param($t) $t['onboardPort'] = 58005; $t } `
    -ExpectFragment 'the MVP onboard NDJSON transport'

Invoke-Case -Name 'case 12: the binding is the wildcard that reaches the CI runner subnet' `
    -Observe 'listenAddress' `
    -Mutate { param($t) $t['listenAddress'] = '0.0.0.0'; $t } `
    -ExpectFragment 'AGV-Internal switch where the CI runner lives'

# ------------------------------ key whitelist (control-server#262 review, finding 3) ---

Invoke-Case -Name 'case 13: a Fleet roster with agv01 mixed in, spelled as the C# property' `
    -Observe 'journeyRuntime.Fleet' `
    -Mutate {
        param($t)
        $t['journeyRuntime']['Fleet'] = @(@{ agvId = '老厂前线新多仓位1'; vehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85' })
        $t
    } `
    -ExpectFragment 'journeyRuntime.Fleet is a vehicle roster'

Invoke-Case -Name "case 14: a second 'AgvId' naming agv01 beside the checked 'agvId'" `
    -Observe 'journeyRuntime.AgvId' `
    -Mutate { param($t) $t['journeyRuntime']['AgvId'] = '老厂前线新多仓位1'; $t } `
    -ExpectFragment 'differs only in case from journeyRuntime.agvId'

# ---------------------------------------------------------------- teardown ---

Write-Section 'final state'
$finalHash = Get-BlobHash
$status = (& git -C $repoRoot status --porcelain -- $relative) -join ''
Write-Host "  blob   : $finalHash (baseline $baselineHash)"
Write-Host "  git status for this file: '$status'"
Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue

# '??' means untracked, which is what a definition looks like before its first commit -- it is
# not a modification this run left behind. Anything else in the status is.
$modified = $status -and -not $status.StartsWith('??')
if ($finalHash -ne $baselineHash -or $modified) {
    Write-Host '  FAIL   the file was left modified' -ForegroundColor Red
    $failed++
} else {
    Write-Host '  PASS   the file is byte-identical to the baseline; git reports no modification' -ForegroundColor Green
    $passed++
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $passed, $failed) -ForegroundColor ($failed -eq 0 ? 'Green' : 'Red')
exit ($failed -eq 0 ? 0 : 1)
