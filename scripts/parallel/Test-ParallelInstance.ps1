#Requires -Version 7

<#
.SYNOPSIS
    Self-test for ParallelInstance.psm1: the shipped definition passes, and each way of
    corrupting it is refused for exactly the reason it should be.

.DESCRIPTION
    control-server#262 acceptance items 3 and 4 ask for a reverse check -- take the RouteGraph
    configuration away and the deployment must refuse; name a vehicle other than agv02/agv03
    and it must refuse. This script is that check, and it asserts something stronger than "it
    went red": every negative case states which failure it expects, and the assertion is that
    the expected failure is the *only* one. A check that fires for the wrong reason would pass
    a weaker test and leave the real guard untested.

    Two guards against a self-test that proves nothing:

      * every mutation is compared against the baseline before and after, so an injection that
        did not land fails the case instead of looking like a weak assertion;
      * the baseline itself is asserted to produce zero failures, so a check that is red for
        everything cannot pass this suite.

    No Pester: this repository has no Pester dependency and the three machines carry Windows'
    own incompatible 3.4.0 on PSModulePath.

.EXAMPLE
    pwsh -File scripts/parallel/Test-ParallelInstance.ps1
#>
[CmdletBinding()]
param(
    [string] $DefinitionPath = (Join-Path $PSScriptRoot 'instance-factory01-v2.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force
# For Invoke-ParallelProductUninstaller, exercised below against fake product scripts. Nothing
# in that section touches a real service: the name it passes exists on no machine.
Import-Module (Join-Path $PSScriptRoot 'ParallelHost.psm1') -Force

$script:Failed = 0
$script:Passed = 0

function Write-Result {
    param([bool] $Ok, [string] $Name, [string] $Detail)
    if ($Ok) {
        $script:Passed++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } else {
        $script:Failed++
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        Write-Host "        $Detail" -ForegroundColor Red
    }
}

function Copy-Definition {
    param($Definition)
    return ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Definition -Depth 12) -AsHashtable -Depth 12
}

function Get-Fingerprint {
    param($Definition)
    return ConvertTo-Json -InputObject $Definition -Depth 12 -Compress
}

function Set-Map26TestValue {
    <#
        The shipped definition carries REPLACE_* placeholders for the two map-26 values that have
        no source yet, and is therefore -- deliberately -- not deployable. Every case below works
        from the shipped file with those filled by obviously-test values, so the cases test the
        checks and not the placeholders. The real values come from the site (README.md).
    #>
    param($Definition)
    $journey = $Definition['journeyRuntime']
    $journey['dispatchZone'] = 'MAP-26-WIRE_TO_GATE'
    $journey['allowedDispatchZones'] = @('MAP-26-WIRE_TO_GATE')
    $journey['admissionPolicyDeploymentId'] = 'MAP-26-WIRE_TO_GATE-SELFTEST'
    return $Definition
}

$shipped = Read-ParallelInstanceDefinition -Path $DefinitionPath
$baseline = Set-Map26TestValue (Copy-Definition $shipped)
$baselineFingerprint = Get-Fingerprint $baseline

Write-Host "Instance definition: $DefinitionPath"
Write-Host ''
Write-Host 'The shipped file (must NOT be deployable yet)' -ForegroundColor Cyan

# control-server#262 re-review, M3: "do not install until the map-26 values are filled in" is now
# a check, not a sentence in a document. The shipped file must be refused for exactly its three
# placeholders -- no more (that would mean something else is wrong with it) and no fewer.
$shippedFailures = @(Test-ParallelInstanceDefinition -Definition $shipped)
$placeholderFailures = @($shippedFailures | Where-Object { $_ -like '*is still the placeholder*' })
Write-Result -Ok ($shippedFailures.Count -eq 3 -and $placeholderFailures.Count -eq 3) `
    -Name 'the shipped definition is refused for exactly its three map-26 placeholders' `
    -Detail ("got $($shippedFailures.Count): " + ($shippedFailures -join ' | '))

Write-Host ''
Write-Host 'Positive case (shipped file with the placeholders filled by test values)' -ForegroundColor Cyan

# If this one ever fails, nothing below it means anything: a checker that refuses everything
# would satisfy every negative case in this file.
$baselineFailures = @(Test-ParallelInstanceDefinition -Definition $baseline)
Write-Result -Ok ($baselineFailures.Count -eq 0) -Name 'the shipped definition is accepted' `
    -Detail ("expected no failures, got: " + ($baselineFailures -join ' | '))

# Assert- separately from Test-, because the two have different failure modes on the accepting
# path and the callers only ever run Assert-. This case caught a real one: Test- returning an
# empty array returns nothing, [string[]] $null is $null, and .Count on it threw under
# StrictMode -- so a perfectly valid definition was "refused" with a PowerShell error, and only
# the file-level reverse check (Invoke-ReverseCheck.ps1 case 0) noticed.
$assertOk = $true
$assertDetail = ''
try {
    $returned = Assert-ParallelInstanceDefinition -Definition $baseline
    if ($null -eq $returned) { $assertOk = $false; $assertDetail = 'returned $null instead of the definition' }
} catch {
    $assertOk = $false
    $assertDetail = "threw: $($_.Exception.Message)"
}
Write-Result -Ok $assertOk -Name 'Assert- accepts the shipped definition and returns it' -Detail $assertDetail

# ... and still throws when it should, so the case above cannot be satisfied by an Assert- that
# never throws at all.
$assertThrows = $false
$rejected = Copy-Definition $baseline
$rejected['journeyRuntime']['vehicleKey'] = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
try {
    $null = Assert-ParallelInstanceDefinition -Definition $rejected
} catch {
    $assertThrows = $_.Exception.Message -like '*driving in production*'
}
Write-Result -Ok $assertThrows -Name 'Assert- throws on the agv01 definition' `
    -Detail 'expected a throw naming the production vehicle'

Write-Host ''
Write-Host 'Negative cases (each must fire exactly one named failure)' -ForegroundColor Cyan

<#
    Each case mutates a fresh copy of the baseline and names the failure it expects. Mutate
    returns the mutated definition rather than editing in place through a closure: a closure
    that writes to a captured variable is a reliable way to write a test that asserts nothing.
#>
$cases = @(
    @{
        Name = 'serviceName is the production service'
        Expect = "serviceName is the production service"
        Mutate = { param($d) $d['serviceName'] = '8005 AGV ControlServer'; $d }
    }
    # --- Paths: the allowlist is the first layer (control-server#262 re-review, S1) ---
    # Every production path below is refused by the allowlist -- its name lacks the V2 marker, or
    # it is not a direct child of a known root -- before the production denylist is consulted.
    # That is the intended order; the denylist is exercised on its own further down.
    @{
        Name = 'installRoot is the production install root'
        Expect = "installRoot ('C:\Program Files\8005 AGV\ControlServer') is named 'ControlServer', which does not carry the instance marker"
        Mutate = { param($d) $d['installRoot'] = 'C:\Program Files\8005 AGV\ControlServer'; $d }
    }
    @{
        Name = 'dataRoot is a V2-named directory nested inside the production data root'
        Expect = "dataRoot ('C:\ProgramData\8005\ControlServer\v2') is not directly under"
        Mutate = { param($d) $d['dataRoot'] = 'C:\ProgramData\8005\ControlServer\v2'; $d }
    }
    @{
        Name = 'installRoot written with forward slashes'
        Expect = "installRoot ('C:/Program Files/8005 AGV/ControlServer.V2') uses '/'"
        Mutate = { param($d) $d['installRoot'] = 'C:/Program Files/8005 AGV/ControlServer.V2'; $d }
    }
    @{
        # The allowed parent compared with -ieq, which skips zero-width characters.
        Name = 'installRoot under a lookalike of an allowed root (zero-width space)'
        Expect = 'is not directly under one of'
        Mutate = { param($d) $d['installRoot'] = "C:\Program Files\8005 AGV$([char]0x200B)\ControlServer.V2"; $d }
    }
    @{
        Name = "dataRoot through a '.' segment"
        Expect = "dataRoot ('C:\ProgramData\8005\.\ControlServer.V2') has a '.' segment"
        Mutate = { param($d) $d['dataRoot'] = 'C:\ProgramData\8005\.\ControlServer.V2'; $d }
    }
    @{
        Name = "dataRoot through a '..' segment"
        Expect = "dataRoot ('C:\ProgramData\8005\x\..\ControlServer.V2') has a '..' segment"
        Mutate = { param($d) $d['dataRoot'] = 'C:\ProgramData\8005\x\..\ControlServer.V2'; $d }
    }
    @{
        Name = 'installRoot through an 8.3 short name'
        Expect = "installRoot ('C:\PROGRA~1\8005 AGV\ControlServer.V2') contains '~'"
        Mutate = { param($d) $d['installRoot'] = 'C:\PROGRA~1\8005 AGV\ControlServer.V2'; $d }
    }
    @{
        Name = 'packageRoot as a \\?\ device path'
        Expect = "packageRoot ('\\?\D:\zhengyushao\ControlServer.V2') is a UNC or device path"
        Mutate = { param($d) $d['packageRoot'] = '\\?\D:\zhengyushao\ControlServer.V2'; $d }
    }
    @{
        Name = 'packageRoot as an administrative share'
        Expect = "packageRoot ('\\localhost\D$\zhengyushao\ControlServer.V2') is a UNC or device path"
        Mutate = { param($d) $d['packageRoot'] = '\\localhost\D$\zhengyushao\ControlServer.V2'; $d }
    }
    @{
        # Windows strips a trailing dot, so this names the directory without it.
        Name = 'opsRoot with a trailing dot'
        Expect = "opsRoot ('D:\zhengyushao\control-server-v2-ops.') has a segment"
        Mutate = { param($d) $d['opsRoot'] = 'D:\zhengyushao\control-server-v2-ops.'; $d }
    }
    @{
        Name = "packageRoot is the MVP's previous package generation"
        Expect = "packageRoot ('D:\zhengyushao\ControlServer.previous') is named 'ControlServer.previous', which does not carry the instance marker"
        Mutate = { param($d) $d['packageRoot'] = 'D:\zhengyushao\ControlServer.previous'; $d }
    }
    @{
        Name = 'backupRoot is the production MesIngest directory'
        Expect = "backupRoot ('D:\zhengyushao\MesIngest') is named 'MesIngest', which does not carry the instance marker"
        Mutate = { param($d) $d['backupRoot'] = 'D:\zhengyushao\MesIngest'; $d }
    }
    @{
        Name = "fakeMesIngest.installRoot is the MVP's ops directory"
        Expect = "fakeMesIngest.installRoot ('D:\zhengyushao\control-server-ops') is named 'control-server-ops', which does not carry the instance marker"
        Mutate = { param($d) $d['fakeMesIngest']['installRoot'] = 'D:\zhengyushao\control-server-ops'; $d }
    }
    @{
        # The exact definition the re-review used to reproduce S1. Four failures, one per path,
        # each from the first layer; before the fix this definition passed and the uninstaller's
        # plan listed the MVP install root, the production database directory and more.
        Name = 'the re-review S1 reproduction definition'
        Expect = @(
            "installRoot ('C:/Program Files/8005 AGV/ControlServer') uses '/'"
            "dataRoot ('C:/ProgramData/8005/ControlServer') uses '/'"
            "packageRoot ('D:\zhengyushao\ControlServer.previous') is named 'ControlServer.previous'"
            "backupRoot ('D:\zhengyushao\MesIngest') is named 'MesIngest'"
        )
        Mutate = {
            param($d)
            $d['installRoot'] = 'C:/Program Files/8005 AGV/ControlServer'
            $d['dataRoot'] = 'C:/ProgramData/8005/ControlServer'
            $d['packageRoot'] = 'D:\zhengyushao\ControlServer.previous'
            $d['backupRoot'] = 'D:\zhengyushao\MesIngest'
            $d
        }
    }
    @{
        Name = 'dataRoot is the same directory as installRoot'
        Expect = 'installRoot (''C:\Program Files\8005 AGV\ControlServer.V2'') and dataRoot (''C:\Program Files\8005 AGV\ControlServer.V2'') are the same directory or nested'
        Mutate = { param($d) $d['dataRoot'] = $d['installRoot']; $d }
    }
    @{
        # Deleting a package generation must never delete the data root.
        Name = "dataRoot is packageRoot's previous generation"
        Expect = "dataRoot ('D:\zhengyushao\ControlServer.V2.previous') and packageRoot.previous (derived)"
        Mutate = { param($d) $d['dataRoot'] = 'D:\zhengyushao\ControlServer.V2.previous'; $d }
    }
    @{
        # Three failures, all named: the new opsRoot matches the install's own staging glob, and the
        # seed file and the roster (control-server#454), still under the old opsRoot, are now outside it.
        Name = "opsRoot matches packageRoot's staging glob"
        Expect = @(
            "opsRoot ('D:\zhengyushao\ControlServer.V2.incoming-ops') matches packageRoot's staging glob"
            'fakeMesIngest.seedPath'
            'fieldOperatorRoles.path'
        )
        Mutate = { param($d) $d['opsRoot'] = 'D:\zhengyushao\ControlServer.V2.incoming-ops'; $d }
    }
    @{
        Name = 'seedPath outside opsRoot'
        Expect = "fakeMesIngest.seedPath ('D:\zhengyushao\seed.json') must be inside opsRoot"
        Mutate = { param($d) $d['fakeMesIngest']['seedPath'] = 'D:\zhengyushao\seed.json'; $d }
    }
    @{
        Name = 'seedPath written with forward slashes'
        Expect = "fakeMesIngest.seedPath ('D:/zhengyushao/control-server-v2-ops/seed.json') uses '/'"
        Mutate = { param($d) $d['fakeMesIngest']['seedPath'] = 'D:/zhengyushao/control-server-v2-ops/seed.json'; $d }
    }

    # --- Names (control-server#262 re-review, M1) ---------------------------------------
    @{
        # The literal "-eq production" check let this through, and Stop-Service -Name expands it.
        Name = 'serviceName with a trailing wildcard'
        Expect = "serviceName '8005 AGV ControlServer*' contains a wildcard character"
        Mutate = { param($d) $d['serviceName'] = '8005 AGV ControlServer*'; $d }
    }
    @{
        Name = 'serviceName with a single-character wildcard'
        Expect = "serviceName '8005 AGV ControlServe?' contains a wildcard character"
        Mutate = { param($d) $d['serviceName'] = '8005 AGV ControlServe?'; $d }
    }
    @{
        Name = 'serviceName is another production service'
        Expect = "serviceName 'MesIngest' does not carry the instance marker"
        Mutate = { param($d) $d['serviceName'] = 'MesIngest'; $d }
    }
    @{
        Name = "taskName is '*' (every task on the host)"
        Expect = "fakeMesIngest.taskName '*' contains a wildcard character"
        Mutate = { param($d) $d['fakeMesIngest']['taskName'] = '*'; $d }
    }

    # --- The MVP's map (control-server#262 re-review, M3) --------------------------------
    @{
        Name = 'both mapIds are the MVP map 25'
        Expect = @('routeGraph.mapId is 25, the MVP''s map', 'journeyRuntime.mapId is 25, the MVP''s map')
        Mutate = { param($d) $d['routeGraph']['mapId'] = 25; $d['journeyRuntime']['mapId'] = 25; $d }
    }
    @{
        # Two, both named: the value is the MVP's map, and it now disagrees with routeGraph's 26.
        Name = 'only journeyRuntime.mapId is 25'
        Expect = @('journeyRuntime.mapId is 25, the MVP''s map', 'disagree')
        Mutate = { param($d) $d['journeyRuntime']['mapId'] = 25; $d }
    }
    @{
        Name = "mapIdentity is the MVP map's name"
        Expect = "journeyRuntime.mapIdentity is '老厂前线new', the MVP's map name"
        Mutate = { param($d) $d['journeyRuntime']['mapIdentity'] = '老厂前线new'; $d }
    }
    @{
        Name = 'dispatchZone is the MVP map-25 zone'
        Expect = "journeyRuntime.dispatchZone is 'MAP-25-WIRE_TO_GATE', an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = 'MAP-25-WIRE_TO_GATE'; $d }
    }
    @{
        Name = 'admissionPolicyDeploymentId is the MVP one'
        Expect = "journeyRuntime.admissionPolicyDeploymentId is 'MAP-25-WIRE_TO_GATE-20260827'"
        Mutate = { param($d) $d['journeyRuntime']['admissionPolicyDeploymentId'] = 'MAP-25-WIRE_TO_GATE-20260827'; $d }
    }
    @{
        Name = 'a map-25 zone hidden second in allowedDispatchZones'
        Expect = "journeyRuntime.allowedDispatchZones[1] is 'MAP-25-WIRE_TO_GATE'"
        Mutate = { param($d) $d['journeyRuntime']['allowedDispatchZones'] = @('MAP-26-WIRE_TO_GATE', 'MAP-25-WIRE_TO_GATE'); $d }
    }
    # S1 re-review, question 3: the comparisons were exact and case-sensitive, and each of these
    # was accepted. They are the ways a person actually types the MVP's map.
    @{
        Name = "mapIdentity is the MVP map's name with a trailing space"
        Expect = "journeyRuntime.mapIdentity is '老厂前线new ', the MVP's map name"
        Mutate = { param($d) $d['journeyRuntime']['mapIdentity'] = '老厂前线new '; $d }
    }
    @{
        Name = "mapIdentity is the MVP map's name in capitals"
        Expect = "journeyRuntime.mapIdentity is '老厂前线NEW', the MVP's map name"
        Mutate = { param($d) $d['journeyRuntime']['mapIdentity'] = '老厂前线NEW'; $d }
    }
    @{
        Name = "mapIdentity is the MVP map's name in full-width letters"
        Expect = "journeyRuntime.mapIdentity is '老厂前线ｎｅｗ', the MVP's map name"
        Mutate = { param($d) $d['journeyRuntime']['mapIdentity'] = '老厂前线ｎｅｗ'; $d }
    }
    @{
        # A zero-width space INSIDE the token. The regex compares ordinally, so without the
        # format-character removal this is not a map-25 token. (The same character in mapIdentity
        # would prove nothing: PowerShell's -ceq is a culture comparison that already ignores
        # zero-width characters -- measured, evidence review3-string-equality.txt.)
        Name = 'dispatchZone is a map-25 zone with a zero-width space inside the number'
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = "MAP-2$([char]0x200B)5-WIRE_TO_GATE"; $d }
    }
    @{
        Name = 'dispatchZone is a map-25 zone written with an underscore'
        Expect = "journeyRuntime.dispatchZone is 'MAP_25-WIRE_TO_GATE', an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = 'MAP_25-WIRE_TO_GATE'; $d }
    }
    @{
        Name = 'dispatchZone is a map-25 zone in lower case with a space'
        Expect = "journeyRuntime.dispatchZone is 'map 25-wire_to_gate', an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = 'map 25-wire_to_gate'; $d }
    }
    # S1 re-review round 3, item 6: separators repeated or mixed, a '.' after the number, and the
    # Unicode hyphens and dashes NFKC leaves alone. Built with [char] so the file stays ASCII.
    @{
        Name = 'dispatchZone map_-_25 (separators mixed and repeated)'
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = 'map_-_25-WIRE_TO_GATE'; $d }
    }
    @{
        Name = "dispatchZone MAP-25.A (a '.' after the number)"
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = 'MAP-25.A'; $d }
    }
    @{
        # The separator left out -- the most natural typo of all (round 4).
        Name = 'dispatchZone MAP25 (no separator)'
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = 'MAP25-WIRE_TO_GATE'; $d }
    }
    @{
        Name = 'dispatchZone with U+2010 hyphen'
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = "MAP$([char]0x2010)25-WIRE_TO_GATE"; $d }
    }
    @{
        Name = 'dispatchZone with U+2011 non-breaking hyphen'
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = "MAP$([char]0x2011)25-WIRE_TO_GATE"; $d }
    }
    @{
        Name = 'dispatchZone with U+2013 en dash'
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = "MAP$([char]0x2013)25-WIRE_TO_GATE"; $d }
    }
    @{
        Name = 'dispatchZone with U+2014 em dash'
        Expect = "an identifier of the MVP's map 25"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = "MAP$([char]0x2014)25-WIRE_TO_GATE"; $d }
    }
    @{
        Name = 'a placeholder left in dispatchZone'
        Expect = "journeyRuntime.dispatchZone is still the placeholder 'REPLACE_WITH_MAP26_DISPATCH_ZONE'"
        Mutate = { param($d) $d['journeyRuntime']['dispatchZone'] = 'REPLACE_WITH_MAP26_DISPATCH_ZONE'; $d }
    }
    @{
        Name = 'onboardPort takes the MVP transport port'
        Expect = 'onboardPort is 58005, which is the MVP onboard NDJSON transport'
        Mutate = { param($d) $d['onboardPort'] = 58005; $d }
    }
    @{
        Name = 'healthPort collides with onboardPort'
        Expect = 'healthPort is 58105, already taken by onboardPort'
        Mutate = { param($d) $d['healthPort'] = $d['onboardPort']; $d }
    }
    @{
        Name = 'listenAddress is the wildcard'
        Expect = 'listenAddress is the wildcard'
        Mutate = { param($d) $d['listenAddress'] = '0.0.0.0'; $d }
    }
    @{
        Name = 'listenAddress is loopback'
        Expect = 'listenAddress is loopback'
        Mutate = { param($d) $d['listenAddress'] = '127.0.0.1'; $d }
    }
    @{
        # Two failures, both correct and both named: moving baseUrl to the production port also
        # makes it disagree with fakeMesIngest.port, which is still 58188. Listing the second
        # one rather than loosening the assertion is the point -- an unexplained extra failure
        # is how a check that fires for the wrong reason hides.
        Name = 'mesIngest.baseUrl points at the production MesIngest'
        Expect = @(
            'points at port 5088, the production MesIngest'
            'does not match the port in mesIngest.baseUrl'
        )
        Mutate = {
            param($d)
            $d['mesIngest']['baseUrl'] = 'http://127.0.0.1:5088'
            $d
        }
    }
    @{
        Name = 'mesIngest.baseUrl is reachable from the plant network'
        Expect = 'is not loopback'
        Mutate = {
            param($d)
            $d['mesIngest']['baseUrl'] = 'http://172.19.205.222:58188'
            $d
        }
    }
    @{
        Name = 'fakeMesIngest.port disagrees with mesIngest.baseUrl'
        Expect = 'does not match the port in mesIngest.baseUrl'
        Mutate = { param($d) $d['fakeMesIngest']['port'] = 58189; $d }
    }

    # --- RouteGraph: acceptance item 3 -------------------------------------------------
    @{
        Name = 'routeGraph section removed entirely'
        Expect = 'routeGraph must be an object'
        Mutate = { param($d) $d.Remove('routeGraph'); $d }
    }
    @{
        Name = 'routeGraph present but an empty object'
        Expect = 'routeGraph.enabled must be stated explicitly'
        Mutate = { param($d) $d['routeGraph'] = @{}; $d }
    }
    @{
        Name = 'routeGraph.enabled is a string rather than a boolean'
        Expect = 'routeGraph.enabled must be a JSON boolean'
        Mutate = { param($d) $d['routeGraph']['enabled'] = 'true'; $d }
    }
    @{
        Name = 'routeGraph.enabled true with no mapId'
        Expect = 'routeGraph.mapId must be set explicitly'
        Mutate = { param($d) $d['routeGraph'].Remove('mapId'); $d }
    }
    @{
        Name = 'routeGraph refresh budget not larger than its period'
        Expect = 'routeGraph.runtimeStateMaxAge'
        Mutate = { param($d) $d['routeGraph']['runtimeStateMaxAge'] = '00:00:05'; $d }
    }
    @{
        Name = 'route graph enabled under a disabled journey runtime'
        Expect = 'the engine only refreshes inside a runtime iteration'
        Mutate = { param($d) $d['journeyRuntime']['enabled'] = $false; $d }
    }
    @{
        Name = 'the two mapIds disagree'
        Expect = 'disagree'
        Mutate = { param($d) $d['journeyRuntime']['mapId'] = 27; $d }
    }

    # --- Vehicle whitelist: acceptance item 4 ------------------------------------------
    @{
        Name = "agvId is 'AGV02', a plausible but wrong spelling"
        Expect = "does not match the RIoT deviceName of agv02"
        Mutate = { param($d) $d['journeyRuntime']['agvId'] = 'AGV02'; $d }
    }
    @{
        Name = "agvId differs only in case from the registered name"
        Expect = "does not match the RIoT deviceName of agv02"
        Mutate = { param($d) $d['journeyRuntime']['agvId'] = '老厂前线新多仓位2 '; $d }
    }
    # Zero-width lookalikes (found by the S1 re-review mutation check). PowerShell's -eq/-ceq/
    # -ccontains are culture comparisons that skip zero-width characters, so each of these passed
    # an allowlist it only looks like it is on -- evidence review3-string-equality.txt. The
    # allowlists now compare Ordinal; each case goes red if its comparison is put back.
    @{
        Name = 'agvId with a zero-width space appended'
        Expect = "does not match the RIoT deviceName of agv02"
        Mutate = { param($d) $d['journeyRuntime']['agvId'] = "老厂前线新多仓位2$([char]0x200B)"; $d }
    }
    @{
        Name = 'vehicleKey with a zero-width space appended'
        Expect = 'is not a spare vehicle'
        Mutate = { param($d) $d['journeyRuntime']['vehicleKey'] = "BROKERX-f38975561adf46ccb1d2f23833c7d0e4$([char]0x200B)"; $d }
    }
    @{
        # -eq was case-insensitive as well as culture-aware; RIoT's deviceKey is exact.
        Name = 'vehicleKey in lower case'
        Expect = 'is not a spare vehicle'
        Mutate = { param($d) $d['journeyRuntime']['vehicleKey'] = 'brokerx-f38975561adf46ccb1d2f23833c7d0e4'; $d }
    }
    @{
        Name = "vehicleKey is agv01's"
        Expect = 'the vehicle the MVP service is driving in production'
        Mutate = {
            param($d)
            $d['journeyRuntime']['vehicleKey'] = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
            $d
        }
    }
    @{
        Name = "agvId is agv01's while the key still names agv02"
        Expect = 'the vehicle the MVP service is driving in production'
        Mutate = { param($d) $d['journeyRuntime']['agvId'] = '老厂前线新多仓位1'; $d }
    }
    @{
        Name = 'agvId is a list with agv01 mixed in'
        Expect = 'must be a single string'
        Mutate = {
            param($d)
            $d['journeyRuntime']['agvId'] = @('老厂前线新多仓位2', '老厂前线新多仓位1')
            $d
        }
    }
    @{
        Name = 'agvId names agv03 while vehicleKey still names agv02'
        Expect = "does not match the RIoT deviceName of agv02"
        Mutate = { param($d) $d['journeyRuntime']['agvId'] = '老厂前线新多仓位3'; $d }
    }
    @{
        Name = 'vehicleKey is a well-formed key of no registered vehicle'
        Expect = 'is not a spare vehicle'
        Mutate = {
            param($d)
            $d['journeyRuntime']['vehicleKey'] = 'BROKERX-00000000000000000000000000000000'
            $d
        }
    }
    @{
        Name = 'journeyRuntime.vehicleKey removed'
        Expect = 'journeyRuntime.vehicleKey must be set explicitly'
        Mutate = { param($d) $d['journeyRuntime'].Remove('vehicleKey'); $d }
    }

    # --- Key whitelist: control-server#262 review, finding 3 ---------------------------
    @{
        # The case the review found: a roster the pair check never reads, copied verbatim into
        # the overlay, and accepted by the server's own validator as long as it *contains* the
        # primary pair.
        Name = 'journeyRuntime.fleet roster with agv01 mixed in'
        Expect = 'journeyRuntime.fleet is a vehicle roster'
        Mutate = {
            param($d)
            $d['journeyRuntime']['fleet'] = @(
                @{ agvId = '老厂前线新多仓位2'; vehicleKey = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4' }
                @{ agvId = '老厂前线新多仓位1'; vehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85' }
            )
            $d
        }
    }
    @{
        # The same roster spelled the way the C# property is. -AsHashtable is case-sensitive,
        # so a check for 'fleet' by name would not see it; .NET configuration would bind it.
        Name = 'journeyRuntime.Fleet, capitalised as the C# property'
        Expect = 'journeyRuntime.Fleet is a vehicle roster'
        Mutate = {
            param($d)
            $d['journeyRuntime']['Fleet'] = @(@{ agvId = '老厂前线新多仓位1'; vehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85' })
            $d
        }
    }
    @{
        # Found while fixing the above: the pair check reads 'agvId', the configuration system
        # reads 'agvId' and 'AgvId' as one setting. Nothing that reads keys by name sees this.
        Name = 'a second agvId spelled AgvId naming agv01'
        Expect = 'journeyRuntime.AgvId differs only in case from journeyRuntime.agvId'
        Mutate = { param($d) $d['journeyRuntime']['AgvId'] = '老厂前线新多仓位1'; $d }
    }
    @{
        Name = 'an unknown top-level key (a typo of a real one)'
        Expect = 'listenAdress is not a key this deployment knows'
        Mutate = { param($d) $d['listenAdress'] = '0.0.0.0'; $d }
    }
    @{
        # A known key with a zero-width space appended: .NET binds it as a different, unknown key,
        # so the setting it looks like is silently not set. It must read as unknown, not as the
        # key it resembles and not as a case variant of it.
        Name = 'a known key with a zero-width space appended'
        Expect = "is not a key this deployment knows"
        Mutate = { param($d) $d['journeyRuntime']["enabled$([char]0x200B)"] = $true; $d }
    }
    @{
        # From the S1 re-review (round 3): an extra key that looks like vehicleKey, carrying agv01's
        # key. .NET does not bind it, but it would be written as-is into appsettings.Production.json
        # -- a dead key with the production vehicle's value in the production configuration.
        Name = "a lookalike vehicleKey key carrying agv01's key"
        Expect = "is not a key this deployment knows"
        Mutate = { param($d) $d['journeyRuntime']["vehicleKey$([char]0x200B)"] = 'BROKERX-0c20ff0600d644869a6a80c186065d85'; $d }
    }

    # --- The gate that moves a car ------------------------------------------------------
    @{
        Name = 'riotCreateDispatch section removed'
        Expect = 'riotCreateDispatch must be an object'
        Mutate = { param($d) $d.Remove('riotCreateDispatch'); $d }
    }
    @{
        Name = 'riotCreateDispatch opened without the explicit switch'
        Expect = 'Placing RIoT orders moves a vehicle'
        Mutate = { param($d) $d['riotCreateDispatch']['enabled'] = $true; $d }
    }

    # --- The gate that cancels an order on our vehicle (control-server#330) ---------------
    @{
        Name = 'riotForeignOrderCancel section removed'
        Expect = 'riotForeignOrderCancel must be an object'
        Mutate = { param($d) $d.Remove('riotForeignOrderCancel'); $d }
    }
    @{
        Name = 'riotForeignOrderCancel.enabled not stated'
        Expect = 'riotForeignOrderCancel.enabled must be stated explicitly'
        Mutate = { param($d) $d['riotForeignOrderCancel'].Remove('enabled'); $d }
    }
    @{
        Name = 'riotForeignOrderCancel.enabled a string'
        Expect = 'riotForeignOrderCancel.enabled must be a JSON boolean'
        Mutate = { param($d) $d['riotForeignOrderCancel']['enabled'] = 'false'; $d }
    }
    @{
        Name = 'riotForeignOrderCancel opened without the explicit switch'
        Expect = 'stops a vehicle someone else set moving'
        Mutate = { param($d) $d['riotForeignOrderCancel']['enabled'] = $true; $d }
    }

    # --- The station clearance exit (control-server#454) ---------------------------------
    # Both sections are stated, never inherited: an absent section deploys an instance whose
    # clearance exit is silently unavailable (alarms 2271/2272, ORDER_HANG, no onboard entry).
    @{
        Name = 'vehicleFaultRecovery absent'
        Expect = 'vehicleFaultRecovery must be an object'
        Mutate = { param($d) $d.Remove('vehicleFaultRecovery'); $d }
    }
    @{
        Name = 'vehicleFaultRecovery.enabled absent'
        Expect = 'vehicleFaultRecovery.enabled must be stated explicitly'
        Mutate = { param($d) $d['vehicleFaultRecovery'].Remove('enabled'); $d }
    }
    @{
        Name = 'vehicleFaultRecovery.enabled as a string'
        Expect = "vehicleFaultRecovery.enabled must be a JSON boolean, got 'true'"
        Mutate = { param($d) $d['vehicleFaultRecovery']['enabled'] = 'true'; $d }
    }
    @{
        # The overlay writes this instance's own variable name; a definition that names one could
        # point the parallel service at the MVP's credential.
        Name = 'vehicleFaultRecovery names its own credential variable'
        Expect = 'vehicleFaultRecovery.credentialEnvironmentVariable is not a key this deployment knows'
        Mutate = { param($d) $d['vehicleFaultRecovery']['credentialEnvironmentVariable'] = 'CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL'; $d }
    }
    @{
        Name = 'fieldOperatorRoles absent'
        Expect = 'fieldOperatorRoles must be an object'
        Mutate = { param($d) $d.Remove('fieldOperatorRoles'); $d }
    }
    @{
        Name = 'fieldOperatorRoles.path absent'
        Expect = 'fieldOperatorRoles.path must be a non-empty path'
        Mutate = { param($d) $d['fieldOperatorRoles'].Remove('path'); $d }
    }
    @{
        # Every install replaces the install root; a roster there would be wiped by the next one.
        Name = 'fieldOperatorRoles.path inside the install root'
        Expect = "fieldOperatorRoles.path ('C:\Program Files\8005 AGV\ControlServer.V2\field-operator-roles.json') must be inside opsRoot"
        Mutate = { param($d) $d['fieldOperatorRoles']['path'] = 'C:\Program Files\8005 AGV\ControlServer.V2\field-operator-roles.json'; $d }
    }
    @{
        Name = 'fieldOperatorRoles.path written with forward slashes'
        Expect = "fieldOperatorRoles.path ('D:/zhengyushao/control-server-v2-ops/field-operator-roles.json') uses '/'"
        Mutate = { param($d) $d['fieldOperatorRoles']['path'] = 'D:/zhengyushao/control-server-v2-ops/field-operator-roles.json'; $d }
    }
    @{
        Name = 'fieldOperatorRoles.onboardClearanceEntryDeclared absent'
        Expect = 'fieldOperatorRoles.onboardClearanceEntryDeclared must be stated explicitly'
        Mutate = { param($d) $d['fieldOperatorRoles'].Remove('onboardClearanceEntryDeclared'); $d }
    }
    @{
        Name = 'fieldOperatorRoles.onboardClearanceEntryDeclared as a number'
        Expect = "fieldOperatorRoles.onboardClearanceEntryDeclared must be a JSON boolean, got '1'"
        Mutate = { param($d) $d['fieldOperatorRoles']['onboardClearanceEntryDeclared'] = 1; $d }
    }
)

foreach ($case in $cases) {
    # A mutation that throws (it reached for a section the baseline lacks) is one red case, not
    # the end of the suite.
    try {
        $mutated = & $case.Mutate (Copy-Definition $baseline)
    } catch {
        Write-Result -Ok $false -Name $case.Name -Detail "the mutation threw: $($_.Exception.Message)"
        continue
    }

    # The injection must have changed something. Without this, a Mutate that silently did
    # nothing would still "pass" as long as some unrelated check happened to fire -- and a
    # case that asserts a failure the baseline already has asserts nothing at all.
    # Ordinal: -eq is a culture comparison that ignores case and skips zero-width characters, so a
    # mutation that only changes case or appends U+200B looked like no change at all -- found when
    # the zero-width allowlist cases were added. Red rather than green, but red for the wrong reason.
    if ([string]::Equals((Get-Fingerprint $mutated), $baselineFingerprint, [StringComparison]::Ordinal)) {
        Write-Result -Ok $false -Name $case.Name -Detail 'the mutation did not change the definition'
        continue
    }

    [string[]] $failures = @(Test-ParallelInstanceDefinition -Definition $mutated)
    [string[]] $expected = @($case.Expect)
    # A literal substring match, not -like: several expected fragments contain '*', '?' or '[1]',
    # which -like would read as wildcards and a character class.
    $unmatched = @($expected | Where-Object { $fragment = $_; -not ($failures | Where-Object { $_.Contains($fragment) }) })

    if ($failures.Count -eq 0) {
        Write-Result -Ok $false -Name $case.Name -Detail 'accepted a definition that must be refused'
    } elseif ($unmatched.Count -gt 0) {
        Write-Result -Ok $false -Name $case.Name `
            -Detail ("expected '" + ($unmatched -join "', '") + "', got: " + ($failures -join ' | '))
    } elseif ($failures.Count -ne $expected.Count) {
        # Not merely noise: a mutation that trips more checks than it names makes it impossible
        # to say which one is load-bearing, and a later change could remove the real guard while
        # this case stays green on a bystander. Every extra failure has to be listed in Expect,
        # which forces whoever adds it to explain why the injection reaches it.
        Write-Result -Ok $false -Name $case.Name `
            -Detail ("expected exactly $($expected.Count) failure(s), got $($failures.Count): " + ($failures -join ' | '))
    } else {
        Write-Result -Ok $true -Name $case.Name -Detail ''
    }
}

Write-Host ''
Write-Host 'Switch cases (the escape hatch must work, or it is decoration)' -ForegroundColor Cyan

$opened = Copy-Definition $baseline
$opened['riotCreateDispatch']['enabled'] = $true
$withSwitch = @(Test-ParallelInstanceDefinition -Definition $opened -AllowRiotCreateDispatch)
Write-Result -Ok ($withSwitch.Count -eq 0) `
    -Name '-AllowRiotCreateDispatch accepts the same definition the default run refused' `
    -Detail ("expected no failures, got: " + ($withSwitch -join ' | '))

# ... and the switch must not be a blanket amnesty.
$openedAndWrong = Copy-Definition $baseline
$openedAndWrong['riotCreateDispatch']['enabled'] = $true
$openedAndWrong['journeyRuntime']['vehicleKey'] = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
$stillRefused = @(Test-ParallelInstanceDefinition -Definition $openedAndWrong -AllowRiotCreateDispatch)
Write-Result -Ok ($stillRefused.Count -eq 1 -and $stillRefused[0] -like '*driving in production*') `
    -Name '-AllowRiotCreateDispatch still refuses agv01' `
    -Detail ("expected the agv01 refusal alone, got: " + ($stillRefused -join ' | '))

# The foreign order cancel gate has a switch of its own (control-server#330), and neither
# switch opens the other gate.
$cancelOpened = Copy-Definition $baseline
$cancelOpened['riotForeignOrderCancel']['enabled'] = $true
$withCancelSwitch = @(Test-ParallelInstanceDefinition -Definition $cancelOpened -AllowRiotForeignOrderCancel)
Write-Result -Ok ($withCancelSwitch.Count -eq 0) `
    -Name '-AllowRiotForeignOrderCancel accepts the definition the default run refused' `
    -Detail ("expected no failures, got: " + ($withCancelSwitch -join ' | '))
$wrongSwitch = @(Test-ParallelInstanceDefinition -Definition $cancelOpened -AllowRiotCreateDispatch)
Write-Result -Ok ($wrongSwitch.Count -eq 1 -and $wrongSwitch[0].Contains('-AllowRiotForeignOrderCancel')) `
    -Name '-AllowRiotCreateDispatch does not open the foreign order cancel gate' `
    -Detail ("expected the cancel gate's refusal alone, got: " + ($wrongSwitch -join ' | '))
$cancelOpenedAndWrong = Copy-Definition $baseline
$cancelOpenedAndWrong['riotForeignOrderCancel']['enabled'] = $true
$cancelOpenedAndWrong['journeyRuntime']['vehicleKey'] = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
$cancelStillRefused = @(Test-ParallelInstanceDefinition -Definition $cancelOpenedAndWrong -AllowRiotForeignOrderCancel)
Write-Result -Ok ($cancelStillRefused.Count -eq 1 -and $cancelStillRefused[0] -like '*driving in production*') `
    -Name '-AllowRiotForeignOrderCancel still refuses agv01' `
    -Detail ("expected the agv01 refusal alone, got: " + ($cancelStillRefused -join ' | '))
$shippedOverlay = New-ParallelInstanceConfigurationOverlay -Definition $baseline
Write-Result -Ok ($shippedOverlay['RiotForeignOrderCancel']['enabled'] -eq $false) `
    -Name 'the shipped definition deploys with the foreign order cancel gate closed' `
    -Detail ("RiotForeignOrderCancel.enabled = $($shippedOverlay['RiotForeignOrderCancel']['enabled'])")

Write-Host ''
Write-Host 'Second layer on its own: the production denylist normalises and fails closed' -ForegroundColor Cyan

# The allowlist refuses all of these first, so the negative cases above never reach the second
# layer. It is exercised directly here, because a second layer nobody tests is a layer nobody
# knows still works. Each spelling of a production path must be recognised as production.
$secondLayer = [ordered]@{
    'C:/Program Files/8005 AGV/ControlServer' = $true
    'C:\ProgramData\8005\.\ControlServer' = $true
    'C:\ProgramData\8005\x\..\ControlServer' = $true
    'D:\zhengyushao\MesIngest\' = $true
    'D:\zhengyushao\ControlServer.previous' = $true
    'D:\zhengyushao' = $true
    'C:\PROGRA~1\8005 AGV\ControlServer' = $true
    '\\?\C:\ProgramData\8005\ControlServer' = $true
    'D:\zhengyushao\ControlServer.V2' = $false
    'C:\ProgramData\8005\ControlServer.V2' = $false
}
foreach ($path in $secondLayer.Keys) {
    $got = Test-ParallelInstancePathIsProduction -Path $path
    $why = $secondLayer[$path] ? 'must be recognised as production (or unreadable, which fails closed)' : 'must NOT be flagged (no false positives on our own paths)'
    Write-Result -Ok ($got -eq $secondLayer[$path]) -Name "denylist: '$path' $why" -Detail "got $got"
}

Write-Host ''
Write-Host 'Removal sequence: order and stop conditions (control-server#262 re-review, S1)' -ForegroundColor Cyan

<#
    Invoke-ParallelRemovalSequence with recording actions instead of real ones. The two cases
    that matter most are the ones the first uninstaller got wrong: a refusal from the product
    uninstaller must stop everything, and a directory outside the allowlist must never reach
    the delete action -- even when it arrives through a staging glob.
#>
function New-RecordingAction {
    <#
        -LinkPaths: paths the ReparsePoint action reports as links. -LinkProbeThrows: it throws
        instead. -Emit: every action also writes this to the output stream, as the real product
        uninstaller does with its PASS line.
    #>
    param([System.Collections.Generic.List[string]] $Log, [hashtable] $Throw = @{}, [string[]] $GlobResult = @(),
        [string[]] $LinkPaths = @(), [switch] $LinkProbeThrows, [string] $Emit)
    $actions = @{}
    foreach ($kind in @('Service', 'ScheduledTask', 'Process', 'FirewallRule', 'MachineEnvironment', 'Directory')) {
        $k = $kind
        $message = $Throw[$kind]
        $actions[$kind] = { param($item) $Log.Add("$k|$($item.Name)"); if ($Emit) { $Emit }; if ($message) { throw $message } }.GetNewClosure()
    }
    $actions['DirectoryPattern'] = { param($item) $Log.Add("DirectoryPattern|$($item.Name)"); $GlobResult }.GetNewClosure()
    $throws = [bool] $LinkProbeThrows
    $actions['ReparsePoint'] = { param($path) $Log.Add("ReparsePoint|$path"); if ($throws) { throw 'access denied' }; $LinkPaths -contains $path }.GetNewClosure()
    return $actions
}
$footprintForSequence = @(Get-ParallelInstanceFootprint -Definition $baseline)

# 1. The product uninstaller refuses. Nothing else may happen.
$log = [System.Collections.Generic.List[string]]::new()
$outcome = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -RemoveData `
    -Actions (New-RecordingAction -Log $log -Throw @{ Service = 'Refusing to uninstall the production deployment without -AllowProductionService: 8005 AGV ControlServer' })
$afterService = @($log | Where-Object { $_ -notlike 'Service|*' })
Write-Result -Ok ($outcome.Aborted -and $afterService.Count -eq 0 -and $outcome.AbortReason -like '*Refusing to uninstall the production deployment*') `
    -Name 'a production refusal from the product uninstaller aborts before ANY other action' `
    -Detail ("aborted=$($outcome.Aborted); actions after the service step: " + ($afterService -join ', '))

# 2. A staging glob returns the MVP's staging directory. It must not reach the delete action,
#    and the directory phase stops there.
$log = [System.Collections.Generic.List[string]]::new()
$outcome = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence `
    -Actions (New-RecordingAction -Log $log -GlobResult @('D:\zhengyushao\ControlServer.incoming-20261008-101010'))
$deletedMvp = @($log | Where-Object { $_ -like 'Directory|D:\zhengyushao\ControlServer.incoming-*' })
$deletedAfter = @($log | Where-Object { $_ -eq "Directory|$($baseline['stagingRoot'])" })
Write-Result -Ok ($outcome.Aborted -and $deletedMvp.Count -eq 0 -and $deletedAfter.Count -eq 0) `
    -Name "a glob match outside the allowlist is never deleted, and the directory phase stops" `
    -Detail ("aborted=$($outcome.Aborted) reason=$($outcome.AbortReason); deleted MVP=$($deletedMvp.Count); later dirs=$($deletedAfter.Count)")

# 3. A footprint that somehow names a production directory (the definition checks would stop
#    this first; the sequence must not rely on that).
$log = [System.Collections.Generic.List[string]]::new()
$poisoned = @($footprintForSequence) + [pscustomobject]@{ Kind = 'Directory'; Name = 'C:\ProgramData\8005\ControlServer'; Data = $false }
$outcome = Invoke-ParallelRemovalSequence -Footprint $poisoned -Actions (New-RecordingAction -Log $log)
$reached = @($log | Where-Object { $_ -eq 'Directory|C:\ProgramData\8005\ControlServer' })
Write-Result -Ok ($outcome.Aborted -and $reached.Count -eq 0) `
    -Name 'a production directory in the footprint itself never reaches the delete action' `
    -Detail ("aborted=$($outcome.Aborted); reached=$($reached.Count)")

# 4. The ordinary path: everything runs, data is kept unless -RemoveData.
$log = [System.Collections.Generic.List[string]]::new()
$outcome = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -Actions (New-RecordingAction -Log $log)
$dataDeleted = @($log | Where-Object { $_ -in @("Directory|$($baseline['dataRoot'])", "Directory|$($baseline['opsRoot'])", "Directory|$($baseline['backupRoot'])") })
$serviceFirst = $log.Count -gt 0 -and $log[0] -like 'Service|*'
Write-Result -Ok (-not $outcome.Aborted -and $dataDeleted.Count -eq 0 -and $serviceFirst -and $outcome.Failed.Count -eq 0) `
    -Name 'the ordinary run: service first, data kept without -RemoveData' `
    -Detail ("aborted=$($outcome.Aborted); first=$($log[0]); data deleted=$($dataDeleted.Count); failed=$($outcome.Failed -join ', ')")
$log = [System.Collections.Generic.List[string]]::new()
$null = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -Actions (New-RecordingAction -Log $log) -RemoveData
$dataDeleted = @($log | Where-Object { $_ -in @("Directory|$($baseline['dataRoot'])", "Directory|$($baseline['opsRoot'])", "Directory|$($baseline['backupRoot'])") })
Write-Result -Ok ($dataDeleted.Count -eq 3) -Name '-RemoveData deletes exactly the three data directories as well' -Detail "data deleted=$($dataDeleted.Count)"

# 5. A failure that cannot reach the MVP (a task that will not unregister) is recorded and the
#    sequence carries on -- the stop is reserved for the conditions above.
$log = [System.Collections.Generic.List[string]]::new()
$outcome = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -Actions (New-RecordingAction -Log $log -Throw @{ ScheduledTask = 'access denied' })
$dirs = @($log | Where-Object { $_ -like 'Directory|*' })
Write-Result -Ok (-not $outcome.Aborted -and $outcome.Failed.Count -eq 1 -and $dirs.Count -gt 0) `
    -Name 'a failed task removal is recorded, not an abort' `
    -Detail ("aborted=$($outcome.Aborted); failed=$($outcome.Failed -join ', '); directories run=$($dirs.Count)")

# 6. S1 re-review, question 2. One of our own directories turns out to be a junction. It is
#    refused before the delete action sees it and the directory phase stops -- the directories
#    after it in the footprint are not reached either. The link is the SECOND directory, so the
#    first one having been deleted shows the probe ran per directory, not once.
$directoryTargets = @($footprintForSequence | Where-Object Kind -eq 'Directory' | Where-Object { -not $_.Data } | ForEach-Object Name)
$linkTarget = $directoryTargets[1]
$log = [System.Collections.Generic.List[string]]::new()
$outcome = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -Actions (New-RecordingAction -Log $log -LinkPaths @($linkTarget))
$deletedLink = @($log | Where-Object { $_ -eq "Directory|$linkTarget" })
$deletedFirst = @($log | Where-Object { $_ -eq "Directory|$($directoryTargets[0])" })
$deletedLater = @($log | Where-Object { $_ -in @($directoryTargets | Select-Object -Skip 2 | ForEach-Object { "Directory|$_" }) })
Write-Result -Ok ($outcome.Aborted -and $outcome.AbortReason -like '*junction or symbolic link*' -and $deletedLink.Count -eq 0 -and $deletedFirst.Count -eq 1 -and $deletedLater.Count -eq 0) `
    -Name 'a directory that is a junction is never deleted, and the directory phase stops there' `
    -Detail ("aborted=$($outcome.Aborted) reason=$($outcome.AbortReason); link deleted=$($deletedLink.Count) first=$($deletedFirst.Count) later=$($deletedLater.Count)")

# 7. The link probe itself fails (access denied reading attributes). Unknown means "yes".
$log = [System.Collections.Generic.List[string]]::new()
$outcome = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -Actions (New-RecordingAction -Log $log -LinkProbeThrows)
$anyDeleted = @($log | Where-Object { $_ -like 'Directory|*' })
Write-Result -Ok ($outcome.Aborted -and $anyDeleted.Count -eq 0) `
    -Name 'a link probe that throws is treated as a link: nothing is deleted' `
    -Detail ("aborted=$($outcome.Aborted); directories deleted=$($anyDeleted.Count)")

# 8. A caller that forgets the link probe is refused before anything runs.
$log = [System.Collections.Generic.List[string]]::new()
$withoutProbe = New-RecordingAction -Log $log
$withoutProbe.Remove('ReparsePoint')
$refusedMissing = $null
try { $null = Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -Actions $withoutProbe } catch { $refusedMissing = $_.Exception.Message }
Write-Result -Ok ($refusedMissing -like '*missing action(s) ReparsePoint*' -and $log.Count -eq 0) `
    -Name 'a removal sequence without its link probe refuses to start' `
    -Detail ("refusal=$refusedMissing; actions run=$($log.Count)")

# 9. Actions that write output (the product uninstaller prints a PASS line) do not change what
#    the sequence returns: one object, not an array with the output in front of it.
$log = [System.Collections.Generic.List[string]]::new()
$returned = @(Invoke-ParallelRemovalSequence -Footprint $footprintForSequence -Actions (New-RecordingAction -Log $log -Emit 'ControlServer uninstall PASS. Result: x'))
Write-Result -Ok ($returned.Count -eq 1 -and $returned[0].PSObject.Properties.Name -contains 'Aborted') `
    -Name 'action output does not leak into the sequence result' `
    -Detail ("returned $($returned.Count) object(s): " + (($returned | ForEach-Object { $_.GetType().Name }) -join ', '))

Write-Host ''
Write-Host 'Uninstaller refuses the S1 definition before printing any plan' -ForegroundColor Cyan

# The script itself, not only the module: with the re-review's reproduction definition it must
# throw at the assertion, and no 'remove' line may have been printed -- before the fix this run
# listed the MVP install root and the production database directory.
$s1 = Copy-Definition $baseline
$s1['installRoot'] = 'C:/Program Files/8005 AGV/ControlServer'
$s1['dataRoot'] = 'C:/ProgramData/8005/ControlServer'
$s1['packageRoot'] = 'D:\zhengyushao\ControlServer.previous'
$s1['backupRoot'] = 'D:\zhengyushao\MesIngest'
$s1Path = Join-Path ([IO.Path]::GetTempPath()) "s1-definition-$([guid]::NewGuid().ToString('N')).json"
[IO.File]::WriteAllText($s1Path, (ConvertTo-Json -InputObject $s1 -Depth 12), [Text.UTF8Encoding]::new($false))
try {
    $out = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Uninstall-ParallelInstanceLocal.ps1') `
            -InstanceDefinitionPath $s1Path -ConfirmUninstall -RemoveData -WhatIf 2>&1 | ForEach-Object { "$_" })
    $exit = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $s1Path -Force -ErrorAction SilentlyContinue
}
$planLines = @($out | Where-Object { $_ -match '\]\s+(remove|keep)\s' })
$refused = [bool]($out | Where-Object { $_ -like '*was refused*' })
Write-Result -Ok ($exit -ne 0 -and $refused -and $planLines.Count -eq 0) `
    -Name 'Uninstall-ParallelInstanceLocal.ps1 -WhatIf throws at the assertion and lists nothing' `
    -Detail ("exit=$exit refused=$refused plan lines=$($planLines.Count): " + ($planLines -join ' / '))

Write-Host ''
Write-Host 'The service step fails closed however the product uninstaller fails (S1 re-review, M1)' -ForegroundColor Cyan

<#
    The removal sequence aborts on a throw from the Service action. The reviewer showed a product
    script can fail without throwing -- exit 1, Write-Error under Continue, a failing native
    command -- and the sequence then went on to delete eight directories. Each fake below fails
    one way, and Invoke-ParallelProductUninstaller must throw for each, for the reason named in
    Expect: that is "which conjunct made it red", read off the message. The first fake is the
    only one that does what the real script does on success; it must be accepted, or every
    refusal below means nothing.

    Every fake appends a line to its own marker file when it runs, so "it was never called" (the
    stale-result case) is observed, not assumed.
#>
$fakeRoot = Join-Path ([IO.Path]::GetTempPath()) "cs262-fake-uninstaller-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fakeRoot | Out-Null
$fakeServiceName = "8005 AGV ControlServer V2 selftest-$([guid]::NewGuid().ToString('N'))"
# Each fake appends to <name>.ran when it runs, and records the result path it was handed in
# <name>.paths -- so "it ran" and "which name it was given" are observed, not assumed.
$fakeHeader = @'
[CmdletBinding()]
param([string] $ServiceName, [string] $InstallRoot, [string] $DataRoot, [string] $ResultPath, [switch] $ConfirmUninstall)
$fakeName = [IO.Path]::GetFileNameWithoutExtension($PSCommandPath)
Add-Content -LiteralPath (Join-Path $PSScriptRoot "$fakeName.ran") 'ran'
Add-Content -LiteralPath (Join-Path $PSScriptRoot "$fakeName.paths") $ResultPath
function Write-FakeResult([string] $Result, [string] $Name, [bool] $Existed, [bool] $Removed) {
    $json = [ordered]@{ result = $Result; serviceName = $Name; serviceExisted = $Existed; serviceRemoved = $Removed } | ConvertTo-Json
    [IO.File]::WriteAllText($ResultPath, $json, [Text.UTF8Encoding]::new($false))
}
'@
$fakes = [ordered]@{
    'succeeds'              = @{ Body = "Write-FakeResult 'PASS' `$ServiceName `$true `$true`n'ControlServer uninstall PASS.'"; Expect = $null }
    'throws-production'     = @{ Body = "throw 'Refusing to uninstall the production deployment without -AllowProductionService: x'"; Expect = 'Refusing to uninstall the production deployment' }
    'exit-1'                = @{ Body = 'exit 1'; Expect = 'exit code 1' }
    'write-error-continue'  = @{ Body = "`$ErrorActionPreference = 'Continue'`nWrite-Error 'service would not stop'`n'after the error'"; Expect = 'without writing its result file' }
    'native-fails-last'     = @{ Body = '& cmd.exe /c exit 3'; Expect = 'exit code 3' }
    'pass-but-native-exit'  = @{ Body = "Write-FakeResult 'PASS' `$ServiceName `$true `$true`n& cmd.exe /c exit 5"; Expect = 'exit code 5' }
    'result-fail'           = @{ Body = "Write-FakeResult 'FAIL' `$ServiceName `$true `$true"; Expect = "result is 'FAIL'" }
    'result-other-service'  = @{ Body = "Write-FakeResult 'PASS' '8005 AGV ControlServer' `$true `$true"; Expect = "serviceName is '8005 AGV ControlServer'" }
    'service-not-removed'   = @{ Body = "Write-FakeResult 'PASS' `$ServiceName `$true `$false"; Expect = 'the service existed and was not removed' }
}
function Invoke-Fake([string] $Name) {
    $message = $null
    try {
        Invoke-ParallelProductUninstaller -UninstallerPath (Join-Path $fakeRoot "$Name.ps1") -ServiceName $fakeServiceName `
            -InstallRoot 'C:\Program Files\8005 AGV\ControlServer.V2' -DataRoot 'C:\ProgramData\8005\ControlServer.V2' `
            -ResultDirectory (Join-Path $fakeRoot "results-$Name") 6>$null 2>$null
    } catch { $message = $_.Exception.Message }
    return $message
}
try {
    foreach ($name in $fakes.Keys) {
        [IO.File]::WriteAllText((Join-Path $fakeRoot "$name.ps1"), $fakeHeader + "`n" + $fakes[$name].Body + "`n", [Text.UTF8Encoding]::new($false))
        $message = Invoke-Fake $name
        $ran = Test-Path -LiteralPath (Join-Path $fakeRoot "$name.ran")
        $expect = $fakes[$name].Expect
        if ($null -eq $expect) {
            Write-Result -Ok ($ran -and $null -eq $message) -Name "product uninstaller '$name' is accepted" -Detail "ran=$ran threw=$message"
        } else {
            Write-Result -Ok ($ran -and $null -ne $message -and $message.Contains($expect)) `
                -Name "product uninstaller '$name' fails the service step" -Detail "ran=$ran; expected '$expect'; threw: $message"
        }
    }

    # S1 re-review round 3, item 4: "written by this run" rests on a name nobody can predict. Run
    # the accepting fake a second time and read back the two names it was handed.
    $null = Invoke-Fake 'succeeds'
    $paths = @(Get-Content -LiteralPath (Join-Path $fakeRoot 'succeeds.paths'))
    $pattern = '\\uninstall-\d{8}-\d{6}-[0-9a-f]{32}\.json$'
    $unpredictable = $paths.Count -eq 2 -and $paths[0] -ne $paths[1] -and
        @($paths | Where-Object { $_ -cmatch $pattern }).Count -eq 2
    Write-Result -Ok $unpredictable -Name 'each run hands the product uninstaller a fresh, unguessable result path' `
        -Detail ("paths: " + ($paths -join ' | '))
} finally {
    Remove-Item -LiteralPath $fakeRoot -Recurse -Force -ErrorAction SilentlyContinue
}

<#
    S1 re-review round 3, item 5: pin the premise the positive confirmation rests on. One failure
    shape still gets through it: the product script hits Write-Error under Continue, carries on,
    removes the service and writes PASS at the end -- every signal says success. That cannot
    happen today because Uninstall-ControlServerLocal.ps1 sets $ErrorActionPreference = 'Stop' as
    its first statement and writes PASS once, at its end.

    A REGRESSION GUARD FOR THOSE TWO FACTS, NOT A PROOF OF THEM. It recognises exactly four ways of
    breaking them: a first statement other than the Stop assignment, 'PASS' written more or fewer
    than once, 'PASS' outside the last three top-level statements, and a trap. It does NOT see
    (round 4 review, measured): a later statement setting Continue again, $PSDefaultParameterValues,
    a try/catch that swallows an error, or 'PA' + 'SS' built up from pieces. If the product script
    changes shape in any way, re-read it by hand; this check going green then means little.
#>
# The check itself lives in ParallelHost.psm1 (Test-ParallelProductUninstallerPremise), because
# Get-ParallelProductUninstallerPath runs it at uninstall time on the copy about to run -- usually
# the installed package's, not this repository's (S1 re-review, round 4).
$productProblems = @(Test-ParallelProductUninstallerPremise -Source ([IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\Uninstall-ControlServerLocal.ps1'))))
Write-Result -Ok ($productProblems.Count -eq 0) `
    -Name "premise: Uninstall-ControlServerLocal.ps1 stops on any error and writes PASS once, at its end" `
    -Detail ($productProblems -join ' | ')
$premiseBreaks = [ordered]@{
    'ErrorActionPreference Continue'  = "`$ErrorActionPreference = 'Continue'`nDo-Work`n`$r = @{ result = 'PASS' }`nSave `$r`n'done'"
    'PASS written twice'              = "`$ErrorActionPreference = 'Stop'`nif (`$x) { `$r = @{ result = 'PASS' } }`nDo-Work`n`$r = @{ result = 'PASS' }`nSave `$r`n'done'"
    'PASS written early'              = "`$ErrorActionPreference = 'Stop'`n`$r = @{ result = 'PASS' }`nSave `$r`nDo-Work`nMore-Work`nEven-More`n'done'"
    'a trap that swallows errors'     = "`$ErrorActionPreference = 'Stop'`ntrap { continue }`nDo-Work`n`$r = @{ result = 'PASS' }`nSave `$r`n'done'"
}
foreach ($name in $premiseBreaks.Keys) {
    Write-Result -Ok (@(Test-ParallelProductUninstallerPremise -Source $premiseBreaks[$name]).Count -gt 0) -Name "premise check catches: $name" -Detail 'found nothing'
}

# At uninstall time: Get-ParallelProductUninstallerPath checks the copy it picks, and refuses one
# that breaks the premise. The candidate here is the fallback beside the scripts (no package root).
$premiseRoot = Join-Path ([IO.Path]::GetTempPath()) "cs262-premise-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $premiseRoot | Out-Null
try {
    $noPackage = [pscustomobject]@{ PackageRoot = (Join-Path $premiseRoot 'no-package'); PreviousRoot = (Join-Path $premiseRoot 'no-previous') }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\Uninstall-ControlServerLocal.ps1') -Destination $premiseRoot
    $picked = $null; $why = $null
    try { $picked = Get-ParallelProductUninstallerPath -Layout $noPackage -ScriptRoot $premiseRoot } catch { $why = $_.Exception.Message }
    Write-Result -Ok ($null -eq $why -and $picked -like '*Uninstall-ControlServerLocal.ps1') `
        -Name 'the real product uninstaller is picked at uninstall time' -Detail "threw: $why"
    Set-Content -LiteralPath (Join-Path $premiseRoot 'Uninstall-ControlServerLocal.ps1') -Value "`$ErrorActionPreference = 'Continue'`nDo-Work`n`$r = @{ result = 'PASS' }`nSave `$r`n'done'"
    $picked = $null; $why = $null
    try { $picked = Get-ParallelProductUninstallerPath -Layout $noPackage -ScriptRoot $premiseRoot } catch { $why = $_.Exception.Message }
    Write-Result -Ok ($null -eq $picked -and $null -ne $why -and $why.Contains('breaks the premise')) `
        -Name 'a product uninstaller that breaks the premise is refused at uninstall time' -Detail "picked: $picked; threw: $why"
} finally {
    Remove-Item -LiteralPath $premiseRoot -Recurse -Force -ErrorAction SilentlyContinue
}

<#
    A REGRESSION GUARD, NOT A PROOF. The service step is safe because Invoke-ParallelProductUninstaller
    requires positive confirmation and the removal sequence aborts on anything else -- that is built
    into the code. This scan only keeps the ordinary ways of calling the product script directly
    from creeping back into the uninstaller, which would route around that confirmation.

    What it looks at, in the uninstaller: every command must be named by a bare word (nothing named
    by a variable, an expression, a string, an index, (Get-Command ...) or a dot-sourced path); none
    of Invoke-Expression, Start-Process, pwsh, cmd, Invoke-Command, Start-Job; no
    [scriptblock]::Create, .InvokeScript(, NewScriptBlock; the product script's file name nowhere
    (Get-ParallelProductUninstallerPath in ParallelHost.psm1 knows it); and exactly one call of
    Invoke-ParallelProductUninstaller. Each bypass the round-3 review found has a synthetic snippet
    below that must be found, and one clean snippet must not.

    Known ways past it, on purpose not chased (S1 re-review, round 4: a blacklist is never
    finished): aliases and function definitions (Set-Alias, New-Alias, Set-Item function:);
    [powershell]::new(), New-Object System.Diagnostics.Process, [Diagnostics.Process]::Start,
    Invoke-CimMethod Win32_Process Create, Register-ScheduledTask; and anything defined in a module,
    which this scan does not read.
#>
function Find-ProductScriptBypass {
    param([string] $Source)
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Source, [ref]$null, [ref]$null)
    $forbiddenCommands = @('Invoke-Expression', 'iex', 'Start-Process', 'saps', 'start', 'pwsh', 'pwsh.exe', 'powershell',
        'powershell.exe', 'cmd', 'cmd.exe', 'Invoke-Command', 'icm', 'Start-Job', 'sajb', 'Start-ThreadJob')
    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($command in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        $head = $command.CommandElements[0]
        $bare = $head -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $head.StringConstantType -eq 'BareWord'
        if (-not $bare -or $command.InvocationOperator -eq 'Dot') { $findings.Add("dynamic command: $($command.Extent.Text)"); continue }
        $name = ($head.Value -split '\\')[-1]
        if ($forbiddenCommands -contains $name) { $findings.Add("forbidden command: $($command.Extent.Text)") }
    }
    foreach ($member in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.MemberExpressionAst] }, $true)) {
        if ($member.Member.Extent.Text -in @('Create', 'InvokeScript', 'NewScriptBlock')) { $findings.Add("script-from-text member: $($member.Extent.Text)") }
    }
    if ($Source.Contains('Uninstall-ControlServerLocal')) { $findings.Add("the product script's name appears") }
    return $findings
}
$uninstallSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Uninstall-ParallelInstanceLocal.ps1'))
$realFindings = @(Find-ProductScriptBypass $uninstallSource)
$legitimateCalls = @(([System.Management.Automation.Language.Parser]::ParseInput($uninstallSource, [ref]$null, [ref]$null)).FindAll({ param($n)
            $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-ParallelProductUninstaller' }, $true))
Write-Result -Ok ($realFindings.Count -eq 0 -and $legitimateCalls.Count -eq 1) `
    -Name 'the uninstaller reaches the product script only through Invoke-ParallelProductUninstaller' `
    -Detail ("calls through the function=$($legitimateCalls.Count); findings: " + ($realFindings -join ' | '))
$bypasses = [ordered]@{
    'call operator on the variable'           = '& $productUninstaller -ServiceName x'
    'call operator on a quoted variable'      = '& "$productUninstaller" -ServiceName x'
    'call operator on a parenthesised value'  = '& ($productUninstaller) -ServiceName x'
    'a renamed variable'                      = '& $u -ServiceName x'
    'an indexed candidate list'               = '& $candidates[0] -ServiceName x'
    'dot-sourcing'                            = '. $u'
    'Get-Command as the command'              = '& (Get-Command $u) -ServiceName x'
    'Invoke-Expression'                       = 'Invoke-Expression "& $u"'
    'iex alias'                               = 'iex $text'
    'Start-Process pwsh'                      = "Start-Process pwsh -ArgumentList '-File', `$u -Wait"
    'pwsh -File'                              = 'pwsh -NoProfile -File $u'
    'module-qualified Invoke-Expression'      = 'Microsoft.PowerShell.Utility\Invoke-Expression $text'
    '[scriptblock]::Create'                   = '[scriptblock]::Create($text).Invoke()'
    'ExecutionContext.InvokeCommand'          = '$ExecutionContext.InvokeCommand.InvokeScript($text)'
    'the product script by name'              = 'C:\x\Uninstall-ControlServerLocal.ps1 -ServiceName x'
}
foreach ($name in $bypasses.Keys) {
    $hits = @(Find-ProductScriptBypass $bypasses[$name])
    Write-Result -Ok ($hits.Count -gt 0) -Name "bypass scanner finds: $name" -Detail "snippet: $($bypasses[$name])"
}
$cleanSnippet = 'Invoke-ParallelProductUninstaller -UninstallerPath (Get-ParallelProductUninstallerPath -Layout $l -ScriptRoot $r) -ServiceName $n'
Write-Result -Ok (@(Find-ProductScriptBypass $cleanSnippet).Count -eq 0) -Name 'bypass scanner is quiet on the one legitimate call' `
    -Detail ((Find-ProductScriptBypass $cleanSnippet) -join ' | ')

Write-Host ''
Write-Host 'Junctions: measured behaviour, and the refusal (S1 re-review, question 2)' -ForegroundColor Cyan

<#
    Real file system, a temporary directory, no elevation needed (junctions, unlike directory
    symbolic links, do not need it -- which is also why symbolic links are not covered here).
    The first two cases pin what Remove-Item does today, so the day a pwsh follows a junction
    inside a directory it deletes, this goes red instead of the MVP's files going missing.
#>
$junctionRoot = Join-Path ([IO.Path]::GetTempPath()) "cs262-links-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $junctionRoot | Out-Null
function New-PreciousTarget([string] $Name) {
    $target = Join-Path $junctionRoot $Name
    New-Item -ItemType Directory -Path $target | Out-Null
    Set-Content -LiteralPath (Join-Path $target 'precious.txt') -Value 'x'
    return $target
}
try {
    $target = New-PreciousTarget 'mvp-inside'
    $ours = Join-Path $junctionRoot 'ControlServer.V2'
    New-Item -ItemType Directory -Path $ours | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $ours 'inner') -Target $target | Out-Null
    Remove-Item -LiteralPath $ours -Recurse -Force
    Write-Result -Ok ((Test-Path -LiteralPath (Join-Path $target 'precious.txt')) -and -not (Test-Path -LiteralPath $ours)) `
        -Name "pwsh $($PSVersionTable.PSVersion): Remove-Item -Recurse on a directory holding a junction leaves the target's files" `
        -Detail "target file present=$(Test-Path -LiteralPath (Join-Path $target 'precious.txt')); directory gone=$(-not (Test-Path -LiteralPath $ours))"

    $target = New-PreciousTarget 'mvp-self'
    $link = Join-Path $junctionRoot 'ControlServer.V2.previous'
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    Write-Result -Ok (Test-ParallelInstanceReparsePoint -Path $link) -Name 'Test-ParallelInstanceReparsePoint sees a junction' -Detail 'returned false'
    $plain = Join-Path $junctionRoot 'plain'
    New-Item -ItemType Directory -Path $plain | Out-Null
    Write-Result -Ok (-not (Test-ParallelInstanceReparsePoint -Path $plain)) -Name '... and not a plain directory' -Detail 'returned true'
    Write-Result -Ok (-not (Test-ParallelInstanceReparsePoint -Path (Join-Path $junctionRoot 'absent'))) -Name '... and not a path that does not exist' -Detail 'returned true'

    $message = $null
    try { Remove-ParallelInstanceDirectory -Path $link } catch { $message = $_.Exception.Message }
    Write-Result -Ok ($null -ne $message -and $message.Contains('junction or symbolic link') -and (Test-Path -LiteralPath $link) -and (Test-Path -LiteralPath (Join-Path $target 'precious.txt'))) `
        -Name 'Remove-ParallelInstanceDirectory refuses a junction and removes nothing' -Detail "threw: $message"

    # Dangling: the target is gone, the link is not. Test-Path says the link does not exist; the
    # probe must still see it (it reads the link, not the target).
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Result -Ok (Test-ParallelInstanceReparsePoint -Path $link) -Name 'Test-ParallelInstanceReparsePoint sees a dangling junction' -Detail 'returned false'

    # A plain directory in a temporary location is not ours: refused by the path layers, which
    # shows those run after the link check and are not skipped by it.
    $message = $null
    try { Remove-ParallelInstanceDirectory -Path $plain } catch { $message = $_.Exception.Message }
    Write-Result -Ok ($null -ne $message -and $message.Contains('is not directly under one of') -and (Test-Path -LiteralPath $plain)) `
        -Name 'Remove-ParallelInstanceDirectory refuses a plain directory outside the allowlist' -Detail "threw: $message"
} finally {
    Get-ChildItem -LiteralPath $junctionRoot -Force -Attributes ReparsePoint -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { [IO.Directory]::Delete($_.FullName) }
    [IO.Directory]::Delete($junctionRoot, $true)
}

<#
    A REGRESSION GUARD, NOT A PROOF. What keeps an uninstall or an install from deleting outside
    this instance is built into the code, not into this scan:
      * the path allowlist (canonical, directly under one of three roots, V2-marked) plus the
        production denylist, re-checked immediately before every single deletion
        (Remove-ParallelInstanceDirectory, Invoke-ParallelRemovalSequence);
      * the link refusal (a path that is itself a junction or symbolic link is not deleted);
      * the service step's positive confirmation (Invoke-ParallelProductUninstaller) and the
        abort on any failure there;
      * the secrets file deleted only at the layout's path, or beside the installer when no
        definition was accepted (Remove-ParallelInstanceDeploymentConfig).
    This scan exists so that the ordinary ways of writing a delete -- the ones someone might
    actually type while changing these files -- cannot slip in past those functions unnoticed.

    What it looks at: deleting commands however named (aliases, module-qualified), commands that
    run something else (cmd, robocopy, Invoke-Expression, Start-Process, pwsh ...), commands named
    by anything but a bare word, '.Delete(' member calls, and 'ForEach-Object Delete'. The same
    function and the same rules for the two scripts and the two modules (S1 re-review, round 4:
    the module side had been checked by a weaker rule, which was a real defect). Allowances are
    exact: a deleting command only inside the two delete functions, a dynamic command only as the
    exact text inside the exact function listed.

    Known ways past it, on purpose not chased (a blacklist is never finished -- round 4 decided):
      * aliases and function definitions (Set-Alias, New-Alias, Set-Item function:);
      * .NET, COM and CIM APIs other than '.Delete(' (VB DeleteDirectory, FSO DeleteFolder,
        Invoke-CimMethod, [Diagnostics.Process]::Start, [powershell]::new());
      * moving, emptying or re-permissioning instead of deleting (Move-Item is used legitimately);
      * anything defined in a module this scan does not read.
#>
function Find-DeletionBypass {
    param([string] $Source, [string[]] $AllowedDynamic = @(), [string[]] $AllowedDeletionOwners = @())
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Source, [ref]$null, [ref]$null)
    $deleting = @('Remove-Item', 'rm', 'del', 'rd', 'rmdir', 'ri', 'erase', 'Clear-RecycleBin')
    $running = @('cmd', 'cmd.exe', 'robocopy', 'robocopy.exe', 'Invoke-Expression', 'iex', 'Start-Process', 'saps', 'start',
        'pwsh', 'pwsh.exe', 'powershell', 'powershell.exe', 'Invoke-Command', 'icm', 'Start-Job', 'sajb', 'Start-ThreadJob')
    $findings = [System.Collections.Generic.List[string]]::new()
    $ownerOf = {
        param($node)
        $o = $node.Parent
        while ($o -and $o -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $o = $o.Parent }
        $o ? $o.Name : '(top level)'
    }
    foreach ($command in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        $owner = & $ownerOf $command
        $head = $command.CommandElements[0]
        $bare = $head -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $head.StringConstantType -eq 'BareWord'
        if (-not $bare -or $command.InvocationOperator -eq 'Dot') {
            if ($AllowedDynamic -cnotcontains "$owner::$($head.Extent.Text)") { $findings.Add("[$owner] dynamic command: $($command.Extent.Text.Split("`n")[0])") }
            continue
        }
        $name = ($head.Value -split '\\')[-1]
        if ($deleting -contains $name) {
            if ($AllowedDeletionOwners -cnotcontains $owner) { $findings.Add("[$owner] deleting command: $($command.Extent.Text.Split("`n")[0])") }
            continue
        }
        if ($running -contains $name) { $findings.Add("[$owner] code-running command: $($command.Extent.Text.Split("`n")[0])"); continue }
        # 'Get-ChildItem ... | ForEach-Object Delete' -- the idiomatic one, and the round-4 review's
        # example of a delete the scan did not see while every other test stayed green.
        if ($name -in @('ForEach-Object', '%', 'foreach')) {
            $deleteArgs = @($command.CommandElements | Select-Object -Skip 1 |
                    Where-Object { $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $_.Value -ieq 'Delete' })
            if ($deleteArgs.Count -gt 0) { $findings.Add("[$owner] ForEach-Object Delete: $($command.Extent.Text.Split("`n")[0])") }
        }
    }
    foreach ($member in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.MemberExpressionAst] }, $true)) {
        if ($member.Member.Extent.Text -in @('Delete', 'Create', 'InvokeScript', 'NewScriptBlock')) {
            $findings.Add("[$(& $ownerOf $member)] member call: $($member.Extent.Text)")
        }
    }
    return $findings
}

# Each file, with exactly the allowances it needs and no others.
$deleteFunctions = @('Remove-ParallelInstanceDirectory', 'Remove-ParallelInstanceDeploymentConfig')
$scanTargets = [ordered]@{
    'Install-ParallelInstanceLocal.ps1'   = @{ Dynamic = @(
            "Invoke-ProductInstaller::(Join-Path `$scripts 'Update-ControlServerLocal.ps1')",
            "Invoke-ProductInstaller::(Join-Path `$scripts 'Install-ControlServerLocal.ps1')"); Owners = @(); Expected = 2 }
    'Uninstall-ParallelInstanceLocal.ps1' = @{ Dynamic = @(); Owners = @(); Expected = 0 }
    'ParallelInstance.psm1'               = @{ Dynamic = @(
            'Invoke-ParallelRemovalSequence::$Actions.Service', 'Invoke-ParallelRemovalSequence::$Actions[$kind]',
            'Invoke-ParallelRemovalSequence::$Actions.Process', 'Invoke-ParallelRemovalSequence::$Actions.DirectoryPattern',
            'Invoke-ParallelRemovalSequence::$Actions.ReparsePoint', 'Invoke-ParallelRemovalSequence::$Actions.Directory',
            'Invoke-ParallelRemovalSequence::$result'); Owners = $deleteFunctions; Expected = 12 }
    'ParallelHost.psm1'                   = @{ Dynamic = @('Invoke-ParallelProductUninstaller::$UninstallerPath',
            # control-server#454: the write seam and the injected machine actions.
            'Update-ParallelInstanceConfigurationFile::$Writer',
            'Invoke-ParallelProductUpgrade::$Actions.StopService', 'Invoke-ParallelProductUpgrade::$Actions.InvokeUpdate',
            'Invoke-ParallelProductUpgrade::$Actions.ServiceStatus', 'Invoke-ParallelInstanceConfigurationStep::$Actions.GetEnvironment',
            'Invoke-ParallelInstanceConfigurationStep::$Actions.SetEnvironment', 'Invoke-ParallelInstanceConfigurationStep::$Actions.RestartService',
            # control-server#472: the generalised write seam, the read-only query helper and the gate change's injected actions.
            'Set-ParallelInstanceConfigurationFlag::$Writer', 'Get-ParallelJourneyDispatchState::$read',
            'Invoke-ParallelDispatchGateChange::$Actions.ServiceStatus', 'Invoke-ParallelDispatchGateChange::$Actions.ReadState',
            'Invoke-ParallelDispatchGateChange::$Actions.StopService', 'Invoke-ParallelDispatchGateChange::$Actions.StartService',
            'Invoke-ParallelDispatchGateChange::$Actions.ProcessStartTimeUtc', 'Invoke-ParallelDispatchGateChange::$startAgain'); Owners = @(); Expected = 25 }
    'Set-ParallelDispatchGateLocal.ps1'   = @{ Dynamic = @(); Owners = @(); Expected = 0 }
}
foreach ($file in $scanTargets.Keys) {
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $file))
    $allow = $scanTargets[$file]
    $found = @(Find-DeletionBypass -Source $source -AllowedDynamic $allow.Dynamic -AllowedDeletionOwners $allow.Owners)
    Write-Result -Ok ($found.Count -eq 0) -Name "$file deletes nothing outside the two delete functions" -Detail ($found -join ' | ')
    # The allowances are real and no wider than what is there: without them, the scan finds
    # exactly the allowed commands (and for the module, the two Remove-Item in the delete functions).
    $withoutAllowance = @(Find-DeletionBypass -Source $source)
    Write-Result -Ok ($withoutAllowance.Count -eq $allow.Expected) `
        -Name "$file without its allowances shows exactly the $($allow.Expected) allowed command(s)" `
        -Detail ("found $($withoutAllowance.Count): " + ($withoutAllowance -join ' | '))
}
$moduleDeletes = @(Find-DeletionBypass -Source ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ParallelInstance.psm1'))) |
        Where-Object { $_ -like '*deleting command*' })
$inDeleteFunctions = @($moduleDeletes | Where-Object { $_ -like '`[Remove-ParallelInstanceDirectory`]*' -or $_ -like '`[Remove-ParallelInstanceDeploymentConfig`]*' })
Write-Result -Ok ($moduleDeletes.Count -eq 2 -and $inDeleteFunctions.Count -eq 2) `
    -Name 'the module deletes only in Remove-ParallelInstanceDirectory and Remove-ParallelInstanceDeploymentConfig' `
    -Detail ("deletions: " + ($moduleDeletes -join ' | '))

# One synthetic snippet per common way of writing a delete. Each must be found -- a scan that
# finds nothing in the real files proves nothing until it is shown to find something.
$deletionBypasses = [ordered]@{
    'Remove-Item with -Recurse on the config path' = 'Remove-Item -LiteralPath $DeploymentConfigPath -Recurse -Force'
    'module-qualified Remove-Item'                  = 'Microsoft.PowerShell.Management\Remove-Item -LiteralPath $p -Recurse'
    'Remove-Item through a variable'                = "`$c = 'Remove-Item'; & `$c -LiteralPath `$p -Recurse"
    'Remove-Item through Get-Command'               = '& (Get-Command Remove-Item) -LiteralPath $p -Recurse'
    'rm alias'                                      = 'rm $p -r -fo'
    '[IO.Directory]::Delete'                        = '[IO.Directory]::Delete($p, $true)'
    'DirectoryInfo.Delete'                          = '([IO.DirectoryInfo]$p).Delete($true)'
    '(Get-Item).Delete'                             = '(Get-Item $p).Delete($true)'
    'Get-ChildItem | ForEach-Object Delete'         = 'Get-ChildItem -LiteralPath $p -Directory | ForEach-Object Delete'
    'ForEach-Object -MemberName Delete'             = 'Get-Item $p | ForEach-Object -MemberName Delete -ArgumentList $true'
    '% Delete'                                      = 'Get-Item $p | % Delete'
    'cmd /c rmdir'                                  = 'cmd /c rmdir /s /q $p'
    'robocopy /MIR from an empty directory'         = 'robocopy $empty $p /MIR'
    'Invoke-Expression'                             = 'Invoke-Expression "Remove-Item $p -Recurse"'
    'Remove-Item inside a function not allowed to'  = 'function Clear-Something { Remove-Item -LiteralPath $p -Recurse }'
}
foreach ($name in $deletionBypasses.Keys) {
    $hits = @(Find-DeletionBypass -Source $deletionBypasses[$name] -AllowedDeletionOwners $deleteFunctions)
    Write-Result -Ok ($hits.Count -gt 0) -Name "deletion scanner finds: $name" -Detail "snippet: $($deletionBypasses[$name])"
}
$quiet = @(Find-DeletionBypass -Source 'Get-ChildItem $p | ForEach-Object { "$($_.Name)" }; function Remove-ParallelInstanceDirectory { Remove-Item -LiteralPath $p -Recurse }' -AllowedDeletionOwners $deleteFunctions)
Write-Result -Ok ($quiet.Count -eq 0) -Name 'deletion scanner is quiet on a plain ForEach-Object and on Remove-Item inside a delete function' `
    -Detail ($quiet -join ' | ')

Write-Host ''
Write-Host 'The deployment config: one path, a plain file (S1 re-review, round 3)' -ForegroundColor Cyan

# The installer's structure. deploy-config.json must be removed on every way out, so: the outer
# try opens before the first check (Assert-Administrator), the config-path refusal is inside it,
# and its finally hands the file to Remove-ParallelInstanceDeploymentConfig with the installer's
# own directory as the fallback, throwing SECRET_FILE_LEFT_BEHIND when an otherwise successful
# install leaves it. The end-to-end runs below exercise one early exit for real; this pins the
# shape that makes every other one reach the same finally.
$installerText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Install-ParallelInstanceLocal.ps1')).Replace("`r`n", "`n")
$marks = [ordered]@{
    'outer try'        = "`$installSucceeded = `$false`ntry {`n"
    'first check'      = "`n    Assert-Administrator`n"
    'path check call'  = '$configRefusal = Test-ParallelInstanceDeploymentConfigPath -Path $DeploymentConfigPath -Layout $layout'
    'path check throw' = 'if ($configRefusal) { throw "-DeploymentConfigPath $configRefusal." }'
    'outer finally'    = "`n    `$installSucceeded = `$true`n} finally {`n"
    'cleanup call'     = 'Remove-ParallelInstanceDeploymentConfig -Path $DeploymentConfigPath -Layout $layout -FallbackDirectory $PSScriptRoot'
    'loud on success'  = 'if ($installSucceeded) { throw $message }'
}
$at = [ordered]@{}
foreach ($k in $marks.Keys) { $at[$k] = $installerText.IndexOf($marks[$k], [StringComparison]::Ordinal) }
$positions = @($at.Values)
$inOrder = @($positions | Where-Object { $_ -lt 0 }).Count -eq 0 -and
    @(for ($i = 1; $i -lt $positions.Count; $i++) { if ($positions[$i] -le $positions[$i - 1]) { $i } }).Count -eq 0
Write-Result -Ok $inOrder `
    -Name 'the installer: outer try before the first check, path refusal inside it, cleanup in its finally' `
    -Detail (($at.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')

$configRoot = Join-Path ([IO.Path]::GetTempPath()) "cs262-config-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $configRoot | Out-Null
try {
    # --- Test-ParallelInstanceDeploymentConfigPath: what the installer accepts up front ---
    $fakeLayout = [pscustomobject]@{ DeploymentConfigPath = (Join-Path $configRoot 'deploy-config.json') }
    Set-Content -LiteralPath $fakeLayout.DeploymentConfigPath -Value '{}'
    Write-Result -Ok ($null -eq (Test-ParallelInstanceDeploymentConfigPath -Path $fakeLayout.DeploymentConfigPath -Layout $fakeLayout)) `
        -Name "the layout's own config path, a plain file, is accepted" -Detail 'refused'
    $elsewhere = Join-Path $configRoot 'other.json'
    Set-Content -LiteralPath $elsewhere -Value '{}'
    $why = Test-ParallelInstanceDeploymentConfigPath -Path $elsewhere -Layout $fakeLayout
    Write-Result -Ok ($null -ne $why -and $why.Contains('the only accepted path is')) -Name 'any other config path is refused' -Detail "got: $why"
    $why = Test-ParallelInstanceDeploymentConfigPath -Path 'C:\Program Files\8005 AGV\ControlServer' -Layout $fakeLayout
    Write-Result -Ok ($null -ne $why -and $why.Contains('the only accepted path is')) -Name "the MVP install root as the config path is refused" -Detail "got: $why"

    # Not a plain file, or not there at all: refused up front.
    $dirAtPath = [pscustomobject]@{ DeploymentConfigPath = (Join-Path $configRoot 'dir-at-path\deploy-config.json') }
    New-Item -ItemType Directory -Path $dirAtPath.DeploymentConfigPath | Out-Null
    $why = Test-ParallelInstanceDeploymentConfigPath -Path $dirAtPath.DeploymentConfigPath -Layout $dirAtPath
    Write-Result -Ok ($null -ne $why -and $why.Contains('not an existing file')) -Name 'a directory at the config path is refused up front' -Detail "got: $why"
    $absent = [pscustomobject]@{ DeploymentConfigPath = (Join-Path $configRoot 'absent\deploy-config.json') }
    $why = Test-ParallelInstanceDeploymentConfigPath -Path $absent.DeploymentConfigPath -Layout $absent
    Write-Result -Ok ($null -ne $why -and $why.Contains('not an existing file')) -Name 'a config path with no file is refused up front' -Detail "got: $why"

    # --- Remove-ParallelInstanceDeploymentConfig, in each state the installer's finally can be in ---
    # Each case gets its own directory and files, so one case going wrong under a mutation cannot
    # make the next one fail for a reason of its own.
    function New-ConfigCase([string] $Name) {
        $dir = Join-Path $configRoot "case-$Name"
        New-Item -ItemType Directory -Path $dir | Out-Null
        $file = Join-Path $dir 'deploy-config.json'
        $other = Join-Path $dir 'other.json'
        Set-Content -LiteralPath $file -Value '{}'
        Set-Content -LiteralPath $other -Value '{}'
        return [pscustomobject]@{ Dir = $dir; File = $file; Other = $other; Layout = [pscustomobject]@{ DeploymentConfigPath = $file } }
    }

    # Exit 1: the main flow threw after the definition and the path were accepted.
    $c = New-ConfigCase 'accepted'
    $r = Remove-ParallelInstanceDeploymentConfig -Path $c.File -Layout $c.Layout -FallbackDirectory $c.Dir
    Write-Result -Ok ($null -eq $r -and -not (Test-Path -LiteralPath $c.File) -and (Test-Path -LiteralPath $c.Other)) `
        -Name 'exit after acceptance: the layout path is deleted, nothing else' -Detail "returned: $r"

    # Exit 2: the path itself was refused. Not deleted -- reported.
    $c = New-ConfigCase 'path-refused'
    $r = Remove-ParallelInstanceDeploymentConfig -Path $c.Other -Layout $c.Layout -FallbackDirectory $c.Dir
    Write-Result -Ok ($null -ne $r -and $r.Contains("not the layout's config path") -and (Test-Path -LiteralPath $c.Other)) `
        -Name 'exit on a refused path: the file is kept and reported' -Detail "returned: $r"

    # Exit 3: the definition was refused, so there is no layout. The installer's own directory decides.
    $c = New-ConfigCase 'definition-refused'
    $r = Remove-ParallelInstanceDeploymentConfig -Path $c.File -Layout $null -FallbackDirectory $c.Dir
    Write-Result -Ok ($null -eq $r -and -not (Test-Path -LiteralPath $c.File)) `
        -Name "exit on a refused definition: deploy-config.json beside the installer is deleted" -Detail "returned: $r"
    $c = New-ConfigCase 'definition-refused-other'
    $r = Remove-ParallelInstanceDeploymentConfig -Path $c.Other -Layout $null -FallbackDirectory $c.Dir
    Write-Result -Ok ($null -ne $r -and $r.Contains('without an accepted definition') -and (Test-Path -LiteralPath $c.Other)) `
        -Name 'exit on a refused definition: any other file is kept and reported' -Detail "returned: $r"

    # Nothing there: nothing to report.
    $c = New-ConfigCase 'absent'
    [IO.File]::Delete($c.File)
    Write-Result -Ok ($null -eq (Remove-ParallelInstanceDeploymentConfig -Path $c.File -Layout $null -FallbackDirectory $c.Dir)) `
        -Name 'no config file left: nothing to report' -Detail 'reported a residue for an absent file'

    # Not a plain file: a directory, and a junction, where the file should be. Both kept and reported.
    $c = New-ConfigCase 'directory'
    [IO.File]::Delete($c.File)
    New-Item -ItemType Directory -Path $c.File | Out-Null
    Set-Content -LiteralPath (Join-Path $c.File 'inner.txt') -Value 'x'
    $r = Remove-ParallelInstanceDeploymentConfig -Path $c.File -Layout $null -FallbackDirectory $c.Dir
    Write-Result -Ok ($null -ne $r -and $r.Contains('not a plain file') -and (Test-Path -LiteralPath (Join-Path $c.File 'inner.txt'))) `
        -Name 'a directory at the config path is kept and reported' -Detail "returned: $r"
    $c = New-ConfigCase 'junction'
    [IO.File]::Delete($c.File)
    $junctionTarget = Join-Path $c.Dir 'target'
    New-Item -ItemType Directory -Path $junctionTarget | Out-Null
    Set-Content -LiteralPath (Join-Path $junctionTarget 'precious.txt') -Value 'x'
    New-Item -ItemType Junction -Path $c.File -Target $junctionTarget | Out-Null
    $r = Remove-ParallelInstanceDeploymentConfig -Path $c.File -Layout $null -FallbackDirectory $c.Dir
    Write-Result -Ok ($null -ne $r -and $r.Contains('symbolic link or junction') -and (Test-Path -LiteralPath (Join-Path $junctionTarget 'precious.txt'))) `
        -Name 'a junction at the config path is kept and reported' -Detail "returned: $r"
    if (Test-ParallelInstanceReparsePoint -Path $c.File) { [IO.Directory]::Delete($c.File) }
} finally {
    Remove-Item -LiteralPath $configRoot -Recurse -Force -ErrorAction SilentlyContinue
}

<#
    End to end: run the real installer from a temporary copy and let it exit at its first check.
    Unelevated it stops at Assert-Administrator; elevated, at reading a definition that does not
    exist. Either way it leaves before any layout exists -- the case where the file used to stay
    behind with its secrets. Nothing else on the machine is touched: both exits come before the
    installer does anything.
#>
$e2eRoot = Join-Path ([IO.Path]::GetTempPath()) "cs262-install-exit-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $e2eRoot | Out-Null
try {
    foreach ($file in @('Install-ParallelInstanceLocal.ps1', 'ParallelInstance.psm1', 'ParallelHost.psm1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $e2eRoot
    }
    function Invoke-InstallerEarlyExit([string] $ConfigPath) {
        $out = @(& pwsh -NoProfile -File (Join-Path $e2eRoot 'Install-ParallelInstanceLocal.ps1') `
                -PackageZip 'x.zip' -ExpectedSha256 'x' -DeploymentConfigPath $ConfigPath -FakeMesIngestZip 'y.zip' `
                -InstanceDefinitionPath (Join-Path $e2eRoot 'no-such-instance.json') 2>&1 | ForEach-Object { "$_" })
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $out }
    }

    $besideInstaller = Join-Path $e2eRoot 'deploy-config.json'
    Set-Content -LiteralPath $besideInstaller -Value '{"riotCallApiKey":"selftest","mesIngestSharedSecret":"selftest"}'
    $run = Invoke-InstallerEarlyExit $besideInstaller
    $leftBehind = @($run.Output | Where-Object { $_ -like '*SECRET_FILE_LEFT_BEHIND*' })
    $exitedEarly = @($run.Output | Where-Object { $_ -like '*must run elevated*' -or $_ -like '*no-such-instance.json*' })
    Write-Result -Ok ($run.Exit -ne 0 -and $exitedEarly.Count -gt 0 -and -not (Test-Path -LiteralPath $besideInstaller) -and $leftBehind.Count -eq 0) `
        -Name 'installer exits at its first check: the config beside it is deleted' `
        -Detail ("exit=$($run.Exit) early=$($exitedEarly.Count) file still there=$(Test-Path -LiteralPath $besideInstaller); output: " + ($run.Output -join ' / '))

    $elsewhereDir = Join-Path $e2eRoot 'elsewhere'
    New-Item -ItemType Directory -Path $elsewhereDir | Out-Null
    $elsewhereFile = Join-Path $elsewhereDir 'deploy-config.json'
    Set-Content -LiteralPath $elsewhereFile -Value '{"riotCallApiKey":"selftest","mesIngestSharedSecret":"selftest"}'
    $run = Invoke-InstallerEarlyExit $elsewhereFile
    $leftBehind = @($run.Output | Where-Object { $_ -like '*SECRET_FILE_LEFT_BEHIND*' -and $_.Contains($elsewhereFile) })
    Write-Result -Ok ($run.Exit -ne 0 -and (Test-Path -LiteralPath $elsewhereFile) -and $leftBehind.Count -eq 1) `
        -Name 'installer exits early with a config it may not delete: SECRET_FILE_LEFT_BEHIND names it' `
        -Detail ("exit=$($run.Exit) kept=$(Test-Path -LiteralPath $elsewhereFile); output: " + ($run.Output -join ' / '))
} finally {
    Remove-Item -LiteralPath $e2eRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Installer and uninstaller take paths and names only from the layout (re-review, M4)' -ForegroundColor Cyan

<#
    What this proves and what it does not. It proves the two scripts contain no index of the form
    [...]['installRoot'] (or any other path/name key) and no literal drive path -- so a new thing
    an install creates must come through Get-ParallelInstanceLayout, which the footprint is
    derived from, or show up here. It does not see a path built by Join-Path under a layout
    directory (results\, logs\); those are inside a directory the uninstaller removes whole.
#>
$forbiddenIndexes = @('installRoot', 'dataRoot', 'backupRoot', 'packageRoot', 'opsRoot', 'stagingRoot', 'serviceName', 'taskName', 'seedPath')
function Find-LayoutBypass {
    param([string] $Source)
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Source, [ref]$null, [ref]$null)
    $hits = @()
    foreach ($node in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.IndexExpressionAst] }, $true)) {
        if ($node.Index -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            $forbiddenIndexes -contains $node.Index.Value) {
            $hits += "line $($node.Extent.StartLineNumber): $($node.Extent.Text)"
        }
    }
    foreach ($node in $ast.FindAll({ param($n)
                ($n -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                 $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) }, $true)) {
        if ($node.Value -match '(^|[^A-Za-z])[A-Za-z]:[\\/]') {
            $hits += "line $($node.Extent.StartLineNumber): literal path $($node.Extent.Text)"
        }
    }
    return $hits
}
foreach ($script in @('Install-ParallelInstanceLocal.ps1', 'Uninstall-ParallelInstanceLocal.ps1')) {
    $source = Get-Content -LiteralPath (Join-Path $PSScriptRoot $script) -Raw
    $hits = @(Find-LayoutBypass $source)
    Write-Result -Ok ($hits.Count -eq 0) -Name "$script reads no path or name except through the layout" -Detail ($hits -join ' / ')
}
# ... and the scan must be able to say something. Plant one of each kind in a copy of the installer.
$planted = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Install-ParallelInstanceLocal.ps1') -Raw) +
    "`n`$bypassA = `$definition['installRoot']`n`$bypassB = 'D:\zhengyushao\somewhere'`n"
$plantedHits = @(Find-LayoutBypass $planted)
Write-Result -Ok ($plantedHits.Count -eq 2) -Name 'the bypass scan reports both planted bypasses (it is not blind)' `
    -Detail ("got $($plantedHits.Count): " + ($plantedHits -join ' / '))

Write-Host ''
Write-Host 'Whitelist against the C# options (a rename there must fail here)' -ForegroundColor Cyan

# Each whitelisted key is copied into appsettings.Production.json and bound by name. A key
# whose property was renamed in C# binds to nothing -- silently -- so the whitelist is checked
# against the properties themselves rather than trusted.
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function Get-OptionProperty {
    param([string] $RelativePath, [string] $ClassName)
    $source = Get-Content -LiteralPath (Join-Path $repoRoot $RelativePath) -Raw
    $start = $source.IndexOf("class $ClassName")
    if ($start -lt 0) { throw "class $ClassName not found in $RelativePath" }
    $next = $source.IndexOf("`nclass ", $start + 1)
    $nextPublic = $source.IndexOf("`npublic ", $start + 1)
    $end = @($next, $nextPublic, $source.Length) | Where-Object { $_ -gt $start } | Sort-Object | Select-Object -First 1
    return @([regex]::Matches($source.Substring($start, $end - $start),
            'public\s+[\w<>\[\]?,\s]+?\s+(\w+)\s*\{\s*get;\s*(?:set|init);') | ForEach-Object { $_.Groups[1].Value })
}
$allowedKeys = Get-ParallelInstanceAllowedKey
$optionSources = @(
    @{ Section = 'journeyRuntime'; Path = 'src/ControlServer.Host/Runtime/JourneyRuntimeOptions.cs'; Class = 'JourneyRuntimeOptions'; Excluded = @('Fleet') }
    @{ Section = 'routeGraph'; Path = 'src/ControlServer.Host/Runtime/RouteGraph/RouteGraphOptions.cs'; Class = 'RouteGraphOptions'; Excluded = @() }
    @{ Section = 'riotCreateDispatch'; Path = 'src/ControlServer.Host/Runtime/RiotCreateDispatchOptions.cs'; Class = 'RiotCreateDispatchOptions'; Excluded = @() }
    @{ Section = 'riotForeignOrderCancel'; Path = 'src/ControlServer.Host/Runtime/ForeignOrders/RiotForeignOrderCancelOptions.cs'; Class = 'RiotForeignOrderCancelOptions'; Excluded = @() }
    # control-server#454. The credential variable's NAME is excluded: the overlay writes this
    # instance's own name, so a definition cannot point the parallel service at the MVP's variable.
    @{ Section = 'vehicleFaultRecovery'; Path = 'src/ControlServer.Host/Runtime/VehicleFaultRecoveryOptions.cs'; Class = 'VehicleFaultRecoveryOptions'; Excluded = @('CredentialEnvironmentVariable') }
    @{ Section = 'fieldOperatorRoles'; Path = 'src/ControlServer.Host/Runtime/Charging/FieldOperatorRoleRoster.cs'; Class = 'FieldOperatorRoleOptions'; Excluded = @() }
)
foreach ($source in $optionSources) {
    $properties = Get-OptionProperty -RelativePath $source.Path -ClassName $source.Class
    # Without this an absent section has no keys, no orphans, and passes vacuously.
    Write-Result -Ok ($allowedKeys.Contains($source.Section) -and @($allowedKeys[$source.Section]).Count -gt 0) `
        -Name "the whitelist has a $($source.Section) section" -Detail 'missing or empty'
    $orphans = @($allowedKeys[$source.Section] | Where-Object { $key = $_; -not ($properties | Where-Object { $_ -ieq $key }) })
    Write-Result -Ok ($orphans.Count -eq 0) -Name "every $($source.Section) key names a $($source.Class) property" `
        -Detail ("no such property: " + ($orphans -join ', '))
    # The reverse direction is informational except for the deliberate exclusions: a new C#
    # option the whitelist does not carry is refused at deploy time, which is fail-closed.
    foreach ($excluded in $source.Excluded) {
        $inCSharp = [bool]($properties | Where-Object { $_ -ceq $excluded })
        $inWhitelist = [bool]($allowedKeys[$source.Section] | Where-Object { $_ -ieq $excluded })
        Write-Result -Ok ($inCSharp -and -not $inWhitelist) `
            -Name "$($source.Class).$excluded exists and is deliberately NOT whitelisted" `
            -Detail "inCSharp=$inCSharp inWhitelist=$inWhitelist"
    }
}

