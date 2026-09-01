#requires -Version 7
<#
.SYNOPSIS
Red side for ticket 09: every detector the acceptance run leans on is shown to go red.

.DESCRIPTION
This imports Ticket09Detectors.psm1 -- the same module Invoke-Ticket09Acceptance.ps1 and
Invoke-Ticket09UpgradeRehearsal.ps1 import -- and feeds each detector a single-field mutation of an
input it reported green on. Both directions are recorded for every detector, because a control that
was never green somewhere proves nothing, and a green that was never red proves nothing either.

The mutations are deliberately one field each. A mutation that changes several things at once cannot
tell you which field the detector is actually sensitive to.

Runs unelevated: it reads certificate stores and the production service, and writes only inside its
own run root.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260901b-19ce7db'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Ticket09Detectors.psm1') -Force

$results = [System.Collections.Generic.List[object]]::new()
function Add-Control {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Detector,
        [Parameter(Mandatory)][string]$Mutation,
        [Parameter(Mandatory)][string]$GreenExpectation,
        [Parameter(Mandatory)]$GreenObserved,
        [Parameter(Mandatory)][string]$RedExpectation,
        [Parameter(Mandatory)]$RedObserved,
        [Parameter(Mandatory)][bool]$GreenHeld,
        [Parameter(Mandatory)][bool]$RedFired
    )
    $verdict = if ($GreenHeld -and $RedFired) { 'PASS' } else { 'FAIL' }
    $results.Add([ordered]@{
            id = $Id; detector = $Detector; mutation = $Mutation
            greenExpectation = $GreenExpectation; greenObserved = "$GreenObserved"; greenHeld = $GreenHeld
            redExpectation = $RedExpectation; redObserved = "$RedObserved"; redFired = $RedFired
            verdict = $verdict
        })
    '[{0,-6}] {1,-28} green={2} red={3}' -f $verdict, $Id, $GreenHeld, $RedFired | Write-Host
}

if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

