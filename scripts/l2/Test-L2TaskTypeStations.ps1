#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2TaskTypeStations.psm1: the setup keys are refused when mistyped, and the preset written for a run
    says what the rig's JourneyRuntime scalars say.

.DESCRIPTION
    Pure input, no rig and no server, a few seconds. What the server does with the preset is covered by
    TaskTypeStationStartupTests in the unit suite and by every L2 scenario, which all run with the default preset
    installed; this covers only the half that is the orchestrator's own: that a typo in a setup file fails before
    anything starts, and that the default preset is the gate binding and nothing else.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2TaskTypeStations.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2TaskTypeStations.psm1') -Force

$wrong = 0
function Test-Case([string]$Name, [scriptblock]$Body, [string]$Throws) {
    $failure = $null
    try { & $Body } catch { $failure = $_.Exception.Message }
    $asExpected = if ($Throws) { $null -ne $failure -and $failure.Contains($Throws) } else { $null -eq $failure }
    if (-not $asExpected) { $script:wrong++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($asExpected) { 'ok  ' } else { 'BAD ' }), $Name,
        $(if ($failure) { "threw: $failure" } else { 'no error' }))
}

$gate = @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'x' }

Test-Case 'no key is the default preset' {
    if ($null -ne (Resolve-L2TaskTypeStationsSetting -Setup @{} -Where 't')) { throw 'expected $null' }
}
Test-Case '$false installs none' {
    if ((Resolve-L2TaskTypeStationsSetting -Setup @{ TaskTypeStations = $false } -Where 't') -ne $false) { throw 'expected $false' }
}
Test-Case '$true is refused' { Resolve-L2TaskTypeStationsSetting -Setup @{ TaskTypeStations = $true } -Where 't' } 'means nothing'
Test-Case 'a misspelt top-level key is refused' {
    Resolve-L2TaskTypeStationsSetting -Setup @{ TaskTypeStations = @{ RequiredTaskTypes = @(); Binding = @($gate) } } -Where 't'
} 'unknown key(s) Binding'
Test-Case 'a missing Bindings is refused' {
    Resolve-L2TaskTypeStationsSetting -Setup @{ TaskTypeStations = @{ RequiredTaskTypes = @() } } -Where 't'
} 'needs Bindings'
Test-Case 'a binding key in the wrong case is refused' {
    $bad = @{ TaskType = 'WIRE_TO_GATE'; StationRiotID = 210; StationName = '关卡'; SiteVerificationRef = 'x' }
    Resolve-L2TaskTypeStationsSetting -Setup @{ TaskTypeStations = @{ RequiredTaskTypes = @(); Bindings = @($bad) } } -Where 't'
} 'unknown key(s) StationRiotID'
Test-Case 'a station id that is not a number is refused' {
    $bad = $gate.Clone(); $bad.StationRiotId = '210'
    Resolve-L2TaskTypeStationsSetting -Setup @{ TaskTypeStations = @{ RequiredTaskTypes = @(); Bindings = @($bad) } } -Where 't'
} 'must be an integer'
Test-Case 'a deliberately bad value passes through to the server' {
    $empty = $gate.Clone(); $empty.StationName = ''
    $null = Resolve-L2TaskTypeStationsSetting -Setup @{ TaskTypeStations = @{ RequiredTaskTypes = @('WIRE_TO_GATE'); Bindings = @($empty) } } -Where 't'
}
Test-Case 'a refusal code in lower case is refused' {
    Resolve-L2ExpectedStartupRefusal -Setup @{ ExpectServerStartupRefusal = 'task_type_station_reused' } -Where 't'
} 'must be a reason code'
Test-Case 'a refusal on the real rig is refused' {
    Resolve-L2ExpectedStartupRefusal -Setup @{ ExpectServerStartupRefusal = 'TASK_TYPE_STATION_REUSED' } -Where 't' -RealOnboard $true
} 'needs the synthetic rig'
Test-Case 'a refusal beside a dashboard is refused' {
    Resolve-L2ExpectedStartupRefusal -Setup @{ ExpectServerStartupRefusal = 'TASK_TYPE_STATION_REUSED'; Dashboard = $true } -Where 't'
} 'cannot be combined with Dashboard'