Write-Host ''
Write-Host 'Footprint (what install creates and uninstall removes)' -ForegroundColor Cyan

$footprint = @(Get-ParallelInstanceFootprint -Definition $baseline)
$names = Get-ParallelInstanceName
$directories = @($footprint | Where-Object Kind -eq 'Directory' | ForEach-Object Name)
$collisions = @($directories | Where-Object { Test-ParallelInstancePathIsProduction -Path $_ })
Write-Result -Ok ($collisions.Count -eq 0) -Name 'no footprint directory is, contains or sits in a production path' `
    -Detail ("collides: " + ($collisions -join ', '))
# The footprint against the layout, not against a list written here: every directory the layout
# names is in the footprint, and every file the layout names lies inside one of them. Together
# with the bypass scan above (the installer takes nothing except through the layout) that is what
# "the footprint names every directory an install creates" now rests on -- re-review M4 pointed
# out that the earlier version compared the footprint with a hand-written list instead.
$layout = Get-ParallelInstanceLayout -Definition $baseline
$layoutDirectories = @('InstallRoot', 'DataRoot', 'BackupRoot', 'PackageRoot', 'PreviousRoot', 'OpsRoot', 'StagingRoot', 'FakeInstallRoot' |
        ForEach-Object { $layout.$_ })
$missing = @($layoutDirectories | Where-Object { $directories -notcontains $_ })
Write-Result -Ok ($missing.Count -eq 0 -and $directories.Count -eq $layoutDirectories.Count) `
    -Name 'the footprint lists exactly the layout''s directories' `
    -Detail ("missing: " + ($missing -join ', ') + "; footprint has $($directories.Count), layout $($layoutDirectories.Count)")
$layoutFiles = @('ResultRoot', 'FakeLogPath', 'InstalledDefinitionPath', 'SeedPath', 'FieldOperatorRosterPath' | ForEach-Object { $layout.$_ })
$outside = @($layoutFiles | Where-Object { -not $_.StartsWith("$($layout.OpsRoot)\", [StringComparison]::OrdinalIgnoreCase) })
Write-Result -Ok ($outside.Count -eq 0) -Name 'every other layout path lies inside opsRoot, so it goes with it' `
    -Detail ("outside: " + ($outside -join ', '))
