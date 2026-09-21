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
        # Two failures, both named: the new opsRoot matches the install's own staging glob, and the
        # seed file, still under the old opsRoot, is now outside it.
        Name = "opsRoot matches packageRoot's staging glob"
        Expect = @(
            "opsRoot ('D:\zhengyushao\ControlServer.V2.incoming-ops') matches packageRoot's staging glob"
            'fakeMesIngest.seedPath'
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
)

foreach ($case in $cases) {
    $mutated = & $case.Mutate (Copy-Definition $baseline)

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
function Find-ProductPremiseBreak {
    param([string] $Source)
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Source, [ref]$null, [ref]$null)
    $problems = [System.Collections.Generic.List[string]]::new()
    $top = @($ast.EndBlock.Statements)
    if ($top.Count -eq 0 -or $top[0].Extent.Text -cne "`$ErrorActionPreference = 'Stop'") {
        $problems.Add("the first statement is not `$ErrorActionPreference = 'Stop': $(if ($top.Count) { $top[0].Extent.Text })")
    }
    $pass = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.Value -ceq 'PASS' }, $true))
    if ($pass.Count -ne 1) {
        $problems.Add("'PASS' appears $($pass.Count) times")
    } else {
        $index = -1
        for ($i = 0; $i -lt $top.Count; $i++) {
            if ($top[$i].Extent.StartOffset -le $pass[0].Extent.StartOffset -and $top[$i].Extent.EndOffset -ge $pass[0].Extent.EndOffset) { $index = $i }
        }
        if ($index -lt $top.Count - 3) { $problems.Add("'PASS' is in top-level statement $index of $($top.Count), not among the last three") }
    }
    if (@($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TrapStatementAst] }, $true)).Count -gt 0) { $problems.Add('it has a trap') }
    return $problems
}
$productProblems = @(Find-ProductPremiseBreak ([IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\Uninstall-ControlServerLocal.ps1'))))
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
    Write-Result -Ok (@(Find-ProductPremiseBreak $premiseBreaks[$name]).Count -gt 0) -Name "premise check catches: $name" -Detail 'found nothing'
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
    'ParallelHost.psm1'                   = @{ Dynamic = @('Invoke-ParallelProductUninstaller::$UninstallerPath'); Owners = @(); Expected = 1 }
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
)
foreach ($source in $optionSources) {
    $properties = Get-OptionProperty -RelativePath $source.Path -ClassName $source.Class
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
$layoutFiles = @('ResultRoot', 'FakeLogPath', 'InstalledDefinitionPath', 'SeedPath' | ForEach-Object { $layout.$_ })
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
Write-Host ("{0} passed, {1} failed" -f $script:Passed, $script:Failed) `
    -ForegroundColor ($script:Failed -eq 0 ? 'Green' : 'Red')

exit ($script:Failed -eq 0 ? 0 : 1)