# ------------------------------------------------------------------ 1. certificate store digest
# Green: the same store read twice with nothing done to it. Red: one thumbprint in the set replaced,
# which keeps the COUNT identical -- the exact case a count-based detector cannot see.
$realStore = Get-StoreDigest 'Root' 'CurrentUser'
$thumbprints = @((Get-ChildItem Cert:\CurrentUser\Root).Thumbprint)
$unmutated = New-StoreDigest 'CurrentUser\Root' $thumbprints
$mutatedSet = @($thumbprints)
$mutatedSet[0] = ('0' * $thumbprints[0].Length)
$mutated = New-StoreDigest 'CurrentUser\Root' $mutatedSet
$greenChanges = @(Compare-StoreDigests @($realStore) @($unmutated))
$redChanges = @(Compare-StoreDigests @($realStore) @($mutated))
Add-Control -Id 'RED-STORE-DIGEST' -Detector 'Compare-StoreDigests' `
    -Mutation 'exactly one of the 44 thumbprints replaced; the count is left identical' `
    -GreenExpectation '0 changes when the same thumbprint set is rebuilt untouched' -GreenObserved "changes=$($greenChanges.Count)" `
    -RedExpectation '1 change when one thumbprint differs' -RedObserved "changes=$($redChanges.Count) $($redChanges -join '')" `
    -GreenHeld ($greenChanges.Count -eq 0) -RedFired ($redChanges.Count -eq 1) `
    | Out-Null
"  (count green=$($unmutated.count) red=$($mutated.count) -- equal, so a count-based detector would have missed this)" | Write-Host

# --------------------------------------------------------------- 2. removed configuration keys
$configurationDir = Join-Path $RunRoot 'config'
New-Item -ItemType Directory -Path $configurationDir -Force | Out-Null
$plaintextConfiguration = Join-Path $configurationDir 'plaintext.json'
@'
{
  "Health": { "url": "http://127.0.0.1:58507" },
  "OnboardTransport": { "enabled": true, "listenAddress": "127.0.0.1", "port": 58505 },
  "OnboardSafetyProjection": { "enabled": true }
}
'@ | Set-Content -LiteralPath $plaintextConfiguration -Encoding utf8NoBOM
$greenKeys = @(Get-RemovedCertificateKeys -ConfigurationPath $plaintextConfiguration)

$mutatedConfiguration = Join-Path $configurationDir 'plaintext-plus-one-key.json'
$configuration = Get-Content -Raw -LiteralPath $plaintextConfiguration | ConvertFrom-Json
# ConvertFrom-Json produces a PSCustomObject: assigning to a property that does not exist throws
# rather than adding it silently, so a new key has to go in through Add-Member.
$configuration.OnboardSafetyProjection | Add-Member -NotePropertyName 'requireHttps' -NotePropertyValue $true
$configuration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $mutatedConfiguration -Encoding utf8NoBOM
$redKeys = @(Get-RemovedCertificateKeys -ConfigurationPath $mutatedConfiguration)
Add-Control -Id 'RED-REMOVED-CONFIG-KEYS' -Detector 'Get-RemovedCertificateKeys' `
    -Mutation 'exactly one key added: OnboardSafetyProjection:requireHttps' `
    -GreenExpectation '0 removed keys found in a plaintext configuration' -GreenObserved "found=$($greenKeys.Count)" `
    -RedExpectation 'exactly requireHttps found' -RedObserved "found=$($redKeys.Count) [$($redKeys -join ',')]" `
    -GreenHeld ($greenKeys.Count -eq 0) -RedFired ($redKeys.Count -eq 1 -and $redKeys[0] -eq 'requireHttps') | Out-Null

# --------------------------------------------------------------------- 3. onboard TLS key names
$onboardGreenPath = Join-Path $configurationDir 'onboard-plaintext.json'
@'
{
  "wireToGate": { "host": "192.168.200.1", "port": 58505 },
  "vehicleSafety": { "endpoint": "http://192.168.200.1:58507/api/onboard/v1/vehicle-safety" }
}
'@ | Set-Content -LiteralPath $onboardGreenPath -Encoding utf8NoBOM
$onboardGreen = @(Get-OnboardTlsKeys -ConfigurationPath $onboardGreenPath)
$onboardRedPath = Join-Path $configurationDir 'onboard-plus-usetls.json'
$onboardConfiguration = Get-Content -Raw -LiteralPath $onboardGreenPath | ConvertFrom-Json
$onboardConfiguration.wireToGate | Add-Member -NotePropertyName 'useTls' -NotePropertyValue $true
$onboardConfiguration | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $onboardRedPath -Encoding utf8NoBOM
$onboardRed = @(Get-OnboardTlsKeys -ConfigurationPath $onboardRedPath)
Add-Control -Id 'RED-ONBOARD-TLS-KEYS' -Detector 'Get-OnboardTlsKeys' `
    -Mutation 'exactly one key added: wireToGate:useTls' `
    -GreenExpectation '0 TLS key names in the plaintext onboard configuration' -GreenObserved "found=$($onboardGreen.Count)" `
    -RedExpectation 'exactly useTls found' -RedObserved "found=$($onboardRed.Count) [$($onboardRed -join ',')]" `
    -GreenHeld ($onboardGreen.Count -eq 0) -RedFired ($onboardRed.Count -eq 1 -and $onboardRed[0] -eq 'useTls') | Out-Null

# ------------------------------------------------------------------------- 4. key material scan
$cleanDir = Join-Path $RunRoot 'key-material-clean'
New-Item -ItemType Directory -Path $cleanDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $cleanDir 'appsettings.json') -Value '{}' -Encoding utf8NoBOM
$keyGreen = @(Get-KeyMaterialFiles -Path @($cleanDir))
$dirtyDir = Join-Path $RunRoot 'key-material-planted'
Copy-Item -LiteralPath $cleanDir -Destination $dirtyDir -Recurse
Set-Content -LiteralPath (Join-Path $dirtyDir 'localhost.pfx') -Value 'not a real certificate' -Encoding ascii
$keyRed = @(Get-KeyMaterialFiles -Path @($dirtyDir))
Add-Control -Id 'RED-KEY-MATERIAL' -Detector 'Get-KeyMaterialFiles' `
    -Mutation 'exactly one file added: localhost.pfx' `
    -GreenExpectation '0 key-material files in the clean copy' -GreenObserved "found=$($keyGreen.Count)" `
    -RedExpectation 'exactly the planted .pfx found' -RedObserved "found=$($keyRed.Count)" `
    -GreenHeld ($keyGreen.Count -eq 0) `
    -RedFired ($keyRed.Count -eq 1 -and $keyRed[0].EndsWith('localhost.pfx')) | Out-Null
"  (single-element guard: `$keyRed[0] is '$($keyRed[0])', not one character -- @() wraps the projection, not the source)" | Write-Host