$patterns = @($footprint | Where-Object Kind -eq 'DirectoryPattern' | ForEach-Object Name)
Write-Result -Ok ($patterns.Count -eq 2 -and @($patterns | Where-Object { (Split-Path -Leaf $_) -like 'ControlServer.V2.*-`*' }).Count -eq 2) `
    -Name 'the leftover staging globs are in the footprint and carry the instance prefix' -Detail ("got: " + ($patterns -join ', '))
$notOwned = @($directories | Where-Object { Test-ParallelInstanceOwnedPath -Path $_ })
Write-Result -Ok ($notOwned.Count -eq 0) -Name 'every footprint directory passes the allowlist' -Detail ("refused: " + ($notOwned -join ', '))
$rules = @($footprint | Where-Object Kind -eq 'FirewallRule' | ForEach-Object Name)
Write-Result -Ok ($rules.Count -eq 2 -and -not ($rules | Where-Object { $_ -in @('8005 AGV ControlServer 58005', '8005 AGV ControlServer 58007') })) `
    -Name 'the footprint firewall rules are the V2 ones, never the MVP rules' -Detail ("got: " + ($rules -join ', '))
Write-Result -Ok ($names.CertificatePasswordVariable -cne $names.ProductionCertificatePasswordVariable) `
    -Name 'the certificate variable the upgrade deletes is not the MVP one' -Detail $names.CertificatePasswordVariable
$data = @($footprint | Where-Object Data | ForEach-Object Name)
Write-Result -Ok ($data -contains [string] $baseline['dataRoot'] -and $data -notcontains [string] $baseline['installRoot']) `
    -Name 'the data root is marked as data (kept unless asked) and the install root is not' -Detail ("data: " + ($data -join ', '))
