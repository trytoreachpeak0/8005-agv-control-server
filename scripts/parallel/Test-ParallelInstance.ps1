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
    if ((Get-Fingerprint $mutated) -eq $baselineFingerprint) {
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
    param([System.Collections.Generic.List[string]] $Log, [hashtable] $Throw = @{}, [string[]] $GlobResult = @())
    $actions = @{}
    foreach ($kind in @('Service', 'ScheduledTask', 'Process', 'FirewallRule', 'MachineEnvironment', 'Directory')) {
        $k = $kind
        $message = $Throw[$kind]
        $actions[$kind] = { param($item) $Log.Add("$k|$($item.Name)"); if ($message) { throw $message } }.GetNewClosure()
    }
    $actions['DirectoryPattern'] = { param($item) $Log.Add("DirectoryPattern|$($item.Name)"); $GlobResult }.GetNewClosure()
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