# ------------------------------------------------------------------------------ 5. tree hashes
$treeSource = Join-Path $RunRoot 'tree-source'
New-Item -ItemType Directory -Path $treeSource -Force | Out-Null
1..3 | ForEach-Object { Set-Content -LiteralPath (Join-Path $treeSource "file$_.txt") -Value "content $_" -Encoding utf8NoBOM }
$treeCopy = Join-Path $RunRoot 'tree-copy'
Copy-Item -LiteralPath $treeSource -Destination $treeCopy -Recurse
$before = Get-TreeHashes $treeSource
$greenDifferences = @(Compare-TreeHashes $before (Get-TreeHashes $treeCopy))
Add-Content -LiteralPath (Join-Path $treeCopy 'file2.txt') -Value 'x' -NoNewline
$redDifferences = @(Compare-TreeHashes $before (Get-TreeHashes $treeCopy))
Add-Control -Id 'RED-TREE-HASHES' -Detector 'Compare-TreeHashes' `
    -Mutation 'exactly one byte appended to file2.txt in the copy' `
    -GreenExpectation '0 differences between a directory and its untouched copy' -GreenObserved "differences=$($greenDifferences.Count)" `
    -RedExpectation 'exactly changed:file2.txt' -RedObserved "differences=$($redDifferences.Count) [$($redDifferences -join ',')]" `
    -GreenHeld ($greenDifferences.Count -eq 0) `
    -RedFired ($redDifferences.Count -eq 1 -and $redDifferences[0] -eq 'changed:file2.txt') | Out-Null

# ------------------------------------------------------------------- 6. release candidate hashes
$hashGreen = Get-HashMismatches -Root $ReleaseRoot -SumsPath (Join-Path $ReleaseRoot 'SHA256SUMS.txt')
$flippedRoot = Join-Path $RunRoot 'flipped-candidate'
New-Item -ItemType Directory -Path $flippedRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'SHA256SUMS.txt') -Destination $flippedRoot -Force
Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'RELEASE-CANDIDATE.md') -Destination $flippedRoot -Force
$flippedFile = Join-Path $flippedRoot 'RELEASE-CANDIDATE.md'
$bytes = [IO.File]::ReadAllBytes($flippedFile)
$bytes[0] = $bytes[0] -bxor 0x01
[IO.File]::WriteAllBytes($flippedFile, $bytes)
# Only the two copied files can be checked here; the rest are legitimately absent, so the red
# criterion is that RELEASE-CANDIDATE.md is among the reported mismatches.
$hashRed = Get-HashMismatches -Root $flippedRoot -SumsPath (Join-Path $flippedRoot 'SHA256SUMS.txt')
Add-Control -Id 'RED-RELEASE-HASHES' -Detector 'Get-HashMismatches' `
    -Mutation 'exactly one byte flipped in a copy of RELEASE-CANDIDATE.md' `
    -GreenExpectation "0 mismatches across all $($hashGreen.checked) listed files of the real candidate" `
    -GreenObserved "checked=$($hashGreen.checked) mismatches=$($hashGreen.mismatches.Count)" `
    -RedExpectation 'RELEASE-CANDIDATE.md reported as a mismatch' `
    -RedObserved "reported=$(@($hashRed.mismatches | Where-Object { $_ -eq 'RELEASE-CANDIDATE.md' }).Count)" `
    -GreenHeld ($hashGreen.checked -gt 0 -and $hashGreen.mismatches.Count -eq 0) `
    -RedFired (@($hashRed.mismatches | Where-Object { $_ -eq 'RELEASE-CANDIDATE.md' }).Count -eq 1) | Out-Null

# --------------------------------------------------------------------- 7. production untouched
$productionGreen = Get-ProductionSnapshot
$productionSame = @(Compare-ProductionSnapshot $productionGreen (Get-ProductionSnapshot))
$mutatedSnapshot = [ordered]@{
    status = $productionGreen.status
    processIds = @(@($productionGreen.processIds)[0] + 1)
    listeners = @($productionGreen.listeners)
}
$productionDrift = @(Compare-ProductionSnapshot $productionGreen $mutatedSnapshot)
Add-Control -Id 'RED-PRODUCTION-DRIFT' -Detector 'Compare-ProductionSnapshot' `
    -Mutation 'exactly one field changed: the service PID incremented by one' `
    -GreenExpectation '0 drift between two consecutive readings of the untouched production service' `
    -GreenObserved "drift=$($productionSame.Count) pid=$($productionGreen.processIds -join ',')" `
    -RedExpectation 'exactly the pid field reported' -RedObserved "drift=$($productionDrift.Count) [$($productionDrift -join ',')]" `
    -GreenHeld ($productionSame.Count -eq 0) `
    -RedFired ($productionDrift.Count -eq 1 -and $productionDrift[0] -like 'pid:*') | Out-Null

$summary = [ordered]@{
    schemaVersion = 1
    runKind = 'TICKET09_RED_SIDE_SINGLE_FIELD_MUTATIONS'
    detectorModule = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Ticket09Detectors.psm1') -Algorithm SHA256).Hash.ToLowerInvariant()
    releaseRoot = $ReleaseRoot
    finishedAt = [DateTimeOffset]::UtcNow.ToString('O')
    counts = [ordered]@{
        pass = @($results | Where-Object verdict -eq 'PASS').Count
        fail = @($results | Where-Object verdict -eq 'FAIL').Count
    }
    controls = $results
}
[IO.File]::WriteAllText((Join-Path $RunRoot 'red-side-controls.json'),
    ($summary | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
'PASS={0} FAIL={1}' -f $summary.counts.pass, $summary.counts.fail