$userKey = @($footprint | Where-Object { $_.Name -eq 'CONTROL_SERVER_RIOT_CALL_API_KEY' })
Write-Result -Ok ($userKey.Count -eq 0) -Name 'the shared RIoT key is not in the footprint (the MVP installer reads it)' -Detail 'listed'

Write-Host ''
Write-Host 'Staging cleanup glob (control-server#262 review, finding 1)' -ForegroundColor Cyan

# Both deployments keep their package roots in D:\zhengyushao. The old cleanup was
# '*.incoming-*' in the parent directory, which matched both instances' staging directories.
# Recreated in a temp directory: first the old glob, asserted to match both (the red side, so
# this case would notice if the fixture stopped reproducing the bug), then the fixed one from
# each side, asserted to match only its own.
$sandbox = Join-Path ([IO.Path]::GetTempPath()) "incoming-glob-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $sandbox | Out-Null
try {
    $mvpRoot = Join-Path $sandbox 'ControlServer'
    $v2Root = Join-Path $sandbox (Split-Path -Leaf ([string] $baseline['packageRoot']))
    New-Item -ItemType Directory -Path "$mvpRoot.incoming-20261008-101010", "$v2Root.incoming-20261008-101011" | Out-Null
    $matchOf = { param($filter) @(Get-ChildItem -Path $sandbox -Directory -Filter $filter | ForEach-Object Name | Sort-Object) }

    $old = @(& $matchOf '*.incoming-*')
    Write-Result -Ok ($old.Count -eq 2) -Name "old glob '*.incoming-*' matches both instances (reproduces the bug)" -Detail ("matched: " + ($old -join ', '))
    $fromV2 = @(& $matchOf "$(Split-Path -Leaf $v2Root).incoming-*")
    Write-Result -Ok ($fromV2.Count -eq 1 -and $fromV2[0] -like 'ControlServer.V2.incoming-*') `
        -Name 'the fixed V2 glob matches only the V2 staging directory' -Detail ("matched: " + ($fromV2 -join ', '))
    $fromMvp = @(& $matchOf "$(Split-Path -Leaf $mvpRoot).incoming-*")
    Write-Result -Ok ($fromMvp.Count -eq 1 -and $fromMvp[0] -like 'ControlServer.incoming-*') `
        -Name 'the fixed MVP glob matches only the MVP staging directory' -Detail ("matched: " + ($fromMvp -join ', '))
} finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Overlay' -ForegroundColor Cyan

# The reason the overlay exists: .NET configuration merges per key, so the product installer
# writing JourneyRuntime = { enabled = false } leaves agvId and vehicleKey coming from the
# package's own appsettings.json, where they still name agv01.
$overlay = New-ParallelInstanceConfigurationOverlay -Definition $baseline
$merged = Merge-ConfigurationTree -Base ([ordered]@{
    JourneyRuntime = [ordered]@{ enabled = $false; agvId = '老厂前线新多仓位1'; pollInterval = '00:00:02' }
    Health = [ordered]@{ url = 'http://172.19.205.222:58107' }
}) -Overlay $overlay

Write-Result -Ok ($merged['JourneyRuntime']['agvId'] -eq '老厂前线新多仓位2') `
    -Name 'the overlay replaces the inherited agv01 identity' `
    -Detail "got '$($merged['JourneyRuntime']['agvId'])'"
Write-Result -Ok ($merged['JourneyRuntime']['enabled'] -eq $true) `
    -Name 'the overlay enables the journey runtime the installer left disabled' `
    -Detail "got '$($merged['JourneyRuntime']['enabled'])'"
Write-Result -Ok ($merged['Health']['url'] -eq 'http://172.19.205.222:58107') `
    -Name 'the merge keeps base keys the overlay does not mention' `
    -Detail "got '$($merged['Health']['url'])'"
Write-Result -Ok ($merged['RouteGraph']['enabled'] -eq $true -and $merged['RouteGraph']['mapId'] -eq 26) `
    -Name 'the overlay states the route graph explicitly' `
    -Detail "got enabled='$($merged['RouteGraph']['enabled'])' mapId='$($merged['RouteGraph']['mapId'])'"
Write-Result -Ok ($merged['MesIngest']['baseUrl'] -eq 'http://127.0.0.1:58188') `
    -Name 'the overlay points MesIngest at the fake catalog' `
    -Detail "got '$($merged['MesIngest']['baseUrl'])'"

Write-Host ''
Write-Host 'Station clearance exit through first install, upgrade and rollback (control-server#454)' -ForegroundColor Cyan

<#
    The two sections the clearance exit needs -- VehicleFaultRecovery (the Host entry and its
    credential) and FieldOperatorRoles (the roster and the onboard-entry declaration) -- used to be
    merged in by hand after an install. Install-ControlServerLocal.ps1 rewrites
    appsettings.Production.json and the service's Environment on a first install, so the hand-merged
    sections and the credential were lost without a word; the server then alarmed 2271/2272, left
    an uncharged vehicle on ORDER_HANG and offered no clearance entry.

    Each path below starts from the configuration and Environment that path really leaves behind
    and runs the steps the installer runs, in its order: take the credential (supplied by the
    control host, or carried over from the service), let the product script do its part, merge the
    overlay into the file, put the credential into the Environment, read the readiness back.

    Two layers per path. The first uses only the overlay and the merge, which existed before this
    ticket: it is red on the old code for a content reason (the sections are not there), not
    because a function is missing. The second runs the whole step.
#>
$credentialName = 'CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL'
$rosterPath = "$([string] $baseline['opsRoot'])\field-operator-roles.json"
$clearanceDefinition = Copy-Definition $baseline
$clearanceDefinition['vehicleFaultRecovery'] = [ordered]@{ enabled = $true }
$clearanceDefinition['fieldOperatorRoles'] = [ordered]@{ path = $rosterPath; onboardClearanceEntryDeclared = $true }