$directory = (New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "l2-tts-$([guid]::NewGuid().ToString('N'))")).FullName
try {
    Test-Case 'the default preset is six rules, the rig map, and WIRE_TO_GATE bound to the gate only' {
        $path = New-L2TaskTypeStationPreset -Setting $null -MapId 25 -GateStationId '关卡' -GateStationRiotId 210 `
            -Path (Join-Path $directory 'default.json')
        $preset = (Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json).TaskTypeStations
        if (@($preset.rules).Count -ne 6) { throw "rules: $(@($preset.rules).Count)" }
        if ((@($preset.rules | Where-Object fixedEnd -EQ 'ORIGIN').taskType) -ne 'STAGING_TO_WIRE') { throw 'ORIGIN is not STAGING_TO_WIRE alone' }
        if ($preset.mapId -ne 25) { throw "mapId: $($preset.mapId)" }
        if ((@($preset.requiredTaskTypes) -join ',') -ne 'WIRE_TO_GATE') { throw "required: $($preset.requiredTaskTypes)" }
        $binding = @($preset.bindings)
        if ($binding.Count -ne 1 -or $binding[0].taskType -ne 'WIRE_TO_GATE' -or $binding[0].stationRiotId -ne 210 -or
            $binding[0].stationName -ne '关卡' -or [string]::IsNullOrWhiteSpace($binding[0].siteVerificationRef)) {
            throw "bindings: $($binding | ConvertTo-Json -Compress)"
        }
    }
    Test-Case '$false writes a file without the section' {
        $path = New-L2TaskTypeStationPreset -Setting $false -MapId 25 -GateStationId '关卡' -GateStationRiotId 210 `
            -Path (Join-Path $directory 'none.json')
        if ($null -ne (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).PSObject.Properties['TaskTypeStations']) { throw 'section present' }
    }
    Test-Case "a scenario's own preset keeps the default rules and replaces the rest" {
        $setting = @{ RequiredTaskTypes = @(); Bindings = @($gate, @{ TaskType = 'STAGING_TO_WIRE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'y' }) }
        $path = New-L2TaskTypeStationPreset -Setting $setting -MapId 25 -GateStationId '关卡' -GateStationRiotId 210 `
            -Path (Join-Path $directory 'own.json')
        $preset = (Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json).TaskTypeStations
        if (@($preset.rules).Count -ne 6 -or @($preset.bindings).Count -ne 2 -or @($preset.requiredTaskTypes).Count -ne 0) {
            throw ($preset | ConvertTo-Json -Compress -Depth 4)
        }
    }
    Test-Case 'the protocol identity is read from the ProtocolCandidate block' {
        Set-Content -LiteralPath (Join-Path $directory 'appsettings.json') -Encoding utf8NoBOM -Value (@{
                ProtocolCandidate = @{ releaseVersion = '2.0.0'; tag = 'protocol-v2.0.0'; repositoryCommit = 'abc'; protocolVersion = 3
                    profileId = 'AGV_FULL_PRODUCT'; manifestSha256 = 'm'; schemaBundleSha256 = 's'; vectorsSha256 = 'v'; approvalStatus = 'APPROVED_RELEASE' }
            } | ConvertTo-Json)
        $identity = Read-L2ProtocolCandidateIdentity -HostDirectory $directory
        if ($identity.tag -ne 'protocol-v2.0.0' -or $identity.commit -ne 'abc' -or $identity.protocolVersion -ne 3) {
            throw ($identity | ConvertTo-Json -Compress)
        }
    }
} finally {
    Remove-Item -LiteralPath $directory -Recurse -Force
}

if ($wrong -gt 0) {
    Write-Host "$wrong case(s) came out the other way."
    exit 1
}
Write-Host 'Every case came out as expected.'
