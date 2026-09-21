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

$baseline = Read-ParallelInstanceDefinition -Path $DefinitionPath
$baselineFingerprint = Get-Fingerprint $baseline

Write-Host "Instance definition: $DefinitionPath"
Write-Host ''
Write-Host 'Positive case' -ForegroundColor Cyan

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
    @{
        Name = 'installRoot is the production install root'
        Expect = "installRoot ('C:\Program Files\8005 AGV\ControlServer') collides"
        Mutate = { param($d) $d['installRoot'] = 'C:\Program Files\8005 AGV\ControlServer'; $d }
    }
    @{
        Name = 'dataRoot nested inside the production data root'
        Expect = 'dataRoot'
        Mutate = { param($d) $d['dataRoot'] = 'C:\ProgramData\8005\ControlServer\v2'; $d }
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
        Mutate = { param($d) $d['journeyRuntime']['mapId'] = 26; $d }
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
    $unmatched = @($expected | Where-Object { $fragment = $_; -not ($failures | Where-Object { $_ -like "*$fragment*" }) })

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
# Includes the derived <packageRoot>.previous, which the definition never names.
$expectedDirectories = @('installRoot', 'dataRoot', 'backupRoot', 'packageRoot', 'opsRoot', 'stagingRoot' |
        ForEach-Object { [string] $baseline[$_] }) + @("$($baseline['packageRoot']).previous", [string] $baseline['fakeMesIngest']['installRoot'])
$missing = @($expectedDirectories | Where-Object { $directories -notcontains $_ })
Write-Result -Ok ($missing.Count -eq 0) -Name 'the footprint names every directory an install creates' `
    -Detail ("missing: " + ($missing -join ', '))
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
Write-Result -Ok ($merged['RouteGraph']['enabled'] -eq $true -and $merged['RouteGraph']['mapId'] -eq 25) `
    -Name 'the overlay states the route graph explicitly' `
    -Detail "got enabled='$($merged['RouteGraph']['enabled'])' mapId='$($merged['RouteGraph']['mapId'])'"
Write-Result -Ok ($merged['MesIngest']['baseUrl'] -eq 'http://127.0.0.1:58188') `
    -Name 'the overlay points MesIngest at the fake catalog' `
    -Detail "got '$($merged['MesIngest']['baseUrl'])'"

Write-Host ''
Write-Host ("{0} passed, {1} failed" -f $script:Passed, $script:Failed) `
    -ForegroundColor ($script:Failed -eq 0 ? 'Green' : 'Red')

exit ($script:Failed -eq 0 ? 0 : 1)