# What Install-ControlServerLocal.ps1 writes (its $configuration literal), and what it puts into the
# service's Environment when it creates the service.
$productConfiguration = [ordered]@{
    Health = [ordered]@{ url = 'http://172.19.205.222:58107' }
    ConnectionStrings = [ordered]@{ ControlServer = 'Data Source=C:\ProgramData\8005\ControlServer.V2\data\controlserver.db' }
    OnboardTransport = [ordered]@{ enabled = $true; listenAddress = '172.19.205.222'; port = 58105; credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL' }
    OnboardSafetyProjection = [ordered]@{ enabled = $true; credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL' }
    JourneyRuntime = [ordered]@{ enabled = $false }
}
$productEnvironment = @('DOTNET_ENVIRONMENT=Production', 'CONTROL_SERVER_RIOT_CALL_API_KEY=selftest-riot', 'CONTROL_SERVER_ONBOARD_CREDENTIAL=selftest-onboard')

function Find-CaseTwin {
    # Keys that differ only in case. .NET configuration reads them as one setting (and the JSON
    # provider refuses the file outright); a hashtable from ConvertFrom-Json -AsHashtable keeps both.
    param($Node, [string] $Path)
    if ($Node -isnot [System.Collections.IDictionary]) { return }
    $keys = @($Node.Keys | ForEach-Object { [string] $_ })
    $groups = $keys | Group-Object -Property { $_.ToLowerInvariant() } | Where-Object Count -gt 1
    foreach ($group in $groups) { "$Path{$($group.Group -join '|')}" }
    foreach ($key in $keys) { Find-CaseTwin -Node $Node[$key] -Path "$Path$key." }
}

function Invoke-ClearancePath {
    <#
        The installer's sequence for one path, against a temporary configuration file.
        -ProductRebuildsEnvironment is the first install: the product script creates the service
        and writes its Environment from scratch, so nothing the old one held survives.
    #>
    param(
        [Parameter(Mandatory = $true)] $Definition,
        [Parameter(Mandatory = $true)] $BaseConfiguration,
        [string[]] $EnvironmentBefore = @(),
        [string] $Supplied,
        [switch] $ProductRebuildsEnvironment,
        [string] $RosterText = '{"operators":[{"operatorId":"OP-1","roles":["R-11"]}]}'
    )
    $carried = Get-ParallelServiceEnvironmentEntry -Environment $EnvironmentBefore -Name $credentialName
    $credential = Resolve-ParallelFaultRecoveryCredential -Definition $Definition -Supplied $Supplied -Carried $carried
    # The service's Environment and its restarts, as the step sees them through its actions.
    $service = @{ Environment = [string[]] ($ProductRebuildsEnvironment ? @($productEnvironment) : @($EnvironmentBefore)); Log = [System.Collections.Generic.List[string]]::new() }
    $directory = Join-Path ([IO.Path]::GetTempPath()) "cs454-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory | Out-Null
    try {
        $file = Join-Path $directory 'appsettings.Production.json'
        $roster = Join-Path $directory 'field-operator-roles.json'
        [IO.File]::WriteAllText($file, (ConvertTo-Json -InputObject $BaseConfiguration -Depth 12), [Text.UTF8Encoding]::new($false))
        if ($null -ne $RosterText) { [IO.File]::WriteAllText($roster, $RosterText, [Text.UTF8Encoding]::new($false)) }
        $readiness = Invoke-ParallelInstanceConfigurationStep -ConfigurationPath $file -Definition $Definition -Credential $credential `
            -CredentialVariable $credentialName -RosterPath $roster -Actions @{
                GetEnvironment = { $service.Environment }
                SetEnvironment = { param([string[]] $Environment) $service.Environment = $Environment; $service.Log.Add('set-environment') }
                RestartService = { $service.Log.Add('restart') }
            }
        $onDisk = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    } finally {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
    }
    return [pscustomobject]@{ Configuration = $onDisk; Environment = $service.Environment; Readiness = $readiness; Log = @($service.Log) }
}

function Assert-ClearancePath {
    param([string] $Name, $Result, [string] $ExpectedCredential)
    $c = $Result.Configuration
    $vfr = $c.Contains('VehicleFaultRecovery') ? $c['VehicleFaultRecovery'] : $null
    $roles = $c.Contains('FieldOperatorRoles') ? $c['FieldOperatorRoles'] : $null
    Write-Result -Ok ($null -ne $vfr -and $vfr['enabled'] -eq $true -and $vfr['credentialEnvironmentVariable'] -ceq $credentialName) `
        -Name "$($Name): VehicleFaultRecovery on disk is the definition's, with this instance's variable" `
        -Detail ("got " + (ConvertTo-Json -InputObject $vfr -Compress))
    Write-Result -Ok ($null -ne $roles -and $roles['path'] -ceq $rosterPath -and $roles['onboardClearanceEntryDeclared'] -eq $true) `
        -Name "$($Name): FieldOperatorRoles on disk is the definition's" `
        -Detail ("got " + (ConvertTo-Json -InputObject $roles -Compress))
    $twins = @(Find-CaseTwin -Node $c -Path '')
    Write-Result -Ok ($twins.Count -eq 0) -Name "$($Name): no key on disk differs from another only in case" -Detail ($twins -join ', ')
    $entries = @($Result.Environment | Where-Object { $_ -like "$credentialName=*" })
    Write-Result -Ok ($entries.Count -eq 1 -and $entries[0] -ceq "$credentialName=$ExpectedCredential") `
        -Name "$($Name): the service Environment carries exactly one credential entry, the expected one" `
        -Detail ("got " + ($entries -join ' | '))
    # The service must be restarted, once, after the Environment changed: neither the file nor the
    # credential is read by a running service (review M12).
    Write-Result -Ok ((@($Result.Log) -join ',') -ceq 'set-environment,restart') `
        -Name "$($Name): the credential is written, then the service is restarted exactly once" -Detail ("log: " + (@($Result.Log) -join ','))
    $kept = @($productEnvironment | Where-Object { $Result.Environment -notcontains $_ })
    Write-Result -Ok ($kept.Count -eq 0) -Name "$($Name): the product's own Environment entries are kept" -Detail ("lost " + ($kept -join ', '))
    Write-Result -Ok (@($Result.Readiness.Fatal).Count -eq 0 -and @($Result.Readiness.Reasons).Count -eq 0) `
        -Name "$($Name): readiness reads the exit as available" `
        -Detail ("fatal: " + (@($Result.Readiness.Fatal) -join ' | ') + "; reasons: " + (@($Result.Readiness.Reasons) -join ','))
}

function Test-OverlayLayer {
    # The pre-#454 functions alone: overlay merged onto the base the path leaves behind.
    param([string] $Name, $Base)
    $layered = Merge-ConfigurationTree -Base $Base -Overlay (New-ParallelInstanceConfigurationOverlay -Definition $clearanceDefinition)
    $vfr = $layered.Contains('VehicleFaultRecovery') ? $layered['VehicleFaultRecovery'] : $null
    $roles = $layered.Contains('FieldOperatorRoles') ? $layered['FieldOperatorRoles'] : $null
    $twins = @(Find-CaseTwin -Node $layered -Path '')
    Write-Result -Ok ($null -ne $vfr -and $vfr['enabled'] -eq $true -and $null -ne $roles -and $roles['path'] -ceq $rosterPath -and $twins.Count -eq 0) `
        -Name "$($Name): the overlay merged onto what the product script left carries both sections, once" `
        -Detail ("VehicleFaultRecovery=" + (ConvertTo-Json -InputObject $vfr -Compress) + " FieldOperatorRoles=" +
            (ConvertTo-Json -InputObject $roles -Compress) + " twins=" + ($twins -join ','))
}

function Invoke-PathCase {
    param([string] $Name, [scriptblock] $Body)
    try { & $Body } catch { Write-Result -Ok $false -Name "$($Name): the step ran" -Detail "threw: $($_.Exception.Message)" }
}

# --- First install: the product script writes the file and the Environment from scratch. The
# credential can only come from the control host (deploy-config.json).
$firstBase = Copy-Definition $productConfiguration
Test-OverlayLayer -Name 'first install' -Base $firstBase
Invoke-PathCase 'first install' {
    $r = Invoke-ClearancePath -Definition $clearanceDefinition -BaseConfiguration $firstBase -Supplied 'selftest-new' -ProductRebuildsEnvironment
    Assert-ClearancePath -Name 'first install' -Result $r -ExpectedCredential 'selftest-new'
}

# --- Upgrade: the product script keeps the installed file and the Environment. The file carries
# the 10-07 hand merge, with other casing and stale values; the Environment the old credential.
$upgradeBase = Copy-Definition $productConfiguration
$upgradeBase['vehicleFaultRecovery'] = [ordered]@{ Enabled = $false; credentialEnvironmentVariable = 'CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL' }
$upgradeBase['FieldOperatorRoles'] = [ordered]@{ Path = 'C:\old\roster.json'; onboardClearanceEntryDeclared = $false }
$upgradeEnvironment = @($productEnvironment) + "$credentialName=selftest-old"
Test-OverlayLayer -Name 'upgrade' -Base $upgradeBase
Invoke-PathCase 'upgrade' {
    $r = Invoke-ClearancePath -Definition $clearanceDefinition -BaseConfiguration $upgradeBase -EnvironmentBefore $upgradeEnvironment -Supplied 'selftest-new'
    Assert-ClearancePath -Name 'upgrade' -Result $r -ExpectedCredential 'selftest-new'
}

# --- Rollback: no deploy-config.json, and a previous generation installed before these sections
# existed. The credential is the one the service already holds.
$rollbackBase = Copy-Definition $productConfiguration
Test-OverlayLayer -Name 'rollback' -Base $rollbackBase
Invoke-PathCase 'rollback' {
    $r = Invoke-ClearancePath -Definition $clearanceDefinition -BaseConfiguration $rollbackBase -EnvironmentBefore $upgradeEnvironment
    Assert-ClearancePath -Name 'rollback' -Result $r -ExpectedCredential 'selftest-old'
}
# ... and a rollback that lands in the product's first-install branch (the service was gone) still
# carries the credential it read before the product script ran.
Invoke-PathCase 'rollback onto a removed service' {
    $r = Invoke-ClearancePath -Definition $clearanceDefinition -BaseConfiguration $rollbackBase -EnvironmentBefore $upgradeEnvironment -ProductRebuildsEnvironment
    Assert-ClearancePath -Name 'rollback onto a removed service' -Result $r -ExpectedCredential 'selftest-old'
}

Write-Host ''
Write-Host 'The credential: refused before anything is stopped when the entry is on and there is none' -ForegroundColor Cyan

$credentialCases = @(
    @{ Name = 'first install, entry on, no credential supplied'; Supplied = ''; Carried = @(); Throws = $true }
    @{ Name = 'rollback, entry on, the service holds none'; Supplied = $null; Carried = @($productEnvironment); Throws = $true }
    @{ Name = 'entry on, a whitespace credential supplied'; Supplied = '   '; Carried = @(); Throws = $true }
    @{ Name = 'entry on, supplied wins over carried'; Supplied = 'selftest-new'; Carried = @("$credentialName=selftest-old"); Throws = $false; Expect = 'selftest-new' }
    @{ Name = 'entry on, carried when none supplied'; Supplied = $null; Carried = @("$credentialName=selftest-old"); Throws = $false; Expect = 'selftest-old' }
)
foreach ($case in $credentialCases) {
    $ok = $false; $detail = ''
    try {
        $carried = Get-ParallelServiceEnvironmentEntry -Environment $case.Carried -Name $credentialName
        $got = Resolve-ParallelFaultRecoveryCredential -Definition $clearanceDefinition -Supplied $case.Supplied -Carried $carried
        if ($case.Throws) { $detail = "returned '$got' instead of refusing" } else { $ok = $got -ceq $case.Expect; $detail = "got '$got'" }
    } catch {
        $message = $_.Exception.Message
        if ($case.Throws) { $ok = $message.Contains($credentialName); $detail = "refused without naming the variable: $message" }
        else { $detail = "threw: $message" }
    }
    Write-Result -Ok $ok -Name $case.Name -Detail $detail
}
# Entry off: no credential is fine, and nothing is added to the Environment.
Invoke-PathCase 'entry off, no credential' {
    $off = Copy-Definition $clearanceDefinition
    $off['vehicleFaultRecovery']['enabled'] = $false
    $r = Invoke-ClearancePath -Definition $off -BaseConfiguration (Copy-Definition $productConfiguration) -ProductRebuildsEnvironment
    Write-Result -Ok (@($r.Environment | Where-Object { $_ -like "$credentialName=*" }).Count -eq 0 -and
        $r.Configuration['VehicleFaultRecovery']['enabled'] -eq $false -and
        $r.Configuration['VehicleFaultRecovery']['credentialEnvironmentVariable'] -ceq $credentialName) `
        -Name 'entry off, no credential: accepted, the section is still written, nothing added to the Environment' `
        -Detail ("environment: " + ($r.Environment -join ' | '))
    Write-Result -Ok ((@($r.Log) -join ',') -ceq 'restart') -Name 'entry off, no credential: the Environment is not rewritten, the service is restarted' `
        -Detail ("log: " + (@($r.Log) -join ','))
}
# A broken state is judged before the restart (review N1): the entry on and no credential reaching the
# step -- Resolve- would have stopped it earlier; this is the step's own line of defence.
Invoke-PathCase 'broken state is not restarted into' {
    $restarts = [System.Collections.Generic.List[string]]::new()
    $directory = Join-Path ([IO.Path]::GetTempPath()) "cs454-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory | Out-Null
    try {
        $file = Join-Path $directory 'appsettings.Production.json'
        [IO.File]::WriteAllText($file, (ConvertTo-Json -InputObject (Copy-Definition $productConfiguration) -Depth 12), [Text.UTF8Encoding]::new($false))
        $message = $null
        try {
            $null = Invoke-ParallelInstanceConfigurationStep -ConfigurationPath $file -Definition $clearanceDefinition -Credential $null `
                -CredentialVariable $credentialName -RosterPath (Join-Path $directory 'roster.json') -Actions @{
                    GetEnvironment = { @($productEnvironment) }
                    SetEnvironment = { param([string[]] $Environment) $restarts.Add('set-environment') }
                    RestartService = { $restarts.Add('restart') }
                }
        } catch { $message = $_.Exception.Message }
        Write-Result -Ok ($null -ne $message -and $message.Contains('CLEARANCE_EXIT_BROKEN') -and $restarts.Count -eq 0) `
            -Name 'entry on without a credential: CLEARANCE_EXIT_BROKEN, and the service is NOT restarted' `
            -Detail ("message: $message; actions: " + ($restarts -join ','))
    } finally {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Invoke-PathCase 'environment entry replacement' {
    $replaced = @(Set-ParallelServiceEnvironmentEntry -Environment @('A=1', "$credentialName=old", "$($credentialName)_X=keep", 'B=2') -Name $credentialName -Value 'new=with=equals')
    Write-Result -Ok (($replaced -join '|') -ceq "A=1|$($credentialName)_X=keep|B=2|$credentialName=new=with=equals") `
        -Name 'replacing an Environment entry touches only that name (not a longer one sharing its prefix)' -Detail ($replaced -join ' | ')
    $read = Get-ParallelServiceEnvironmentEntry -Environment $replaced -Name $credentialName
    Write-Result -Ok ($read -ceq 'new=with=equals') -Name 'reading an entry back keeps an = inside the value' -Detail "got '$read'"
}

Write-Host ''
Write-Host 'Readiness: what the service will read, said out loud' -ForegroundColor Cyan

# The reason codes are the server's own (StationClearanceExit.cs). Read from the source, so a rename
# there turns this red instead of the readiness line drifting into a vocabulary nobody else uses.
$exitSource = Get-Content -LiteralPath (Join-Path $repoRoot 'src/ControlServer.Host/Runtime/Charging/StationClearanceExit.cs') -Raw
$code = @{}
foreach ($constant in @('RosterEmpty', 'NoEntry', 'NoRecoveryEntry')) {
    $code[$constant] = [regex]::Match($exitSource, "const string $constant = ""([A-Z_]+)""").Groups[1].Value
}
Write-Result -Ok (@($code.Values | Where-Object { $_ }).Count -eq 3) -Name 'the three reason codes are read from StationClearanceExit.cs' `
    -Detail (ConvertTo-Json -InputObject $code -Compress)

$phase1 = Copy-Definition $productConfiguration
$phase1['VehicleFaultRecovery'] = [ordered]@{ enabled = $false; credentialEnvironmentVariable = $credentialName }
$phase1['FieldOperatorRoles'] = [ordered]@{ path = $rosterPath; onboardClearanceEntryDeclared = $false }
$phase2 = Copy-Definition $phase1
$phase2['VehicleFaultRecovery']['enabled'] = $true
$phase2['FieldOperatorRoles']['onboardClearanceEntryDeclared'] = $true
$named = '{"operators":[{"operatorId":"OP-1","roles":["R-11"]}]}'
# The roster template in PR #455 (control-server#411): operatorId left empty on purpose.
$template = '{"operators":[{"operatorId":"","fillIn":"...","roles":["R-11"]},{"operatorId":"","fillIn":"...","roles":["R-13"]}]}'
$withCredential = @('DOTNET_ENVIRONMENT', $credentialName)
$readinessCases = @(
    @{ Name = 'both sections missing'; Config = (Copy-Definition $productConfiguration); Env = $withCredential; Roster = $named
        Fatal = @('VehicleFaultRecovery', 'FieldOperatorRoles'); Reasons = $null }
    @{ Name = 'entry on, credential variable missing from the service'; Config = $phase2; Env = @('DOTNET_ENVIRONMENT'); Roster = $named
        Fatal = @($credentialName); Reasons = $null }
    @{ Name = 'phase 1 as shipped (entry off, empty roster, nothing declared)'; Config = $phase1; Env = @('DOTNET_ENVIRONMENT'); Roster = '{"operators":[]}'
        Fatal = @(); Reasons = @($code.RosterEmpty, $code.NoEntry, $code.NoRecoveryEntry) }
    @{ Name = 'phase 2 with a named R-11 and the credential'; Config = $phase2; Env = $withCredential; Roster = $named
        Fatal = @(); Reasons = @() }
    @{ Name = 'the PR #455 roster template left unfilled'; Config = $phase2; Env = $withCredential; Roster = $template
        Fatal = @(); Reasons = @($code.RosterEmpty) }
    @{ Name = 'roster file missing'; Config = $phase2; Env = $withCredential; Roster = $null
        Fatal = @(); Reasons = @($code.RosterEmpty) }
    @{ Name = 'roster file unreadable JSON'; Config = $phase2; Env = $withCredential; Roster = '{"operators":[{'
        Fatal = @(); Reasons = @($code.RosterEmpty) }
    @{ Name = 'roster in PascalCase (the server reads it case-insensitively)'; Config = $phase2; Env = $withCredential
        Roster = '{"Operators":[{"OperatorId":"OP-1","Roles":["R-13"]}]}'; Fatal = @(); Reasons = @() }
    @{ Name = 'only the onboard entry declared: exit open, recovery entry not'; Config = (& { $c = Copy-Definition $phase1; $c['FieldOperatorRoles']['onboardClearanceEntryDeclared'] = $true; $c })
        Env = @('DOTNET_ENVIRONMENT'); Roster = $named; Fatal = @(); Reasons = @($code.NoRecoveryEntry) }
)
foreach ($case in $readinessCases) {
    Invoke-PathCase $case.Name {
        $r = Get-ParallelClearanceExitReadiness -Configuration $case.Config -EnvironmentNames $case.Env -RosterText $case.Roster
        $fatal = @($r.Fatal)
        $missingFatal = @($case.Fatal | Where-Object { $f = $_; -not ($fatal | Where-Object { $_.Contains($f) }) })
        $fatalOk = ($case.Fatal.Count -eq 0) ? ($fatal.Count -eq 0) : ($missingFatal.Count -eq 0)
        $reasonsOk = ($null -eq $case.Reasons) -or ((@($r.Reasons) -join ',') -ceq (@($case.Reasons) -join ','))
        $lineOk = $r.Line -like 'CLEARANCE_EXIT_READINESS *' -and -not $r.Line.Contains('selftest')
        Write-Result -Ok ($fatalOk -and $reasonsOk -and $lineOk) -Name "readiness: $($case.Name)" `
            -Detail ("fatal: " + ($fatal -join ' | ') + "; reasons: " + (@($r.Reasons) -join ',') + "; line: $($r.Line)")
    }
}

Write-Host ''
Write-Host 'Upgrade and rollback get past the product upgrade preflight (control-server#454)' -ForegroundColor Cyan

<#
    Update-ControlServerLocal.ps1 refuses an installed configuration whose JourneyRuntime is not
    disabled ('JourneyRuntime must remain disabled during upgrade.', added with the upgrade script in
    ae2f99be9). The reason: the upgrade starts the new, unproven binary on the retained configuration
    -- start, live check, restart, live check again -- and its result file records
    journeyRuntimeEnabled=false, vehicleMoved=false. A runtime that is on would poll demand and place
    orders from inside that lifecycle check, and the upgrade's own rollback restores whatever the
    backup held.

    The parallel overlay writes JourneyRuntime.enabled=true, so after a first install every upgrade
    and every -Rollback (which runs the upgrade script while the service exists) was refused there.
    The fix keeps the preflight's intent: the installer stops the service, sets the flag false in the
    file, and only then calls the upgrade script -- whose backup and rollback therefore hold false --
    and the overlay writes true again only after the upgrade succeeded.
#>
$updateSource = Get-Content -LiteralPath (Join-Path $repoRoot 'scripts/Update-ControlServerLocal.ps1') -Raw
$preflightLine = "if (`$configuration.JourneyRuntime.enabled -ne `$false) { throw 'JourneyRuntime must remain disabled during upgrade.' }"
Write-Result -Ok ($updateSource.Contains($preflightLine)) `
    -Name 'the premise: the product upgrade script still refuses an installed configuration with JourneyRuntime on' `
    -Detail 'the preflight line changed or is gone; reread Update-ControlServerLocal.ps1 before trusting the cases below'
# The preflight exactly as the upgrade script evaluates it: ConvertFrom-Json without -AsHashtable,
# whose objects resolve properties ignoring case.
$preflightPasses = { param([string] $Text) ($Text | ConvertFrom-Json).JourneyRuntime.enabled -eq $false }

$installed = Merge-ConfigurationTree -Base (Copy-Definition $productConfiguration) -Overlay (New-ParallelInstanceConfigurationOverlay -Definition $clearanceDefinition)
$installedText = ConvertTo-Json -InputObject $installed -Depth 12
Write-Result -Ok (-not (& $preflightPasses $installedText)) `
    -Name 'reproduces the defect: the configuration a first install leaves is refused by the upgrade preflight' -Detail 'it passed'

Invoke-PathCase 'disable before upgrade' {
    $file = Join-Path ([IO.Path]::GetTempPath()) "cs454-preflight-$([guid]::NewGuid().ToString('N')).json"
    try {
        # A hand-edited file may spell the section another way; the preflight reads it either way.
        [IO.File]::WriteAllText($file, ($installedText -replace '"JourneyRuntime"', '"journeyRuntime"'), [Text.UTF8Encoding]::new($false))
        $before = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable -Depth 12
        $null = Set-ParallelInstanceJourneyRuntimeDisabled -Path $file
        $afterText = Get-Content -LiteralPath $file -Raw
        $after = $afterText | ConvertFrom-Json -AsHashtable -Depth 12
        Write-Result -Ok (& $preflightPasses $afterText) -Name 'after the disable step the upgrade preflight passes' -Detail $afterText
        $journeyKeys = @($after.Keys | Where-Object { $_ -ieq 'JourneyRuntime' })
        $unchangedJourney = @($before['journeyRuntime'].Keys | Where-Object { $_ -ne 'enabled' } |
                Where-Object { (ConvertTo-Json $before['journeyRuntime'][$_] -Compress) -cne (ConvertTo-Json $after[$journeyKeys[0]][$_] -Compress) })
        $otherSections = @($before.Keys | Where-Object { $_ -ine 'journeyRuntime' } |
                Where-Object { (ConvertTo-Json $before[$_] -Compress -Depth 12) -cne (ConvertTo-Json $after[$_] -Compress -Depth 12) })
        Write-Result -Ok ($journeyKeys.Count -eq 1 -and $unchangedJourney.Count -eq 0 -and $otherSections.Count -eq 0) `
            -Name 'the disable step changes JourneyRuntime.enabled and nothing else (one section, identity kept)' `
            -Detail ("journey sections: $($journeyKeys -join ',') changed journey keys: $($unchangedJourney -join ',') changed sections: $($otherSections -join ',')")
        # ... and the overlay, applied after a successful upgrade, turns it back on.
        $restored = Update-ParallelInstanceConfigurationFile -Path $file -Definition $clearanceDefinition
        $restoredJourney = @($restored.Keys | Where-Object { $_ -ieq 'JourneyRuntime' })
        Write-Result -Ok ($restoredJourney.Count -eq 1 -and $restored[$restoredJourney[0]]['enabled'] -eq $true) `
            -Name 'the overlay after a successful upgrade writes JourneyRuntime.enabled back to the definition''s true' `
            -Detail (ConvertTo-Json $restored[$restoredJourney[0]] -Compress)
    } finally {
        Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    }
}

# The wrapper the installer's upgrade branch runs (Invoke-ParallelProductUpgrade), driven with fake
# actions against a temporary configuration file: stop, then set false, then upgrade; on a failure
# the flag stays false, JOURNEY_RUNTIME_LEFT_DISABLED says how to recover, and the failure is
# rethrown; with RIoT dispatch open nothing at all happens (review S3, S4 M5/M6).
function Invoke-UpgradeCase {
    param([string] $InstalledText, [scriptblock] $Update = { 'upgrade ran' })
    $directory = Join-Path ([IO.Path]::GetTempPath()) "cs454-upgrade-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory | Out-Null
    $file = Join-Path $directory 'appsettings.Production.json'
    [IO.File]::WriteAllText($file, $InstalledText, [Text.UTF8Encoding]::new($false))
    $seen = [System.Collections.Generic.List[string]]::new()
    $flagOf = { ((Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).JourneyRuntime.enabled).ToString().ToLowerInvariant() }
    $thrown = $null; $warnings = @()
    try {
        $null = Invoke-ParallelProductUpgrade -ConfigurationPath $file -ServiceName '8005 AGV ControlServer V2' -WarningVariable +warnings -WarningAction SilentlyContinue -Actions @{
            StopService = { $seen.Add("stop(flag=$(& $flagOf))") }
            InvokeUpdate = { $seen.Add("update(flag=$(& $flagOf))"); & $Update }
            ServiceStatus = { 'Running' }
        }
    } catch { $thrown = $_.Exception.Message }
    $after = & $flagOf
    $text = Get-Content -LiteralPath $file -Raw
    Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Seen = @($seen); Thrown = $thrown; Warnings = @($warnings | ForEach-Object { "$_" }); FlagAfter = $after; Text = $text }
}

Invoke-PathCase 'upgrade wrapper, success' {
    $r = Invoke-UpgradeCase -InstalledText $installedText
    Write-Result -Ok ((@($r.Seen) -join ',') -ceq 'stop(flag=true),update(flag=false)' -and $null -eq $r.Thrown -and $r.FlagAfter -eq 'false') `
        -Name 'upgrade: the service is stopped while the flag is still true, the upgrade runs with it false, and it stays false for the overlay to restore' `
        -Detail ("seen: " + (@($r.Seen) -join ',') + " thrown: $($r.Thrown) after: $($r.FlagAfter)")
}
Invoke-PathCase 'upgrade wrapper, failure' {
    $r = Invoke-UpgradeCase -InstalledText $installedText -Update { throw 'simulated upgrade failure' }
    Write-Result -Ok ($r.Thrown -ceq 'simulated upgrade failure') -Name 'a failed upgrade is rethrown unchanged, never swallowed (M6)' -Detail "thrown: $($r.Thrown)"
    Write-Result -Ok ($r.FlagAfter -eq 'false') -Name 'a failed upgrade leaves JourneyRuntime.enabled false, not restored to true (M5)' -Detail "after: $($r.FlagAfter)"
    $warning = @($r.Warnings | Where-Object { $_ -like 'JOURNEY_RUNTIME_LEFT_DISABLED*' })
    Write-Result -Ok ($warning.Count -eq 1 -and $warning[0].Contains('redeploy the commit that is running now') -and $warning[0].Contains('Do NOT use -Rollback')) `
        -Name 'a failed upgrade says JOURNEY_RUNTIME_LEFT_DISABLED and to redeploy the running commit, not -Rollback (review S1)' -Detail ("warnings: " + ($r.Warnings -join ' | '))
}
Invoke-PathCase 'upgrade wrapper, dispatch open' {
    $open = $installedText | ConvertFrom-Json -AsHashtable -Depth 12
    $open['RiotCreateDispatch'] = [ordered]@{ enabled = $true }
    $openText = ConvertTo-Json -InputObject $open -Depth 12
    $r = Invoke-UpgradeCase -InstalledText $openText
    Write-Result -Ok ($null -ne $r.Thrown -and $r.Thrown.Contains('UPGRADE_REFUSED_DISPATCH_OPEN') -and @($r.Seen).Count -eq 0 -and $r.Text -ceq $openText) `
        -Name 'dispatch open: refused before the service is stopped, the file untouched (review S3)' `
        -Detail ("thrown: $($r.Thrown); seen: " + (@($r.Seen) -join ',') + "; file unchanged: $($r.Text -ceq $openText)")
}
$v2Configuration = 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json'
$v2Service = '8005 AGV ControlServer V2'
$mvpConfiguration = 'C:\Program Files\8005 AGV\ControlServer\appsettings.Production.json'
$mvpService = '8005 AGV ControlServer'
# The operator's steps must name THIS instance's file and service, warn off the MVP's (one '.V2'
# apart), warn about Notepad's "Save As", and put "stop new demand, wait for Completed" before the
# edit and restart (incremental review, item 1).
function Test-ClosingSteps {
    param([string] $Message)
    $missing = @()
    foreach ($needle in @($v2Configuration, "NOT the MVP's $mvpConfiguration", "'$v2Service'", "NOT '$mvpService'", 'Notepad', 'agvId', 'stop injecting new demand', 'Completed', '20-set-control-server-parallel-dispatch-gate.ps1 -State Closed')) {
        if (-not $Message.Contains($needle)) { $missing += $needle }
    }
    $stopAt = $Message.IndexOf('stop injecting new demand'); $editAt = $Message.IndexOf("edit $v2Configuration"); $restartAt = $Message.IndexOf("restart the service '$v2Service'")
    if (-not ($stopAt -ge 0 -and $editAt -gt $stopAt -and $restartAt -gt $editAt)) { $missing += "order stop($stopAt) < edit($editAt) < restart($restartAt)" }
    return $missing
}
$refusalCases = @(
    @{ Name = 'RiotCreateDispatch.enabled true'; Config = [ordered]@{ RiotCreateDispatch = [ordered]@{ enabled = $true } }; Refused = $true }
    @{ Name = 'riotCreateDispatch.Enabled true (other casing)'; Config = [ordered]@{ riotCreateDispatch = [ordered]@{ Enabled = $true } }; Refused = $true }
    @{ Name = 'RiotCreateDispatch.enabled false'; Config = [ordered]@{ RiotCreateDispatch = [ordered]@{ enabled = $false } }; Refused = $false }
    @{ Name = 'no RiotCreateDispatch section (product default: closed)'; Config = [ordered]@{ JourneyRuntime = [ordered]@{ enabled = $true } }; Refused = $false }
)
foreach ($case in $refusalCases) {
    $refusal = Get-ParallelUpgradeRefusal -Configuration $case.Config -ConfigurationPath $v2Configuration -ServiceName $v2Service
    $gaps = $case.Refused -and $null -ne $refusal ? @(Test-ClosingSteps $refusal) : @()
    Write-Result -Ok ($case.Refused ? ($null -ne $refusal -and $refusal.StartsWith('UPGRADE_REFUSED_DISPATCH_OPEN') -and $gaps.Count -eq 0) : ($null -eq $refusal)) `
        -Name "upgrade refusal: $($case.Name) -> $($case.Refused ? 'refused, with the V2 file and service named and the MVP''s warned off' : 'allowed')" `
        -Detail "missing: $($gaps -join ' | '); got: $refusal"
}

# The installer's first check, before anything is touched (Get-ParallelPreInstallRefusal). Fail closed:
# a service with no installed configuration is a state nobody can explain, and -Rollback used to swap
# the package directories before finding out (control-server#454, found in self-review after S3); and a
# file written after the running process started is not what that process is doing (incremental review,
# item 2) -- the hand edit to "false" that nobody restarted for is exactly the open gate S3 refuses.
$closedText = ConvertTo-Json -InputObject ([ordered]@{ RiotCreateDispatch = [ordered]@{ enabled = $false }; JourneyRuntime = [ordered]@{ enabled = $true } }) -Depth 5
$openText = ConvertTo-Json -InputObject ([ordered]@{ RiotCreateDispatch = [ordered]@{ enabled = $true } }) -Depth 5
$started = [datetime]::new(2026, 10, 8, 8, 0, 0, [DateTimeKind]::Utc)
$before = $started.AddMinutes(-5); $after = $started.AddMinutes(5)
$preInstallCases = @(
    @{ Name = 'no service (first install), no configuration'; Exists = $false; Status = $null; Text = $null; Write = $null; Start = $null; Expect = $null }
    @{ Name = 'no service, a configuration left behind'; Exists = $false; Status = $null; Text = $openText; Write = $after; Start = $null; Expect = $null }
    @{ Name = 'service, configuration missing'; Exists = $true; Status = 'Running'; Text = $null; Write = $null; Start = $started; Expect = 'INSTALLED_CONFIGURATION_MISSING' }
    @{ Name = 'service, configuration empty'; Exists = $true; Status = 'Running'; Text = ''; Write = $before; Start = $started; Expect = 'INSTALLED_CONFIGURATION_UNREADABLE' }
    @{ Name = 'service, configuration not JSON'; Exists = $true; Status = 'Running'; Text = '{"RiotCreateDispatch":'; Write = $before; Start = $started; Expect = 'INSTALLED_CONFIGURATION_UNREADABLE' }
    @{ Name = 'service, configuration a JSON array'; Exists = $true; Status = 'Running'; Text = '[1,2]'; Write = $before; Start = $started; Expect = 'INSTALLED_CONFIGURATION_UNREADABLE' }
    @{ Name = 'running, dispatch open, file older than the process'; Exists = $true; Status = 'Running'; Text = $openText; Write = $before; Start = $started; Expect = 'UPGRADE_REFUSED_DISPATCH_OPEN' }
    @{ Name = 'running, dispatch closed, file older than the process'; Exists = $true; Status = 'Running'; Text = $closedText; Write = $before; Start = $started; Expect = $null }
    @{ Name = 'running, file edited to closed AFTER the process started (not restarted)'; Exists = $true; Status = 'Running'; Text = $closedText; Write = $after; Start = $started; Expect = 'CONFIGURATION_CHANGED_SINCE_START' }
    @{ Name = 'running, process start time unknown'; Exists = $true; Status = 'Running'; Text = $closedText; Write = $before; Start = $null; Expect = 'SERVICE_START_TIME_UNKNOWN' }
    @{ Name = 'start pending, process start time unknown'; Exists = $true; Status = 'StartPending'; Text = $closedText; Write = $before; Start = $null; Expect = 'SERVICE_NOT_SETTLED' }
    @{ Name = 'service status unknown, process start time unknown'; Exists = $true; Status = $null; Text = $closedText; Write = $before; Start = $null; Expect = 'SERVICE_NOT_SETTLED' }
    # Not settled refuses even with both times known and the file older than the process: mid-transition
    # the process may not have read the file yet, so the comparison that would let these through proves
    # nothing (incremental review, item 2).
    @{ Name = 'start pending, times known, file older'; Exists = $true; Status = 'StartPending'; Text = $closedText; Write = $before; Start = $started; Expect = 'SERVICE_NOT_SETTLED' }
    @{ Name = 'stop pending, times known, file older'; Exists = $true; Status = 'StopPending'; Text = $closedText; Write = $before; Start = $started; Expect = 'SERVICE_NOT_SETTLED' }
    @{ Name = 'continue pending, times known, file older'; Exists = $true; Status = 'ContinuePending'; Text = $closedText; Write = $before; Start = $started; Expect = 'SERVICE_NOT_SETTLED' }
    @{ Name = 'pause pending, times known, file older'; Exists = $true; Status = 'PausePending'; Text = $closedText; Write = $before; Start = $started; Expect = 'SERVICE_NOT_SETTLED' }
    @{ Name = 'paused, times known, file older'; Exists = $true; Status = 'Paused'; Text = $closedText; Write = $before; Start = $started; Expect = 'SERVICE_NOT_SETTLED' }
    @{ Name = 'status unknown, times known, file older'; Exists = $true; Status = $null; Text = $closedText; Write = $before; Start = $started; Expect = 'SERVICE_NOT_SETTLED' }
    @{ Name = 'running, file write time unknown'; Exists = $true; Status = 'Running'; Text = $closedText; Write = $null; Start = $started; Expect = 'CONFIGURATION_WRITE_TIME_UNKNOWN' }
    @{ Name = 'stopped, file newer, no process (the file is the truth)'; Exists = $true; Status = 'Stopped'; Text = $closedText; Write = $after; Start = $null; Expect = $null }
    @{ Name = 'stopped, dispatch open'; Exists = $true; Status = 'Stopped'; Text = $openText; Write = $after; Start = $null; Expect = 'UPGRADE_REFUSED_DISPATCH_OPEN' }
)
foreach ($case in $preInstallCases) {
    $got = $null; $err = $null
    try {
        $got = Get-ParallelPreInstallRefusal -ServiceExists $case.Exists -ServiceStatus $case.Status -ServiceName $v2Service `
            -ConfigurationPath $v2Configuration -ConfigurationText $case.Text -ConfigurationWriteTimeUtc $case.Write -ProcessStartTimeUtc $case.Start
    } catch { $err = $_.Exception.Message }
    $ok = $null -eq $err -and (($null -eq $case.Expect) ? ($null -eq $got) : ($null -ne $got -and $got.StartsWith($case.Expect) -and $got.EndsWith('Nothing was stopped or changed.')))
    Write-Result -Ok $ok -Name "pre-install check: $($case.Name) -> $($case.Expect ?? 'go ahead')" -Detail "got: $got; threw: $err"
}
$missing = Get-ParallelPreInstallRefusal -ServiceExists $true -ServiceStatus 'Running' -ServiceName $v2Service -ConfigurationPath $v2Configuration -ConfigurationText $null -ConfigurationWriteTimeUtc $null -ProcessStartTimeUtc $started
Write-Result -Ok ($null -ne $missing -and $missing.Contains($v2Configuration) -and $missing.Contains('find out why')) `
    -Name 'a missing configuration names the V2 path and tells the operator to find out why' -Detail "got: $missing"
# Third quick review, T4: the comparison must not depend on the caller having converted to UTC. [datetime]
# comparison ignores Kind, so on a UTC+8 machine a start time handed over in local time reads 8 hours
# late, and a file edited within 8 hours after the start -- not restarted for -- would be let through.
# The same instant in two Kinds, both directions; on a machine whose local offset is zero the two Kinds
# are the same ticks and these cases cannot tell the difference, which is said rather than passed.
$offset = [TimeZoneInfo]::Local.GetUtcOffset($started)
if ($offset -eq [TimeSpan]::Zero) {
    Write-Host '  NOTE  local UTC offset is zero here: the mixed-Kind cases below cannot discriminate' -ForegroundColor Yellow
}
$startLocal = $started.ToLocalTime()
$writeLocal = $after.ToLocalTime()
$mixed = @(
    @{ Name = 'start time in local Kind, file written 5 minutes after it'; Write = $after; Start = $startLocal; Expect = 'CONFIGURATION_CHANGED_SINCE_START' }
    @{ Name = 'write time in local Kind, 5 minutes after a UTC start'; Write = $writeLocal; Start = $started; Expect = 'CONFIGURATION_CHANGED_SINCE_START' }
    @{ Name = 'start time in local Kind, file written 5 minutes before it'; Write = $before; Start = $startLocal; Expect = $null }
)
foreach ($case in $mixed) {
    $got = Get-ParallelPreInstallRefusal -ServiceExists $true -ServiceStatus 'Running' -ServiceName $v2Service -ConfigurationPath $v2Configuration `
        -ConfigurationText $closedText -ConfigurationWriteTimeUtc $case.Write -ProcessStartTimeUtc $case.Start
    $ok = ($null -eq $case.Expect) ? ($null -eq $got) : ($null -ne $got -and $got.StartsWith($case.Expect))
    Write-Result -Ok $ok -Name "pre-install check, times compared as UTC whatever their Kind: $($case.Name) -> $($case.Expect ?? 'go ahead') (T4)" -Detail "got: $got"
}
# ... and the reader hands back UTC, read on this test's own process: a service's process (svchost,
# another account) needs elevation to read, and this run has none. The service reader goes through this
# one (Get-ParallelServiceProcessStartTimeUtc -> Get-ParallelProcessStartTimeUtc).
$ownStart = Get-ParallelProcessStartTimeUtc -ProcessId $PID
$expectedUtc = (Get-Process -Id $PID).StartTime.ToUniversalTime()
Write-Result -Ok ($ownStart -is [datetime] -and $ownStart.Kind -eq [DateTimeKind]::Utc -and $ownStart.Ticks -eq $expectedUtc.Ticks) `
    -Name 'Get-ParallelProcessStartTimeUtc returns the process start time as UTC (T4)' -Detail ("got: $ownStart (Kind " + ($ownStart -is [datetime] ? $ownStart.Kind : 'n/a') + "), expected $expectedUtc")
$readerAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'ParallelHost.psm1'), [ref]$null, [ref]$null)
$serviceReader = $readerAst.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-ParallelServiceProcessStartTimeUtc' }, $true)
$viaProcessReader = $null -ne $serviceReader -and $null -ne $serviceReader.Body.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Get-ParallelProcessStartTimeUtc' }, $true)
Write-Result -Ok $viaProcessReader -Name 'the service start-time reader goes through Get-ParallelProcessStartTimeUtc' -Detail 'it reads the process some other way'
$unsettled = Get-ParallelPreInstallRefusal -ServiceExists $true -ServiceStatus 'StopPending' -ServiceName $v2Service -ConfigurationPath $v2Configuration -ConfigurationText $closedText -ConfigurationWriteTimeUtc $before -ProcessStartTimeUtc $started
Write-Result -Ok ($null -ne $unsettled -and $unsettled.Contains('try again later')) -Name 'a service mid-transition is told to try again later' -Detail "got: $unsettled"
$changed = Get-ParallelPreInstallRefusal -ServiceExists $true -ServiceStatus 'Running' -ServiceName $v2Service -ConfigurationPath $v2Configuration -ConfigurationText $closedText -ConfigurationWriteTimeUtc $after -ProcessStartTimeUtc $started
Write-Result -Ok ($null -ne $changed -and $changed.Contains("Restart '$v2Service'") -and $changed.Contains("NOT '$mvpService'")) `
    -Name 'a changed-since-start configuration says to restart the V2 service, not the MVP''s' -Detail "got: $changed"

# The read-back checks, through the -Writer seam: a write that is lost or lands something else must
# be caught. Without the read-back (M4) or with a per-value check that never reports (M10), these go
# green on a file that does not say what the overlay wrote -- for M10, a file naming agv01.
Invoke-PathCase 'read-back checks' {
    $directory = Join-Path ([IO.Path]::GetTempPath()) "cs454-writer-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory | Out-Null
    try {
        $file = Join-Path $directory 'appsettings.Production.json'
        [IO.File]::WriteAllText($file, $installedText, [Text.UTF8Encoding]::new($false))
        $lost = { param($Path, $Text) }
        $message = $null
        try { $null = Set-ParallelInstanceJourneyRuntimeDisabled -Path $file -Writer $lost } catch { $message = $_.Exception.Message }
        Write-Result -Ok ($null -ne $message -and $message.Contains('is not false after it was set')) `
            -Name 'disabling the runtime: a lost write is caught by the read-back (M4)' -Detail "got: $message"

        $base = Copy-Definition $productConfiguration
        foreach ($tamper in @(
                @{ Name = 'vehicle identity'; Key = 'JourneyRuntime.agvId'; Edit = { param($t) $t.Replace('老厂前线新多仓位2', '老厂前线新多仓位1') } }
                @{ Name = 'vehicle key'; Key = 'JourneyRuntime.vehicleKey'; Edit = { param($t) $t.Replace('BROKERX-f38975561adf46ccb1d2f23833c7d0e4', 'BROKERX-0c20ff0600d644869a6a80c186065d85') } }
                @{ Name = 'MesIngest origin'; Key = 'MesIngest.baseUrl'; Edit = { param($t) $t.Replace('http://127.0.0.1:58188', 'http://127.0.0.1:5088') } }
                @{ Name = 'clearance section'; Key = 'VehicleFaultRecovery.enabled'; Edit = { param($t) ($t | ConvertFrom-Json -AsHashtable -Depth 12 | ForEach-Object { $_.Remove('VehicleFaultRecovery'); ConvertTo-Json $_ -Depth 12 }) } }
            )) {
            [IO.File]::WriteAllText($file, (ConvertTo-Json -InputObject $base -Depth 12), [Text.UTF8Encoding]::new($false))
            $edit = $tamper.Edit
            $corrupting = { param($Path, $Text) [IO.File]::WriteAllText($Path, (& $edit $Text), [Text.UTF8Encoding]::new($false)) }.GetNewClosure()
            $message = $null
            try { $null = Update-ParallelInstanceConfigurationFile -Path $file -Definition $clearanceDefinition -Writer $corrupting } catch { $message = $_.Exception.Message }
            Write-Result -Ok ($null -ne $message -and $message.Contains($tamper.Key)) `
                -Name "the overlay's per-value check catches a write that changed the $($tamper.Name) (M10)" -Detail "got: $message"
        }
    } finally {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# The installer's wiring, from its AST. The behaviour is tested above through the wrapper; this checks
# the installer uses the wrapper on its upgrade branch only, passes it the product upgrade script, and
# writes true back only after the product script returned, on both paths.
$installerAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-ParallelInstanceLocal.ps1'), [ref]$null, [ref]$null)
$productCalls = @($installerAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-ProductInstaller' }, $true))
$invokeProduct = $installerAst.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-ProductInstaller' }, $true)
$ifStatement = $null -eq $invokeProduct ? $null : $invokeProduct.Body.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Extent.Text -like '*Update-ControlServerLocal.ps1*' }, $true)
$upgradeBranch = $null -eq $ifStatement ? $null : $ifStatement.Clauses[0].Item2
$firstBranch = $null -eq $ifStatement ? $null : $ifStatement.ElseClause
$wrapperCall = $null -eq $upgradeBranch ? $null : $upgradeBranch.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-ParallelProductUpgrade' }, $true)
Write-Result -Ok ($null -ne $wrapperCall -and $wrapperCall.Extent.Text.Contains("Update-ControlServerLocal.ps1")) `
    -Name 'upgrade branch: runs the product upgrade script through Invoke-ParallelProductUpgrade' -Detail 'not found'
$bareUpdate = $null -eq $upgradeBranch ? @() : @($upgradeBranch.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.CommandElements[0].Extent.Text -like '*Update-ControlServerLocal.ps1*' }, $true) |
        Where-Object { $p = $_.Parent; $inWrapper = $false; while ($p) { if ($p -eq $wrapperCall) { $inWrapper = $true; break }; $p = $p.Parent }; -not $inWrapper })
Write-Result -Ok ($bareUpdate.Count -eq 0) -Name 'upgrade branch: no call of the upgrade script outside the wrapper' -Detail ($bareUpdate | ForEach-Object { "line $($_.Extent.StartLineNumber)" })
$firstWrapper = $null -eq $firstBranch ? $null : $firstBranch.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in @('Invoke-ParallelProductUpgrade', 'Set-ParallelInstanceJourneyRuntimeDisabled') }, $true)
Write-Result -Ok ($null -ne $firstBranch -and $null -eq $firstWrapper) -Name 'first-install branch does not touch JourneyRuntime before the product script' -Detail 'found'
$orderBad = @()
foreach ($site in $productCalls) {
    $block = $site.Parent
    while ($block -and $block -isnot [System.Management.Automation.Language.StatementBlockAst] -and
        $block -isnot [System.Management.Automation.Language.NamedBlockAst]) { $block = $block.Parent }
    $setAfter = @($block.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Set-InstanceConfiguration' }, $false) |
            Where-Object { $_.Extent.StartOffset -gt $site.Extent.StartOffset })
    $setBefore = @($block.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Set-InstanceConfiguration' }, $false) |
            Where-Object { $_.Extent.StartOffset -lt $site.Extent.StartOffset })
    if ($setAfter.Count -eq 0 -or $setBefore.Count -gt 0) { $orderBad += "line $($site.Extent.StartLineNumber)" }
}
Write-Result -Ok ($productCalls.Count -eq 2 -and $orderBad.Count -eq 0) `
    -Name 'Set-InstanceConfiguration (which writes true back) runs only after the product script, on both paths' -Detail ($orderBad -join ', ')
# Review S3, the early check: before the installed definition is re-recorded, before the rollback swap,
# before either product-installer call.
$allCommands = @($installerAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
$refusalAt = @($allCommands | Where-Object { $_.GetCommandName() -eq 'Get-ParallelPreInstallRefusal' } | ForEach-Object { $_.Extent.StartOffset } | Sort-Object | Select-Object -First 1)
$recordAt = @($allCommands | Where-Object { $_.GetCommandName() -eq 'Copy-Item' -and $_.Extent.Text.Contains('InstalledDefinitionPath') } | ForEach-Object { $_.Extent.StartOffset })
$swapAt = @($allCommands | Where-Object { $_.GetCommandName() -eq 'Move-Item' } | ForEach-Object { $_.Extent.StartOffset } | Sort-Object | Select-Object -First 1)
$productAt = @($productCalls | ForEach-Object { $_.Extent.StartOffset } | Sort-Object | Select-Object -First 1)
Write-Result -Ok ($refusalAt.Count -eq 1 -and $recordAt.Count -eq 1 -and $refusalAt[0] -lt $recordAt[0] -and $refusalAt[0] -lt $swapAt[0] -and $refusalAt[0] -lt $productAt[0]) `
    -Name 'the installer runs its pre-install check (open dispatch gate, missing or unreadable configuration) before recording the definition, swapping a rollback or running a product script' `
    -Detail "refusal=$refusalAt record=$recordAt swap=$swapAt product=$productAt"
# ... unconditionally. The first version sat inside "if the service exists AND the file exists", which
# is exactly how a missing file skipped it; the function takes both facts and decides itself.
$preCall = @($allCommands | Where-Object { $_.GetCommandName() -eq 'Get-ParallelPreInstallRefusal' } | Select-Object -First 1)
$gated = $false
if ($preCall.Count -eq 1) { $a = $preCall[0].Parent; while ($a) { if ($a -is [System.Management.Automation.Language.IfStatementAst]) { $gated = $true }; $a = $a.Parent } }
Write-Result -Ok ($preCall.Count -eq 1 -and -not $gated) -Name 'the pre-install check is not inside any if: no file-exists condition can skip it' `
    -Detail "found: $($preCall.Count); inside an if: $gated"

Write-Host ''
Write-Host 'The shipped definition and the installer carry it' -ForegroundColor Cyan

$shippedVfr = $shipped.Contains('vehicleFaultRecovery') ? $shipped['vehicleFaultRecovery'] : $null
$shippedRoles = $shipped.Contains('fieldOperatorRoles') ? $shipped['fieldOperatorRoles'] : $null
Write-Result -Ok ($null -ne $shippedVfr -and $shippedVfr['enabled'] -eq $false -and $null -ne $shippedRoles -and
    $shippedRoles['onboardClearanceEntryDeclared'] -eq $false -and $shippedRoles['path'] -ceq $rosterPath) `
    -Name 'the shipped definition states phase 1 (entry off, nothing declared, roster in opsRoot)' `
    -Detail ("vehicleFaultRecovery=" + (ConvertTo-Json -InputObject $shippedVfr -Compress) + " fieldOperatorRoles=" + (ConvertTo-Json -InputObject $shippedRoles -Compress))
$namesNow = Get-ParallelInstanceName
Write-Result -Ok ($namesNow.PSObject.Properties.Name -contains 'FaultRecoveryCredentialVariable' -and $namesNow.FaultRecoveryCredentialVariable -ceq $credentialName) `
    -Name 'the instance names its own fault recovery credential variable' -Detail ("got '" + ($namesNow.PSObject.Properties.Name -contains 'FaultRecoveryCredentialVariable' ? $namesNow.FaultRecoveryCredentialVariable : '(none)') + "'")
$layoutNow = Get-ParallelInstanceLayout -Definition $baseline
Write-Result -Ok ($layoutNow.PSObject.Properties.Name -contains 'FieldOperatorRosterPath' -and
    [string] $layoutNow.FieldOperatorRosterPath -ceq [string] $baseline['fieldOperatorRoles']['path']) `
    -Name 'the layout carries the roster path (the installer creates an empty roster there, never overwrites one)' -Detail 'missing'

# The installer's wiring. The pure pieces are tested above; this checks the installer calls them,
# and takes the credential before the product script can replace the Environment it is read from.
$installerAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-ParallelInstanceLocal.ps1'), [ref]$null, [ref]$null)
$calls = @($installerAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))
$productCalls = @($calls | Where-Object { $_.GetCommandName() -eq 'Invoke-ProductInstaller' })
$resolveCalls = @($calls | Where-Object { $_.GetCommandName() -eq 'Resolve-ParallelFaultRecoveryCredential' })
$recordCall = @($calls | Where-Object { $_.GetCommandName() -eq 'Copy-Item' -and $_.Extent.Text.Contains('InstalledDefinitionPath') })
$firstProduct = @($productCalls | Sort-Object { $_.Extent.StartOffset } | Select-Object -First 1)
# Incremental review, item 4: the credential refusal says "Nothing was stopped or changed", so it must
# come before the first change -- re-recording the installed definition -- on both paths: one call,
# shared by install and rollback, ahead of it.
Write-Result -Ok ($resolveCalls.Count -eq 1 -and $recordCall.Count -eq 1 -and $productCalls.Count -eq 2 -and
    $resolveCalls[0].Extent.StartOffset -lt $recordCall[0].Extent.StartOffset -and $resolveCalls[0].Extent.StartOffset -lt $firstProduct[0].Extent.StartOffset) `
    -Name 'the credential is resolved once, for both paths, before the installed definition is recorded and before any product script' `
    -Detail ("Resolve- calls: $($resolveCalls.Count) at " + (($resolveCalls | ForEach-Object { "line $($_.Extent.StartLineNumber)" }) -join ', ') + "; record at line $($recordCall | ForEach-Object { $_.Extent.StartLineNumber })")

# These checks match the installer's text closely, and are strict on purpose. When one goes red, first
# see whether the change is an ordinary rewrite -- splatted parameters, an added log line, throwing an
# object instead of the string -- and if it is, update the check to match; do not loosen it into
# something that no longer tells a working call from a dead one.
# Incremental review, item 3: the installer's wiring, from its AST. Each injected action must run the
# command it stands for (R1-R4, R6 swapped or emptied them and every behaviour test stayed green,
# because those tests inject their own actions); the pre-install refusal must actually be thrown (R5: a
# computed refusal that is never thrown is a check that silently does nothing); and its inputs must come
# from the machine (-ServiceExists and -ServiceStatus from Get-Service, the start time from the process).
function Get-ActionCommands {
    # The command names inside one entry of an -Actions hashtable literal passed to $Function.
    param([string] $Function, [string] $Key)
    $call = $installerAst.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq $Function }, $true)
    if ($null -eq $call) { return @('(no call of ' + $Function + ')') }
    $table = $call.Find({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)
    if ($null -eq $table) { return @('(no -Actions hashtable)') }
    $pair = @($table.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq $Key })
    if ($pair.Count -ne 1) { return @("(no $Key action)") }
    return @($pair[0].Item2.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object {
            $name = $_.GetCommandName(); if ($name) { $name } else { $_.CommandElements[0].Extent.Text } })
}
$actionWiring = @(
    @{ Function = 'Invoke-ParallelProductUpgrade'; Key = 'StopService'; Expect = @('Stop-Service', 'Get-Service'); Text = "WaitForStatus('Stopped'" }
    @{ Function = 'Invoke-ParallelProductUpgrade'; Key = 'InvokeUpdate'; Expect = @("(Join-Path `$scripts 'Update-ControlServerLocal.ps1')"); Text = '-ServiceName $serviceName' }
    @{ Function = 'Invoke-ParallelProductUpgrade'; Key = 'ServiceStatus'; Expect = @('Get-Service'); Text = '-Name $serviceName' }
    @{ Function = 'Invoke-ParallelInstanceConfigurationStep'; Key = 'GetEnvironment'; Expect = @('Get-ServiceEnvironment'); Text = $null }
    @{ Function = 'Invoke-ParallelInstanceConfigurationStep'; Key = 'SetEnvironment'; Expect = @('Set-ServiceEnvironment'); Text = 'Set-ServiceEnvironment $Environment' }
    @{ Function = 'Invoke-ParallelInstanceConfigurationStep'; Key = 'RestartService'; Expect = @('Restart-Service'); Text = '-Name $serviceName' }
)
foreach ($wire in $actionWiring) {
    $commands = @(Get-ActionCommands -Function $wire.Function -Key $wire.Key)
    $absentCommands = @($wire.Expect | Where-Object { $commands -notcontains $_ })
    $call = $installerAst.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq $wire.Function }, $true)
    $pairText = ''
    if ($null -ne $call) {
        $table = $call.Find({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)
        $pairText = @($table.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq $wire.Key } | ForEach-Object { $_.Item2.Extent.Text }) -join ''
    }
    $textOk = $null -eq $wire.Text -or $pairText.Contains($wire.Text)
    Write-Result -Ok ($absentCommands.Count -eq 0 -and $textOk) -Name "wiring: $($wire.Function) -Actions.$($wire.Key) runs $($wire.Expect -join ' + ')" `
        -Detail ("commands: " + ($commands -join ', ') + "; missing: " + ($absentCommands -join ', ') + "; text '$($wire.Text)' present: $textOk")

    # Review R4: the command being there is not the command running -- a 'return' ahead of it, or an
    # 'if' around it, leaves its text in place and runs nothing (and on -Rollback reports success). So
    # no action may leave early or branch: no return/exit/throw/break/continue, no if/loop/switch/trap.
    $value = $null
    if ($null -ne $call) {
        $table = $call.Find({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)
        $value = @($table.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq $wire.Key } | ForEach-Object { $_.Item2 }) | Select-Object -First 1
    }
    $escapes = $null -eq $value ? @('(no action)') : @($value.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.ReturnStatementAst] -or $n -is [System.Management.Automation.Language.ExitStatementAst] -or
                $n -is [System.Management.Automation.Language.ThrowStatementAst] -or $n -is [System.Management.Automation.Language.BreakStatementAst] -or
                $n -is [System.Management.Automation.Language.ContinueStatementAst] -or $n -is [System.Management.Automation.Language.IfStatementAst] -or
                $n -is [System.Management.Automation.Language.LoopStatementAst] -or $n -is [System.Management.Automation.Language.SwitchStatementAst] -or
                $n -is [System.Management.Automation.Language.TrapStatementAst] }, $true) | ForEach-Object { $_.Extent.Text.Split("`n")[0].Trim() })
    Write-Result -Ok ($escapes.Count -eq 0) -Name "wiring: $($wire.Function) -Actions.$($wire.Key) runs straight through (no early exit, no branch) (R4)" `
        -Detail ("found: " + ($escapes -join ' | '))
}
# R4 again, for the one action whose whole job is one call: InvokeUpdate is exactly that call.
$upgradeCall = $installerAst.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-ParallelProductUpgrade' }, $true)
$invokeUpdate = $null
if ($null -ne $upgradeCall) {
    $table = $upgradeCall.Find({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)
    $invokeUpdate = @($table.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq 'InvokeUpdate' } | ForEach-Object { $_.Item2 }) | Select-Object -First 1
}
$updateBlock = $null -eq $invokeUpdate ? $null : $invokeUpdate.Find({ param($n) $n -is [System.Management.Automation.Language.ScriptBlockExpressionAst] }, $true)
$updateStatements = $null -eq $updateBlock ? @() : @($updateBlock.ScriptBlock.EndBlock.Statements)
$onlyCall = $updateStatements.Count -eq 1 -and $updateBlock.ScriptBlock.BeginBlock -eq $null -and $updateBlock.ScriptBlock.ProcessBlock -eq $null -and
    $updateStatements[0] -is [System.Management.Automation.Language.PipelineAst] -and
    $updateStatements[0].PipelineElements.Count -eq 1 -and $updateStatements[0].PipelineElements[0] -is [System.Management.Automation.Language.CommandAst] -and
    $updateStatements[0].PipelineElements[0].CommandElements[0].Extent.Text -like '*Update-ControlServerLocal.ps1*'
Write-Result -Ok $onlyCall -Name 'wiring: the InvokeUpdate action is exactly one statement, the call of Update-ControlServerLocal.ps1 (R4)' `
    -Detail ("statements: " + (($updateStatements | ForEach-Object { $_.Extent.Text.Split("`n")[0].Trim() }) -join ' | '))
$preCallAst = @($calls | Where-Object { $_.GetCommandName() -eq 'Get-ParallelPreInstallRefusal' } | Select-Object -First 1)
$argumentText = { param($command, [string] $name)
    $elements = $command.CommandElements
    for ($i = 0; $i -lt $elements.Count - 1; $i++) {
        if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst] -and $elements[$i].ParameterName -eq $name) { return $elements[$i + 1].Extent.Text }
    }
    return '' }
$existsText = $preCallAst.Count -eq 1 ? (& $argumentText $preCallAst[0] 'ServiceExists') : ''
$statusText = $preCallAst.Count -eq 1 ? (& $argumentText $preCallAst[0] 'ServiceStatus') : ''
$startText = $preCallAst.Count -eq 1 ? (& $argumentText $preCallAst[0] 'ProcessStartTimeUtc') : ''
$writeText = $preCallAst.Count -eq 1 ? (& $argumentText $preCallAst[0] 'ConfigurationWriteTimeUtc') : ''
Write-Result -Ok ($existsText.Contains('Get-Service -Name $serviceName') -and $statusText.Contains('Get-Service -Name $serviceName') -and $statusText.Contains('.Status')) `
    -Name 'wiring: the pre-install check''s -ServiceExists and -ServiceStatus come from Get-Service on this instance''s service' -Detail "exists: $existsText; status: $statusText"
Write-Result -Ok ($startText.Contains('Get-ParallelServiceProcessStartTimeUtc -ServiceName $serviceName') -and $writeText.Contains('GetLastWriteTimeUtc($installedConfigurationPath)')) `
    -Name 'wiring: the start time comes from the service''s process and the write time from the installed file' -Detail "start: $startText; write: $writeText"
# R5: the result is thrown, by the statement right after the call, unconditionally on a refusal.
$thrown = $false
if ($preCallAst.Count -eq 1) {
    $assignment = $preCallAst[0].Parent
    while ($assignment -and $assignment -isnot [System.Management.Automation.Language.AssignmentStatementAst]) { $assignment = $assignment.Parent }
    if ($assignment) {
        $variable = $assignment.Left.Extent.Text
        $block = $assignment.Parent
        $index = $block.Statements.IndexOf($assignment)
        $next = $index -ge 0 -and $index + 1 -lt $block.Statements.Count ? $block.Statements[$index + 1] : $null
        $thrown = $next -is [System.Management.Automation.Language.IfStatementAst] -and $next.Clauses.Count -eq 1 -and
            $next.Clauses[0].Item1.Extent.Text -ceq $variable -and $next.Clauses[0].Item2.Statements.Count -eq 1 -and
            $next.Clauses[0].Item2.Statements[0].Extent.Text -ceq "throw $variable"
    }
}
Write-Result -Ok $thrown -Name 'wiring: the pre-install refusal is thrown by the very next statement (R5: computed but not thrown is a silent no-op)' -Detail 'next statement is not "if (<refusal>) { throw <refusal> }"'
# Incremental review, item 4: the rollback's previous generation is checked before the record and the
# swap, and the rollback asks for the product script's result file as the install path does.
$previousCheck = @($installerAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and
            $n.Clauses[0].Item1.Extent.Text.Contains('$Rollback') -and $n.Clauses[0].Item1.Extent.Text.Contains("Join-Path `$previousRoot 'controlserver'") }, $true))
$swapFirst = @($calls | Where-Object { $_.GetCommandName() -eq 'Move-Item' } | Sort-Object { $_.Extent.StartOffset } | Select-Object -First 1)
Write-Result -Ok ($previousCheck.Count -eq 1 -and $recordCall.Count -eq 1 -and $previousCheck[0].Extent.StartOffset -lt $recordCall[0].Extent.StartOffset -and
    $previousCheck[0].Extent.StartOffset -lt $swapFirst[0].Extent.StartOffset -and $previousCheck[0].Clauses[0].Item2.Extent.Text.Contains('throw')) `
    -Name 'rollback: <previous>\controlserver is checked, and refused on, before the definition is recorded and before any directory is swapped' -Detail "checks found: $($previousCheck.Count)"
$rollbackBlock = $installerAst.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -ceq '$Rollback' }, $true)
$rollbackProduct = $null -eq $rollbackBlock ? $null : $rollbackBlock.Clauses[0].Item2.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-ProductInstaller' }, $true)
$resultCheck = $null -eq $rollbackBlock ? $null : $rollbackBlock.Clauses[0].Item2.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and
            $n.Clauses[0].Item1.Extent.Text.Contains('$resultPath') -and $n.Clauses[0].Item2.Extent.Text.Contains('throw') }, $true)
Write-Result -Ok ($null -ne $rollbackProduct -and $null -ne $resultCheck -and $resultCheck.Extent.StartOffset -gt $rollbackProduct.Extent.StartOffset) `
    -Name 'rollback: a missing product result file after the product script is refused, as on the install path' -Detail 'no result-file check after Invoke-ProductInstaller in the rollback branch'
$setConfig = $installerAst.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Set-InstanceConfiguration' }, $true)
$inside = $null -eq $setConfig ? @() : @($setConfig.Body.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
$needed = @('Invoke-ParallelInstanceConfigurationStep')
$absent = @($needed | Where-Object { $inside -notcontains $_ })
Write-Result -Ok ($absent.Count -eq 0) -Name 'Set-InstanceConfiguration (run by install, upgrade and rollback) goes through Invoke-ParallelInstanceConfigurationStep' `
    -Detail ("not called: " + ($absent -join ', '))

Write-Host ''
Write-Host 'Dispatch gate script (control-server#472)' -ForegroundColor Cyan

# Everything below runs on temporary files and injected actions: no service is queried, stopped or
# started, and no real database is opened except a temporary one built for the reader case.
$gateLayout = Get-ParallelInstanceLayout -Definition $baseline
$gateConfigurationPath = "$($gateLayout.InstallRoot)\appsettings.Production.json"
$gateDatabase = "$($gateLayout.DataRoot)\data\controlserver.db"

# --- Paths and service: only ever the v2 set. The script takes them from the layout of the installed
# definition, and the definition is asserted (the MVP's service or paths are refused there).
Write-Result -Ok ($gateLayout.ServiceName -ceq '8005 AGV ControlServer V2' -and $gateConfigurationPath -ceq $v2Configuration -and
    -not (Test-ParallelInstancePathIsProduction -Path $gateConfigurationPath) -and -not (Test-ParallelInstancePathIsProduction -Path $gateDatabase)) `
    -Name 'gate: the shipped definition gives the V2 service and the V2 file, neither of them production' `
    -Detail "service '$($gateLayout.ServiceName)' file $gateConfigurationPath"
foreach ($mvpCase in @(
        @{ Name = 'the MVP service name'; Edit = { param($d) $d['serviceName'] = '8005 AGV ControlServer' } }
        @{ Name = 'the MVP install root'; Edit = { param($d) $d['installRoot'] = 'C:\Program Files\8005 AGV\ControlServer' } }
        @{ Name = 'the MVP data root'; Edit = { param($d) $d['dataRoot'] = 'C:\ProgramData\8005\ControlServer' } }
    )) {
    $mvpDefinition = Copy-Definition $baseline
    & $mvpCase.Edit $mvpDefinition
    $thrown = $null
    try { $null = Assert-ParallelInstanceDefinition -Definition $mvpDefinition -AllowRiotCreateDispatch -AllowRiotForeignOrderCancel } catch { $thrown = $_.Exception.Message }
    Write-Result -Ok ($null -ne $thrown) -Name "gate: an installed definition naming $($mvpCase.Name) is refused before anything is read" -Detail 'accepted'
}
$gateScriptSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Set-ParallelDispatchGateLocal.ps1') -Raw
$gateHits = @(Find-LayoutBypass $gateScriptSource)
Write-Result -Ok ($gateHits.Count -eq 0) -Name 'Set-ParallelDispatchGateLocal.ps1 reads no path or name except through the layout' -Detail ($gateHits -join ' / ')
$gateAst = [System.Management.Automation.Language.Parser]::ParseInput($gateScriptSource, [ref]$null, [ref]$null)
$gateCommands = @($gateAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
$gateNeeded = @('Assert-ParallelInstanceDefinition', 'Get-ParallelInstanceLayout', 'Resolve-ParallelInstanceDatabasePath', 'Invoke-ParallelDispatchGateChange', 'Get-MvpFingerprint', 'Assert-MvpUntouched')
$gateAbsent = @($gateNeeded | Where-Object { $gateCommands -notcontains $_ })
$gateDirect = @($gateCommands | Where-Object { $_ -in @('Set-Content', 'Out-File', 'Restart-Service', 'Set-ParallelInstanceConfigurationFlag') })
Write-Result -Ok ($gateAbsent.Count -eq 0 -and $gateDirect.Count -eq 0) `
    -Name 'Set-ParallelDispatchGateLocal.ps1 asserts the definition, fingerprints the MVP and changes the gate only through Invoke-ParallelDispatchGateChange' `
    -Detail ("not called: $($gateAbsent -join ', ')  direct writes/restarts: $($gateDirect -join ', ')")

# --- The database the state is read from: the service's own, and only inside the V2 data root.
$dbCases = @(
    @{ Name = 'the V2 connection string'; Value = "Data Source=$gateDatabase"; Expect = $gateDatabase }
    @{ Name = 'forward slashes and other casing'; Value = 'data source=C:/ProgramData/8005/ControlServer.V2/data/controlserver.db;Mode=ReadWriteCreate'; Expect = $gateDatabase }
    @{ Name = 'an environment variable'; Value = 'Data Source=%ProgramData%\8005\ControlServer.V2\data\controlserver.db'; Expect = (Join-Path $env:ProgramData '8005\ControlServer.V2\data\controlserver.db'); DataRoot = (Join-Path $env:ProgramData '8005\ControlServer.V2') }
    @{ Name = 'the MVP database'; Value = 'Data Source=C:\ProgramData\8005\ControlServer\data\controlserver.db'; Expect = $null }
    @{ Name = 'a sibling that shares the prefix'; Value = 'Data Source=C:\ProgramData\8005\ControlServer.V2-backups\controlserver.db'; Expect = $null }
    @{ Name = 'a climb out of the data root'; Value = 'Data Source=C:\ProgramData\8005\ControlServer.V2\..\ControlServer\data\controlserver.db'; Expect = $null }
    @{ Name = 'a climb out of the data root to a non-production directory'; Value = 'Data Source=C:\ProgramData\8005\ControlServer.V2\..\Elsewhere\controlserver.db'; Expect = $null }
    @{ Name = 'no Data Source'; Value = 'Mode=ReadOnly'; Expect = $null }
)
foreach ($case in $dbCases) {
    $got = $null; $thrown = $null
    $root = $case.ContainsKey('DataRoot') ? $case.DataRoot : $gateLayout.DataRoot
    try { $got = Resolve-ParallelInstanceDatabasePath -Configuration ([ordered]@{ connectionStrings = [ordered]@{ controlServer = $case.Value } }) -DataRoot $root -ConfigurationPath $v2Configuration } catch { $thrown = $_.Exception.Message }
    Write-Result -Ok ($null -eq $case.Expect ? ($null -ne $thrown -and $thrown.Contains($v2Configuration)) : ($got -ceq $case.Expect)) `
        -Name "gate: database path from $($case.Name) -> $($case.Expect ?? 'refused, naming the file')" -Detail "got: $got thrown: $thrown"
}

# --- The server facts the refusal rests on live in the .NET suite, which CI runs (review S3): this script is not in
# CI, and the literal pins it used to carry were weaker than they read (a substring that appeared three times, a
# gate-order check that saw only two of the calls that could come first). Here only that they are still there.
$premiseTests = Get-Content -LiteralPath (Join-Path $repoRoot 'tests/ControlServer.Tests/DispatchGatePremiseArchitectureTests.cs') -Raw
$premiseNames = @('RiotOrdersAreCreatedAtExactlyOneCallSite', 'OnlyTheTwoCreatePathsReachTheCreateAttempt',
    'EachCreatePathChecksTheGateBeforeAnythingElseAwaits', 'AClosedGateWritesNothing',
    'JourneyStagesAreExactlyTheKnownSetWithCompletedTheOnlyTerminal', 'TheEngineReadsActiveJourneysAsStageNotCompleted',
    'NeverSentIsDefinedAsTheGateScriptPortsIt')
$premiseMissing = @($premiseNames | Where-Object { -not [regex]::IsMatch($premiseTests, "\[(?:Fact|Theory)\][^{]*?public void $_\(") })
Write-Result -Ok ($premiseMissing.Count -eq 0) -Name 'gate premise: the server facts are pinned by DispatchGatePremiseArchitectureTests (CI), all seven tests present' `
    -Detail "missing: $($premiseMissing -join ', ')"

# --- Test-ParallelOrderIntentNeverSent, case by case against the C# above.
function New-GateIntent {
    param([string] $UpperId, [string] $Status = 'PENDING_RECONCILIATION', $Attempts = 0L, $AttemptId = $null, $OrderId = $null,
        [string] $VehicleKey = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4', [string] $CreatedAt = '2026-10-08 08:10:00+08:00',
        $AuditVersion = 1L, $Experimental = $null)
    return [ordered]@{ MovementLegId = "leg-$UpperId"; UpperId = $UpperId; VehicleKey = $VehicleKey; Status = $Status; OrderId = $OrderId
        CreateAttemptCount = $Attempts; CreateAttemptId = $AttemptId; DispatchAuditVersion = $AuditVersion
        ExperimentalCreateAuthorizationId = $Experimental; CreatedAt = $CreatedAt }
}
function New-GateRead {
    param([string] $UpperId, [string] $Phase = 'PRE_CREATE_RECONCILIATION', [string] $Outcome = 'UNKNOWN', $AttemptId = $null, $ReturnedOrderId = $null, $ResultPresent = $null)
    return [ordered]@{ MovementLegId = "leg-$UpperId"; Phase = $Phase; Outcome = $Outcome; AttemptId = $AttemptId; ReturnedOrderId = $ReturnedOrderId; ResultPresent = $ResultPresent }
}
$neverSentCases = @(
    @{ Name = 'pending, no attempt, no order'; Intent = (New-GateIntent u1); Reads = @(); Expect = $true }
    @{ Name = 'pending with a create attempt counted'; Intent = (New-GateIntent u1 -Attempts 1L); Reads = @(); Expect = $false }
    @{ Name = 'pending with an attempt id'; Intent = (New-GateIntent u1 -AttemptId 'a1'); Reads = @(); Expect = $false }
    @{ Name = 'pending with an order id'; Intent = (New-GateIntent u1 -OrderId 'O1'); Reads = @(); Expect = $false }
    @{ Name = 'pending with an unreadable attempt count'; Intent = (New-GateIntent u1 -Attempts $null); Reads = @(); Expect = $false }
    @{ Name = 'CREATE_ATTEMPTED'; Intent = (New-GateIntent u1 -Status CREATE_ATTEMPTED -Attempts 1L -AttemptId 'a1'); Reads = @(); Expect = $false }
    @{ Name = 'CONFIRMED'; Intent = (New-GateIntent u1 -Status CONFIRMED -Attempts 1L -AttemptId 'a1' -OrderId 'O1'); Reads = @(); Expect = $false }
    @{ Name = 'TERMINAL_RECONCILIATION_REQUIRED'; Intent = (New-GateIntent u1 -Status TERMINAL_RECONCILIATION_REQUIRED -OrderId 'O1'); Reads = @(); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN after reads that answered nothing'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @((New-GateRead u1), (New-GateRead u1 -Outcome NOT_FOUND)); Expect = $true }
    @{ Name = 'RESULT_UNKNOWN with no reads'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @(); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN, a read returned an order'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @((New-GateRead u1 -ReturnedOrderId 'O9')); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN, a read had a result'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @((New-GateRead u1 -ResultPresent 1L)); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN, a post-create read'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @((New-GateRead u1 -Phase POST_CREATE_RECONCILIATION)); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN, a read with an attempt'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @((New-GateRead u1 -AttemptId 'a1')); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN, a read with another outcome'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @((New-GateRead u1 -Outcome ACTIVE)); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN on the experimental path'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN -Experimental 'x1'); Reads = @((New-GateRead u1)); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN under audit version 2'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN -AuditVersion 2L); Reads = @((New-GateRead u1)); Expect = $false }
    @{ Name = 'RESULT_UNKNOWN, reads of another leg only'; Intent = (New-GateIntent u1 -Status RESULT_UNKNOWN); Reads = @((New-GateRead u2)); Expect = $false }
)
foreach ($case in $neverSentCases) {
    $got = Test-ParallelOrderIntentNeverSent -Intent $case.Intent -AuditEvents $case.Reads
    Write-Result -Ok ($got -eq $case.Expect) -Name "never sent: $($case.Name) -> $($case.Expect)" -Detail "got $got"
}

# --- The refusal, both directions.
function New-GateJourney {
    param([string] $Id, [string] $Stage = 'AwaitingPickupArrival', [string] $Pickup = $null, [string] $Gate = $null,
        [string] $CreatedAt = '2026-10-08 08:00:00+08:00', [string] $VehicleKey = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4')
    return [ordered]@{ JourneyId = $Id; Stage = $Stage; AgvId = '老厂前线新多仓位2'; VehicleKey = $VehicleKey; PickupUpperId = $Pickup; GateUpperId = $Gate; CreatedAt = $CreatedAt }
}
function New-GateState { param($Journeys = @(), $Intents = @(), $Reads = @()) return [ordered]@{ Journeys = @($Journeys); OrderIntents = @($Intents); AuditEvents = @($Reads) } }
$otherVehicle = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
# The state a close leaves and the journeys that come after it: the last journey before the close is
# Completed, its orders CONFIRMED; every journey since was accepted with the gate closed, so its orders
# were never created -- one still pending, one RESULT_UNKNOWN from an SDK timeout before its create.
$afterClose = New-GateState -Journeys @(
    (New-GateJourney j-before -Stage Completed -Pickup p0 -Gate g0 -CreatedAt '2026-10-08 07:00:00+08:00')
    (New-GateJourney j-waiting -Stage Blocked -Pickup p1 -CreatedAt '2026-10-08 09:00:00+08:00')
    (New-GateJourney j-timeout -Stage AwaitingPickupArrival -Pickup p2 -CreatedAt '2026-10-08 09:05:00+08:00' -VehicleKey $otherVehicle)
) -Intents @(
    (New-GateIntent p0 -Status CONFIRMED -Attempts 1L -AttemptId a0 -OrderId O0 -CreatedAt '2026-10-08 07:00:01+08:00')
    (New-GateIntent g0 -Status CONFIRMED -Attempts 1L -AttemptId a1 -OrderId O1 -CreatedAt '2026-10-08 07:20:00+08:00')
    (New-GateIntent p1 -CreatedAt '2026-10-08 09:00:00+08:00')
    (New-GateIntent p2 -Status RESULT_UNKNOWN -VehicleKey $otherVehicle -CreatedAt '2026-10-08 09:05:00+08:00')
) -Reads @((New-GateRead p2))
$gateRefusalCases = @(
    @{ Name = 'close, no journey'; Direction = 'Close'; State = (New-GateState); Expect = $null }
    @{ Name = 'close, only Completed journeys'; Direction = 'Close'; State = (New-GateState -Journeys @((New-GateJourney j1 -Stage Completed))); Expect = $null }
    @{ Name = 'close, a journey under way'; Direction = 'Close'; State = (New-GateState -Journeys @((New-GateJourney j1 -Stage AwaitingGateArrival))); Expect = 'GATE_CLOSE_REFUSED_IN_FLIGHT' }
    @{ Name = 'close, a Blocked journey (active, not terminal)'; Direction = 'Close'; State = (New-GateState -Journeys @((New-GateJourney j1 -Stage Blocked))); Expect = 'GATE_CLOSE_REFUSED_IN_FLIGHT' }
    @{ Name = 'close, a journey waiting for the gate with no order sent'; Direction = 'Close'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1)) -Intents @((New-GateIntent p1))); Expect = 'GATE_CLOSE_REFUSED_IN_FLIGHT' }
    @{ Name = 'close, state unreadable'; Direction = 'Close'; State = [ordered]@{ Error = 'no such table: JourneyRuntimes' }; Expect = 'GATE_STATE_UNREADABLE' }
    @{ Name = 'close, no state'; Direction = 'Close'; State = $null; Expect = 'GATE_STATE_UNREADABLE' }
    @{ Name = 'open, state unreadable'; Direction = 'Open'; State = [ordered]@{ Error = 'database is locked' }; Expect = 'GATE_STATE_UNREADABLE' }
    @{ Name = 'open, no journey'; Direction = 'Open'; State = (New-GateState); Expect = $null }
    @{ Name = 'open, only journeys waiting for the gate (never-sent orders)'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Stage Blocked -Pickup p1)) -Intents @((New-GateIntent p1))); Expect = $null }
    @{ Name = 'open, the journeys after a close (a timeout left one RESULT_UNKNOWN, never sent)'; Direction = 'Open'; State = $afterClose; Expect = $null }
    @{ Name = 'open, a journey whose named order is CONFIRMED (vehicle may be moving)'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1)) -Intents @((New-GateIntent p1 -Status CONFIRMED -Attempts 1L -AttemptId a1 -OrderId O1))); Expect = 'GATE_OPEN_REFUSED_ORDER_SENT' }
    @{ Name = 'open, the named order dated before the journey row'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1)) -Intents @((New-GateIntent p1 -Status CREATE_ATTEMPTED -Attempts 1L -AttemptId a1 -CreatedAt '2026-10-08 07:59:59+08:00'))); Expect = 'GATE_OPEN_REFUSED_ORDER_SENT' }
    @{ Name = 'open, an unnamed order for the vehicle created during the journey (a later stop, a rebuild)'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1)) -Intents @((New-GateIntent p1), (New-GateIntent s2 -Status RESULT_UNKNOWN -Attempts 1L -AttemptId a2 -CreatedAt '2026-10-08 08:30:00+08:00'))); Expect = 'GATE_OPEN_REFUSED_ORDER_SENT' }
    @{ Name = 'open, the same instant in another offset counts as during the journey'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1)) -Intents @((New-GateIntent p1), (New-GateIntent s2 -Status CONFIRMED -OrderId O2 -CreatedAt '2026-10-08 00:00:00+00:00'))); Expect = 'GATE_OPEN_REFUSED_ORDER_SENT' }
    @{ Name = 'open, a CONFIRMED order of another vehicle'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1)) -Intents @((New-GateIntent p1), (New-GateIntent x1 -Status CONFIRMED -OrderId OX -VehicleKey $otherVehicle))); Expect = $null }
    @{ Name = 'open, a journey with an unreadable CreatedAt'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1 -CreatedAt 'yesterday-ish')) -Intents @((New-GateIntent p1))); Expect = 'GATE_OPEN_REFUSED_ORDER_SENT' }
    @{ Name = 'open, an order for the vehicle with an unreadable CreatedAt'; Direction = 'Open'; State = (New-GateState -Journeys @((New-GateJourney j1 -Pickup p1)) -Intents @((New-GateIntent p1), (New-GateIntent s2 -CreatedAt ''))); Expect = 'GATE_OPEN_REFUSED_ORDER_SENT' }
)
foreach ($case in $gateRefusalCases) {
    # A throw is a failure of this case, not of the run: the refusal must answer, never break.
    try { $got = Get-ParallelDispatchGateRefusal -Direction $case.Direction -State $case.State -ServiceName $v2Service -DatabasePath $gateDatabase } catch { $got = "THREW: $($_.Exception.Message)" }
    $named = $null -eq $got -or ($got.Contains($v2Service) -and $got.Contains($gateDatabase) -and $got.EndsWith(' Nothing was stopped or changed.'))
    Write-Result -Ok ($named -and ($null -eq $case.Expect ? ($null -eq $got) : ($null -ne $got -and $got.StartsWith("$($case.Expect):")))) `
        -Name "gate refusal: $($case.Name) -> $($case.Expect ?? 'allowed')" -Detail "got: $got"
}

# --- The writer: section ignoring case, the Chinese agvId kept byte for byte, every read-back live.
$gateInstalled = Merge-ConfigurationTree -Base (Copy-Definition $productConfiguration) -Overlay (New-ParallelInstanceConfigurationOverlay -Definition $baseline)
$gateInstalled['RiotCreateDispatch'] = [ordered]@{ enabled = $true }
$gateInstalledText = ConvertTo-Json -InputObject $gateInstalled -Depth 12
$agvIdBytes = [Text.Encoding]::UTF8.GetBytes('"老厂前线新多仓位2"')
function Test-ByteRun { param([byte[]] $Haystack, [byte[]] $Needle)
    for ($i = 0; $i -le $Haystack.Length - $Needle.Length; $i++) {
        $hit = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) { if ($Haystack[$i + $j] -ne $Needle[$j]) { $hit = $false; break } }
        if ($hit) { return $true }
    }
    return $false
}
Invoke-PathCase 'gate writer' {
    $directory = Join-Path ([IO.Path]::GetTempPath()) "cs472-writer-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory | Out-Null
    try {
        $file = Join-Path $directory 'appsettings.Production.json'
        [IO.File]::WriteAllText($file, ($gateInstalledText -replace '"RiotCreateDispatch"', '"riotCreateDispatch"' -replace '"enabled": true', '"Enabled": true'), [Text.UTF8Encoding]::new($false))
        $before = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
        $previous = Set-ParallelInstanceConfigurationFlag -Path $file -Section 'RiotCreateDispatch' -Value $false
        $bytes = [IO.File]::ReadAllBytes($file)
        $after = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
        $sections = @($after.Keys | Where-Object { $_ -ieq 'RiotCreateDispatch' })
        $others = @($before.Keys | Where-Object { $_ -ine 'RiotCreateDispatch' } |
                Where-Object { (ConvertTo-Json $before[$_] -Compress -Depth 12) -cne (ConvertTo-Json $after[$_] -Compress -Depth 12) })
        Write-Result -Ok ($previous -eq $true -and $sections.Count -eq 1 -and @($after[$sections[0]].Keys).Count -eq 1 -and $after[$sections[0]]['enabled'] -eq $false -and $others.Count -eq 0) `
            -Name 'gate writer: riotCreateDispatch.Enabled (other casing) becomes one enabled=false, nothing else changes' `
            -Detail ("previous $previous sections $($sections -join ',') keys $(@($after[$sections[0]].Keys) -join ',') changed: $($others -join ',')")
        Write-Result -Ok ((Test-ByteRun $bytes $agvIdBytes) -and -not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)) `
            -Name 'gate writer: the Chinese agvId is in the file as its UTF-8 bytes, no BOM' -Detail "length $($bytes.Length)"
        $null = Set-ParallelInstanceConfigurationFlag -Path $file -Section 'RiotCreateDispatch' -Value $true
        Write-Result -Ok ((Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).RiotCreateDispatch.enabled -eq $true -and (Test-ByteRun ([IO.File]::ReadAllBytes($file)) $agvIdBytes)) `
            -Name 'gate writer: opening is symmetric (enabled=true), agvId still intact' -Detail (Get-Content -LiteralPath $file -Raw)

        $writerCases = @(
            @{ Name = 'a lost write'; Writer = { param($Path, $Text) }; Expect = 'RiotCreateDispatch.enabled in' }
            @{ Name = 'a re-encoding writer (GB18030, as a code-page editor would)'; Writer = { param($Path, $Text) [IO.File]::WriteAllText($Path, $Text, [Text.Encoding]::GetEncoding('GB18030')) }; Expect = 'does not read back as written' }
            @{ Name = 'a re-encoding writer (Latin-1)'; Writer = { param($Path, $Text) [IO.File]::WriteAllText($Path, $Text, [Text.Encoding]::Latin1) }; Expect = 'does not read back as written' }
            @{ Name = 'a writer that changes the agvId and keeps the flag'; Writer = { param($Path, $Text) [IO.File]::WriteAllText($Path, $Text.Replace('老厂前线新多仓位2', '老厂前线新多仓位1'), [Text.UTF8Encoding]::new($false)) }; Expect = 'does not read back as written' }
            @{ Name = 'a writer that writes the flag as a string'; Writer = { param($Path, $Text) [IO.File]::WriteAllText($Path, ($Text -replace '"enabled": false', '"enabled": "false"'), [Text.UTF8Encoding]::new($false)) }; Expect = 'RiotCreateDispatch.enabled in' }
        )
        foreach ($case in $writerCases) {
            [IO.File]::WriteAllText($file, $gateInstalledText, [Text.UTF8Encoding]::new($false))
            $message = $null
            try { $null = Set-ParallelInstanceConfigurationFlag -Path $file -Section 'RiotCreateDispatch' -Value $false -Writer $case.Writer } catch { $message = $_.Exception.Message }
            Write-Result -Ok ($null -ne $message -and $message.Contains($case.Expect) -and $message.Contains($file)) -Name "gate writer: $($case.Name) is caught by the read-back" -Detail "got: $message"
        }
        [IO.File]::WriteAllText($file, '{"RiotCreateDispatch":{"enabled":true},"riotcreatedispatch":{"enabled":true}}', [Text.UTF8Encoding]::new($false))
        $message = $null
        try { $null = Set-ParallelInstanceConfigurationFlag -Path $file -Section 'RiotCreateDispatch' -Value $false } catch { $message = $_.Exception.Message }
        Write-Result -Ok ($null -ne $message -and $message.Contains('2 RiotCreateDispatch sections')) -Name 'gate writer: two sections differing in case are refused' -Detail "got: $message"
    } finally {
        Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- The whole change, with recorded actions in place of the service.
$gateStarted = [datetime]::new(2026, 10, 8, 0, 0, 0, [DateTimeKind]::Utc)
function Invoke-GateCase {
    <#
        Runs Invoke-ParallelDispatchGateChange on a temporary file with recorded actions. -States is the
        sequence ReadState returns; -StartTimes the sequence ProcessStartTimeUtc returns (a scriptblock
        entry is evaluated, so "after the write" can be computed from the file).
    #>
    param([string] $Direction, [string] $Text, $Status = 'Running', [object[]] $States = @(), [object[]] $StartTimes = @(),
        [string] $ServiceName = $v2Service, [string] $ConfigurationName = 'appsettings.Production.json', [scriptblock] $Writer,
        [string] $StatusAfter = 'Running', [datetime] $WriteTime = $gateStarted.AddMinutes(-5), [string] $ConfigurationPath)
    $directory = Join-Path ([IO.Path]::GetTempPath()) "cs472-gate-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $directory | Out-Null
    $file = $ConfigurationPath ? $ConfigurationPath : (Join-Path $directory $ConfigurationName)
    if (-not $ConfigurationPath -and $null -ne $Text) {
        [IO.File]::WriteAllText($file, $Text, [Text.UTF8Encoding]::new($false))
        [IO.File]::SetLastWriteTimeUtc($file, $WriteTime)
    }
    $calls = [System.Collections.Generic.List[string]]::new()
    $stateQueue = [System.Collections.Generic.Queue[object]]::new([object[]] $States)
    $startQueue = [System.Collections.Generic.Queue[object]]::new([object[]] $StartTimes)
    $current = @{ Status = $Status }
    $actions = @{
        ServiceStatus = { $calls.Add('Status'); $current.Status }.GetNewClosure()
        ReadState = { $calls.Add('ReadState'); $stateQueue.Count ? $stateQueue.Dequeue() : $null }.GetNewClosure()
        StopService = { $calls.Add('Stop'); $current.Status = 'Stopped' }.GetNewClosure()
        StartService = { $calls.Add('Start'); $current.Status = $StatusAfter }.GetNewClosure()
        ProcessStartTimeUtc = { $calls.Add('StartTime'); $next = $startQueue.Count ? $startQueue.Dequeue() : $null; $next -is [scriptblock] ? (& $next $file) : $next }.GetNewClosure()
    }
    # A -ConfigurationPath case is a production path: it is never read or written here, only handed over.
    $own = -not $ConfigurationPath
    $bytesBefore = $own -and (Test-Path -LiteralPath $file) ? [IO.File]::ReadAllBytes($file) : $null
    $result = $null; $thrown = $null
    try {
        $arguments = @{ Direction = $Direction; ConfigurationPath = $file; ServiceName = $ServiceName; DatabasePath = $gateDatabase; Actions = $actions }
        if ($Writer) { $arguments['Writer'] = $Writer }
        $result = Invoke-ParallelDispatchGateChange @arguments
    } catch { $thrown = $_.Exception.Message }
    $bytesAfter = $own -and (Test-Path -LiteralPath $file) ? [IO.File]::ReadAllBytes($file) : $null
    $unchanged = ($null -eq $bytesBefore -and $null -eq $bytesAfter) -or ($null -ne $bytesBefore -and $null -ne $bytesAfter -and [Linq.Enumerable]::SequenceEqual([byte[]] $bytesBefore, [byte[]] $bytesAfter))
    $flag = $null
    if ($null -ne $bytesAfter) { try { $flag = ([Text.Encoding]::UTF8.GetString($bytesAfter) | ConvertFrom-Json).RiotCreateDispatch.enabled } catch { } }
    Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Result = $result; Thrown = $thrown; Calls = ($calls -join ','); Unchanged = $unchanged; Flag = $flag; File = $file }
}
$openConfig = $gateInstalledText
$closedTree = Copy-Definition $gateInstalled; $closedTree['RiotCreateDispatch'] = [ordered]@{ enabled = $false }
$closedConfig = ConvertTo-Json -InputObject $closedTree -Depth 12
$idle = New-GateState
$busy = New-GateState -Journeys @((New-GateJourney j1 -Stage AwaitingGateArrival -Pickup p1)) -Intents @((New-GateIntent p1 -Status CONFIRMED -Attempts 1L -AttemptId a1 -OrderId O1))
$waiting = New-GateState -Journeys @((New-GateJourney j1 -Stage Blocked -Pickup p1)) -Intents @((New-GateIntent p1))
$afterWrite = { param($Path) [IO.File]::GetLastWriteTimeUtc($Path).AddSeconds(2) }
$beforeWrite = { param($Path) [IO.File]::GetLastWriteTimeUtc($Path).AddSeconds(-2) }
$nothingSuffix = 'Nothing was stopped or changed.'

$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted, $afterWrite)
Write-Result -Ok ($null -eq $r.Thrown -and $r.Result.Changed -and $r.Flag -eq $false -and $r.Calls -ceq 'Status,StartTime,ReadState,Stop,ReadState,Start,Status,StartTime') `
    -Name 'gate change: close, nothing in flight -> read, stop, read again, write, start, verified' -Detail "calls $($r.Calls) flag $($r.Flag) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($busy) -StartTimes @($gateStarted)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_CLOSE_REFUSED_IN_FLIGHT:') -and $r.Thrown.EndsWith($nothingSuffix) -and $r.Unchanged -and $r.Calls -notmatch 'Stop|Start(?!Time)') `
    -Name 'gate change: close with a journey in flight -> refused before the service is stopped, file unchanged' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $busy) -StartTimes @($gateStarted)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('AFTER_STOP GATE_CLOSE_REFUSED_IN_FLIGHT:') -and $r.Thrown.Contains('The configuration was not changed.') -and
    $r.Thrown.Contains('started again') -and -not $r.Thrown.Contains($nothingSuffix) -and $r.Unchanged -and $r.Calls -ceq 'Status,StartTime,ReadState,Stop,ReadState,Start,Status') `
    -Name 'gate change: a journey that appears between the two reads -> refused after the stop, service started again on the unchanged file, and said so' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @([ordered]@{ Error = 'unable to open database file' }) -StartTimes @($gateStarted)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_STATE_UNREADABLE:') -and $r.Unchanged -and $r.Calls -notmatch 'Stop') `
    -Name 'gate change: unreadable journey state -> refused, nothing stopped or written' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, [ordered]@{ Error = 'disk I/O error' }) -StartTimes @($gateStarted)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('AFTER_STOP GATE_STATE_UNREADABLE:') -and $r.Unchanged -and $r.Calls.EndsWith('Stop,ReadState,Start,Status')) `
    -Name 'gate change: state unreadable after the stop -> refused, service started again, file unchanged' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -Status $null -States @($idle)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_SERVICE_MISSING:') -and $r.Thrown.Contains($v2Service) -and $r.Unchanged) `
    -Name 'gate change: no V2 service -> refused, naming it' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -ServiceName '8005 AGV ControlServer' -States @($idle, $idle) -StartTimes @($gateStarted, $afterWrite)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_REFUSED_PRODUCTION:') -and $r.Unchanged -and $r.Calls -ceq '') `
    -Name 'gate change: the MVP service name -> refused before any action' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -ConfigurationPath $mvpConfiguration -States @($idle, $idle) -StartTimes @($gateStarted, $afterWrite)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_REFUSED_PRODUCTION:') -and $r.Calls -ceq '') `
    -Name 'gate change: the MVP configuration path -> refused before any action (nothing is read there)' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted, $beforeWrite)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_RESTART_UNVERIFIED:') -and $r.Flag -eq $false) `
    -Name 'gate change: a process that did not start after the write -> GATE_RESTART_UNVERIFIED' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted, $null)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_RESTART_UNVERIFIED:') -and $r.Thrown.Contains('an unknown time')) `
    -Name 'gate change: a process start time that cannot be read after the restart -> GATE_RESTART_UNVERIFIED' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted, $afterWrite) -StatusAfter 'StartPending'
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_RESTART_UNVERIFIED:') -and $r.Thrown.Contains("'StartPending'")) `
    -Name 'gate change: a service that is not Running after the start -> GATE_RESTART_UNVERIFIED' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted) -Writer { param($Path, $Text) [IO.File]::WriteAllText($Path, $Text, [Text.Encoding]::GetEncoding('GB18030')) }
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_WRITE_FAILED:') -and $r.Thrown.Contains('put back and checked') -and $r.Unchanged -and $r.Calls.EndsWith('Stop,ReadState,Start,Status') -and $r.Thrown.Contains('started again and is Running')) `
    -Name 'gate change: a write that does not read back -> original bytes restored, service started again, gate as it was' -Detail "calls $($r.Calls) unchanged $($r.Unchanged) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $closedConfig -States @($busy) -StartTimes @($gateStarted)
Write-Result -Ok ($null -eq $r.Thrown -and -not $r.Result.Changed -and $r.Unchanged -and $r.Calls -notmatch 'ReadState|Stop|Start(?!Time)') `
    -Name 'gate change: already closed -> nothing touched' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -Status 'Stopped' -States @($idle, $idle)
Write-Result -Ok ($null -eq $r.Thrown -and $r.Result.Changed -and $r.Flag -eq $false -and $r.Calls -ceq 'Status,ReadState,ReadState') `
    -Name 'gate change: a stopped service -> both reads, the write, and the service left stopped' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted.AddMinutes(-10), $afterWrite)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('CONFIGURATION_CHANGED_SINCE_START:') -and $r.Unchanged -and $r.Calls -notmatch 'ReadState|Stop') `
    -Name 'gate change: a file written after the running process started -> refused (the installer''s first check)' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -Status 'StopPending' -States @($idle, $idle)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('SERVICE_NOT_SETTLED:') -and $r.Unchanged) -Name 'gate change: a service mid-transition -> refused' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $null -States @($idle, $idle) -StartTimes @($gateStarted)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('INSTALLED_CONFIGURATION_MISSING:')) -Name 'gate change: no installed configuration -> refused' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Open -Text $closedConfig -States @($waiting, $waiting) -StartTimes @($gateStarted, $afterWrite)
Write-Result -Ok ($null -eq $r.Thrown -and $r.Result.Changed -and $r.Flag -eq $true -and $r.Calls -ceq 'Status,StartTime,ReadState,Stop,ReadState,Start,Status,StartTime') `
    -Name 'gate change: open with journeys waiting for the gate -> opened, restarted, verified (they can go on)' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Open -Text $closedConfig -States @($busy) -StartTimes @($gateStarted)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_OPEN_REFUSED_ORDER_SENT:') -and $r.Unchanged -and $r.Calls -notmatch 'Stop') `
    -Name 'gate change: open with a journey whose order was sent -> refused before the stop' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Open -Text $closedConfig -States @($afterClose, $afterClose) -StartTimes @($gateStarted, $afterWrite)
Write-Result -Ok ($null -eq $r.Thrown -and $r.Flag -eq $true) `
    -Name 'gate change: open on the state a close leaves plus the journeys accepted since -> opened' -Detail "thrown $($r.Thrown)"

# --- The reader's columns exist in the real schema. The temporary database below is written by this test,
# so it proves the reads work, not that the names are the service's: those are checked here, against the
# EF model snapshot the migrations are generated from, for every column the reader's SQL names.
$snapshot = Get-Content -LiteralPath (Join-Path $repoRoot 'src/ControlServer.Infrastructure/Persistence/Migrations/ControlServerDbContextModelSnapshot.cs') -Raw
$hostSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ParallelHost.psm1') -Raw
$readerAst = [System.Management.Automation.Language.Parser]::ParseInput($hostSource, [ref]$null, [ref]$null).Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-ParallelJourneyDispatchState' }, $true)
$readerSql = [regex]::Replace($readerAst.Body.Extent.Text, "['""]\s*\+\s*['""]", '')
$selects = @([regex]::Matches($readerSql, '(?s)SELECT (.+?) FROM (\w+)'))
$schemaGaps = @()
foreach ($select in $selects) {
    $table = $select.Groups[2].Value
    $entity = [regex]::Match($snapshot, "(?s)modelBuilder\.Entity\(""[\w.]+"", b =>(?:(?!modelBuilder\.Entity\().)*?b\.ToTable\(""$table""\)").Value
    $columns = @([regex]::Matches($entity, 'b\.Property<[^>]+>\("(\w+)"\)') | ForEach-Object { $_.Groups[1].Value })
    if ($columns.Count -eq 0) { $schemaGaps += "table $table not found in the snapshot"; continue }
    foreach ($column in ($select.Groups[1].Value -split ',' | ForEach-Object { $_.Trim() })) {
        if ($columns -cnotcontains $column) { $schemaGaps += "$table.$column" }
    }
}
Write-Result -Ok ($selects.Count -eq 4 -and $schemaGaps.Count -eq 0) -Name 'gate reader: every column its SQL selects exists in the EF model snapshot (JourneyRuntimes, OrderIntents, RiotDispatchAuditEvents)' `
    -Detail "selects found: $($selects.Count) missing: $($schemaGaps -join ', ')"

# --- Review N3: a process that started at the very instant of the write is not shown to have read it.
$atWrite = { param($Path) [IO.File]::GetLastWriteTimeUtc($Path) }
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted, $atWrite)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_RESTART_UNVERIFIED:')) `
    -Name 'gate change: a process start time equal to the write time -> GATE_RESTART_UNVERIFIED (strictly after is required)' -Detail "thrown $($r.Thrown)"

# --- Review N4: a Stopped service is not touched, so its refusal is the plain one; a service started again after a
# refusal is reported as it is, not as it was hoped to be.
$r = Invoke-GateCase -Direction Close -Text $openConfig -Status 'Stopped' -States @($idle, $busy)
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_CLOSE_REFUSED_IN_FLIGHT:') -and $r.Thrown.EndsWith($nothingSuffix) -and $r.Unchanged -and $r.Calls -ceq 'Status,ReadState,ReadState') `
    -Name 'gate change: a Stopped service refused on the second read -> no AFTER_STOP, "nothing was stopped or changed" holds, nothing started' -Detail "calls $($r.Calls) thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $busy) -StartTimes @($gateStarted) -StatusAfter 'Stopped'
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('AFTER_STOP ') -and $r.Thrown.Contains("asked to start again but is 'Stopped'") -and -not $r.Thrown.Contains('started again and is Running')) `
    -Name 'gate change: a service that does not come back after a refusal after the stop -> the refusal says so' -Detail "thrown $($r.Thrown)"
$r = Invoke-GateCase -Direction Close -Text $openConfig -States @($idle, $idle) -StartTimes @($gateStarted) -StatusAfter 'StartPending' -Writer { param($Path, $Text) }
Write-Result -Ok ($r.Thrown -and $r.Thrown.StartsWith('GATE_WRITE_FAILED:') -and $r.Thrown.Contains("asked to start again but is 'StartPending'")) `
    -Name 'gate change: a service that does not come back after a failed write -> the failure says so' -Detail "thrown $($r.Thrown)"

# --- Review S5: the open refusal names the way out, in order, with the V2 service and not the MVP's.
$sentRefusal = Get-ParallelDispatchGateRefusal -Direction Open -State $busy -ServiceName $v2Service -DatabasePath $gateDatabase
$riotAt = $sentRefusal.IndexOf('in RIoT, confirm that no order is running'); $handAt = $sentRefusal.IndexOf('section 10 of remote-ops/factory-server/docs/wire-to-gate-parallel-cd.md')
$restartAt = $sentRefusal.IndexOf("restart '$v2Service' only -- never the MVP's service")
Write-Result -Ok ($riotAt -ge 0 -and $handAt -gt $riotAt -and $restartAt -gt $handAt -and $sentRefusal.Contains('RiotCreateDispatch.enabled to true')) `
    -Name 'gate refusal: an open refused for a sent order gives the way out: RIoT first, then the hand edit (section 10), then the V2 restart only' -Detail "got: $sentRefusal"

# --- Review S2: -State maps to the direction in one place, and the script takes it from there and reports what was done.
Write-Result -Ok ((ConvertTo-ParallelGateDirection -State Closed) -ceq 'Close' -and (ConvertTo-ParallelGateDirection -State Open) -ceq 'Open') `
    -Name 'gate: -State Closed is Close and -State Open is Open' -Detail "Closed -> $(ConvertTo-ParallelGateDirection -State Closed), Open -> $(ConvertTo-ParallelGateDirection -State Open)"
$directionAssignments = @($gateAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ieq '$direction' }, $true))
Write-Result -Ok ($directionAssignments.Count -eq 1 -and $directionAssignments[0].Right.Extent.Text -ceq 'ConvertTo-ParallelGateDirection -State $State') `
    -Name 'Set-ParallelDispatchGateLocal.ps1 takes its direction only from ConvertTo-ParallelGateDirection -State $State' `
    -Detail ("assignments: " + (($directionAssignments | ForEach-Object { $_.Extent.Text }) -join ' | '))
$passLine = @($gateAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -or $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true) |
        Where-Object { $_.Value -like 'PASS:*' })
$passText = ($passLine | ForEach-Object { $_.Extent.Text }) -join ' '
Write-Result -Ok ($passLine.Count -eq 1 -and $passText.Contains('$result.Previous') -and $passText.Contains('$result.Now') -and -not $passText.Contains('$State')) `
    -Name 'Set-ParallelDispatchGateLocal.ps1 builds its PASS line from the result (Previous, Now), not from -State' -Detail "PASS line: $passText"
$mismatch = $gateAst.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -ceq "`$result.Now -ne (`$State -eq 'Open')" }, $true)
Write-Result -Ok ($null -ne $mismatch -and $mismatch.Clauses[0].Item2.Extent.Text.Contains('throw "GATE_STATE_MISMATCH')) `
    -Name 'Set-ParallelDispatchGateLocal.ps1 throws GATE_STATE_MISMATCH when the gate is not what -State asked for' -Detail 'no such check'

# --- Review S1: the dry run's branch. Its condition is exactly "not ShouldProcess", it returns, and nothing in it
# changes the machine; and it comes before the change.
$whatIf = @($gateAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text.Contains('ShouldProcess') }, $true))
$whatIfOk = $false; $whatIfDetail = "branches mentioning ShouldProcess: $($whatIf.Count)"
if ($whatIf.Count -eq 1) {
    $condition = $whatIf[0].Clauses[0].Item1
    $pipeline = $condition -is [System.Management.Automation.Language.PipelineAst] -and $condition.PipelineElements.Count -eq 1 ? $condition.PipelineElements[0].Expression : $null
    $notShouldProcess = $pipeline -is [System.Management.Automation.Language.UnaryExpressionAst] -and $pipeline.TokenKind -eq 'Not' -and
        $pipeline.Child -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $pipeline.Child.Member.Extent.Text -ceq 'ShouldProcess' -and
        $pipeline.Child.Expression.Extent.Text -ceq '$PSCmdlet'
    $body = $whatIf[0].Clauses[0].Item2
    $commands = @($body.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object { $_.GetCommandName() })
    $changing = @($commands | Where-Object { $_ -eq 'Invoke-ParallelDispatchGateChange' -or $_ -like 'Stop-*' -or $_ -like 'Start-*' -or $_ -like 'Restart-*' -or $_ -like 'Set-*' -or $_ -like 'Remove-*' -or $_ -like 'New-*' -or $null -eq $_ })
    $returns = @($body.Statements | Where-Object { $_ -is [System.Management.Automation.Language.ReturnStatementAst] }).Count -eq 1
    $changeCall = $gateAst.Find({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-ParallelDispatchGateChange' }, $true)
    $before = $null -ne $changeCall -and $whatIf[0].Extent.EndOffset -lt $changeCall.Extent.StartOffset
    $whatIfOk = $notShouldProcess -and $changing.Count -eq 0 -and $returns -and $before
    $whatIfDetail = "condition '$($condition.Extent.Text)' notShouldProcess=$notShouldProcess changing=[$($changing -join ',')] returns=$returns beforeChange=$before"
}
Write-Result -Ok $whatIfOk -Name 'Set-ParallelDispatchGateLocal.ps1: the -WhatIf branch is exactly "-not $PSCmdlet.ShouldProcess(...)", returns, changes nothing, and comes before the change' `
    -Detail $whatIfDetail

# --- Review N1: the reader opens the store read-only. Without a database to write to there is nothing to observe
# (it reads only, and a missing file is refused before opening), so the connection string itself is pinned.
$readerConnections = @($readerAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
            $n.Expression.Extent.Text -ceq '[Microsoft.Data.Sqlite.SqliteConnection]' -and $n.Member.Extent.Text -ceq 'new' }, $true))
$readerConnectionText = ($readerConnections | ForEach-Object { $_.Arguments[0].Extent.Text }) -join ' | '
Write-Result -Ok ($readerConnections.Count -eq 1 -and $readerConnectionText -match '(^|;)Mode=ReadOnly(;|"$)' -and $readerConnectionText -notmatch 'ReadWrite') `
    -Name 'gate reader: the one SqliteConnection it opens is Mode=ReadOnly' -Detail "connections: $readerConnectionText"

# --- The reader, on a real SQLite file, when a ControlServer build is there to borrow the provider from.
$hostBuild = @('Release', 'Debug') | ForEach-Object { Join-Path $repoRoot "src/ControlServer.Host/bin/$_/net8.0/win-x64"; Join-Path $repoRoot "src/ControlServer.Host/bin/$_/net8.0" } |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Microsoft.Data.Sqlite.dll') } | Select-Object -First 1
if (-not $hostBuild) {
    Write-Host '  SKIP  gate reader on a real SQLite file: no ControlServer.Host build under src/ControlServer.Host/bin (build it to run this case)' -ForegroundColor Yellow
} else {
    Invoke-PathCase 'gate reader' {
        $directory = Join-Path ([IO.Path]::GetTempPath()) "cs472-db-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $directory | Out-Null
        try {
            $db = Join-Path $directory 'controlserver.db'
            $missing = Get-ParallelJourneyDispatchState -DatabasePath $db -AssemblyDirectory $hostBuild
            Write-Result -Ok ($missing.Contains('Error') -and -not (Test-Path -LiteralPath $db)) -Name 'gate reader: a missing database is an error, and is not created' -Detail (ConvertTo-Json $missing -Compress)
            # The test writes the file itself, with the same provider the reader borrows.
            foreach ($assembly in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
                Add-Type -LiteralPath (Join-Path $hostBuild $assembly) -ErrorAction SilentlyContinue
            }
            try { [SQLitePCL.Batteries_V2]::Init() } catch { }
            $writer = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False")
            $writer.Open()
            $command = $writer.CreateCommand()
            $command.CommandText = @'
CREATE TABLE JourneyRuntimes (JourneyId TEXT PRIMARY KEY, Stage TEXT, AgvId TEXT, VehicleKey TEXT, PickupUpperId TEXT, GateUpperId TEXT, CreatedAt TEXT);
CREATE TABLE OrderIntents (MovementLegId TEXT PRIMARY KEY, UpperId TEXT, VehicleKey TEXT, Status TEXT, OrderId TEXT, CreateAttemptCount INTEGER, CreateAttemptId TEXT, DispatchAuditVersion INTEGER, ExperimentalCreateAuthorizationId TEXT, CreatedAt TEXT);
CREATE TABLE RiotDispatchAuditEvents (AuditEventId TEXT PRIMARY KEY, MovementLegId TEXT, Phase TEXT, Outcome TEXT, AttemptId TEXT, ReturnedOrderId TEXT, ResultPresent INTEGER);
INSERT INTO JourneyRuntimes VALUES ('j-done', 'Completed', '老厂前线新多仓位2', 'K', 'p0', 'g0', '2026-10-08 07:00:00+08:00');
INSERT INTO JourneyRuntimes VALUES ('j-wait', 'Blocked', '老厂前线新多仓位2', 'K', 'p1', NULL, '2026-10-08 09:00:00+08:00');
INSERT INTO OrderIntents VALUES ('leg-p1', 'p1', 'K', 'RESULT_UNKNOWN', NULL, 0, NULL, 1, NULL, '2026-10-08 09:00:00+08:00');
INSERT INTO RiotDispatchAuditEvents VALUES ('e1', 'leg-p1', 'PRE_CREATE_RECONCILIATION', 'UNKNOWN', NULL, NULL, 0);
'@
            $null = $command.ExecuteNonQuery(); $command.Dispose(); $writer.Dispose()
            $state = Get-ParallelJourneyDispatchState -DatabasePath $db -AssemblyDirectory $hostBuild
            $ok = -not $state.Contains('Error') -and @($state['Journeys']).Count -eq 1 -and $state['Journeys'][0]['JourneyId'] -ceq 'j-wait' -and
                $state['Journeys'][0]['AgvId'] -ceq '老厂前线新多仓位2' -and @($state['AuditEvents']).Count -eq 1
            Write-Result -Ok $ok -Name 'gate reader: reads the journeys not Completed (Chinese agvId intact) and the audit events of RESULT_UNKNOWN intents' -Detail (ConvertTo-Json $state -Compress -Depth 5)
            Write-Result -Ok ($null -eq (Get-ParallelDispatchGateRefusal -Direction Open -State $state -ServiceName $v2Service -DatabasePath $db) -and
                $null -ne (Get-ParallelDispatchGateRefusal -Direction Close -State $state -ServiceName $v2Service -DatabasePath $db)) `
                -Name 'gate reader: what it reads judges as expected (open allowed: never sent; close refused: a journey is active)' -Detail ''
            $writer = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False"); $writer.Open()
            $command = $writer.CreateCommand(); $command.CommandText = 'DROP TABLE RiotDispatchAuditEvents'; $null = $command.ExecuteNonQuery(); $command.Dispose(); $writer.Dispose()
            $broken = Get-ParallelJourneyDispatchState -DatabasePath $db -AssemblyDirectory $hostBuild
            Write-Result -Ok ($broken.Contains('Error') -and $broken['Error'] -match 'RiotDispatchAuditEvents') -Name 'gate reader: a missing table is an error, not an empty state' -Detail (ConvertTo-Json $broken -Compress)
        } finally {
            [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
            Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:Passed, $script:Failed) `
    -ForegroundColor ($script:Failed -eq 0 ? 'Green' : 'Red')

exit ($script:Failed -eq 0 ? 0 : 1)
