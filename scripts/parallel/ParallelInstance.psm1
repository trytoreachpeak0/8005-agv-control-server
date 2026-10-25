#Requires -Version 7

<#
    The v2 parallel instance definition and the checks that refuse a bad one.

    control-server#262. From roughly 2026-10-08 a second ControlServer runs on factory01
    beside the MVP service, driving agv02/agv03 while the MVP keeps driving agv01. Four
    resources are shared between the two (MES demand, ports and database, RIoT, the map),
    and every check below exists because one of them can be taken by accident.

    Pure functions, with two named exceptions: no network, no registry, and the checks never
    read the file system. Everything they decide is decidable from the definition text alone,
    which is what lets Test-ParallelInstance.ps1 assert that a given corruption produces exactly
    one named failure. The scripts that do touch a machine call Assert-ParallelInstanceDefinition
    first and then stop reasoning about identity. The one function that orchestrates side
    effects, Invoke-ParallelRemovalSequence, performs none itself: the caller injects them, which
    is how the self-test proves its stop conditions without a machine to delete things from.

    The two exceptions live here rather than in ParallelHost.psm1 so that every deletion of an
    instance directory -- the installer's four and the uninstaller's -- goes through one function
    that cannot be called without the path checks: Test-ParallelInstanceReparsePoint (reads one
    path's attributes) and Remove-ParallelInstanceDirectory (the only delete).
#>

Set-StrictMode -Version 3.0

<#
    Paths: two layers, and the order matters.

    FIRST LAYER -- an allowlist, and the one that decides. A path this instance may create or
    delete must be (a) written canonically -- see Test-CanonicalWindowsPath -- (b) a direct
    child of one of the three roots below, and (c) named with the instance marker, a 'V2'/'v2'
    token delimited by '.', '-', '_', space or the ends of the name. Anything else is refused.

    SECOND LAYER -- the production denylist below. It is a snapshot and cannot be complete: the
    first version of this list missed five of the directories listed in this ticket's own survey
    evidence (control-server#262 re-review, S1). It stays as a second opinion, and it fails
    closed on anything it cannot resolve.

    Why the allowlist leads: a gap in an allowlist means "cannot delete that", a gap in a
    denylist means "deleted the wrong thing". On a server that holds the production SQLite,
    only the first failure direction is acceptable.
#>
$script:AllowedParents = @(
    'C:\Program Files\8005 AGV'
    'C:\ProgramData\8005'
    'D:\zhengyushao'
)
$script:InstanceMarkerPattern = '(?i)(^|[.\-_ ])v2([.\-_ ]|$)'

# The production installation and its neighbours, named here so a parallel definition can be
# refused for colliding with them. The ControlServer entries are the defaults of
# Install-ControlServerLocal.ps1 and wire-to-gate-cd.md section 5; the rest are the siblings the
# 2026-09-21 survey found in D:\zhengyushao (evidence/deploy/20260921-cs262-parallel-instance/
# factory01-survey-paths.txt) plus the MesIngest Watch root on the same host.
$script:ProductionServiceName = '8005 AGV ControlServer'
$script:ProductionPaths = @(
    'C:\Program Files\8005 AGV\ControlServer'
    'C:\Program Files\8005 AGV\ControlServer.Dashboard'
    'C:\ProgramData\8005\ControlServer'
    'C:\ProgramData\8005\ControlServer-backups'
    'D:\zhengyushao\ControlServer'
    'D:\zhengyushao\ControlServer.previous'
    'D:\zhengyushao\control-server-ops'
    'D:\zhengyushao\control-server-staging'
    'D:\zhengyushao\MesIngest'
    'D:\zhengyushao\MesIngest.previous'
    'D:\zhengyushao\mes-ingest-ops'
    'D:\zhengyushao\mes-ingest-staging'
    'D:\zhengyushao\Hyper-V'
    'D:\zhengyushao\installer'
    'D:\zhengyushao\RabbitMQ'
    'D:\zhengyushao\vm-staging'
    'D:\zhengyushao\w1-20260913'
    'C:\MesIngest-Watch'
)

# The MVP's map, from its own appsettings.json and the user's 2026-09-21 answer ("MVP runs on
# 25, v2 on 26"). Refused outright: a definition still carrying these points the parallel
# instance at the production vehicle's map, and nothing else in these checks would notice --
# 25 is a perfectly valid positive integer.
$script:ProductionMapId = 25
$script:ProductionMapIdentity = '老厂前线new'
# Matched against ConvertTo-MapComparisonKey's output, which is lower case with '_' and
# whitespace already turned into '-'.
# 'map', at most one separator, '25', and no further digit: 'map-25', 'map25' (the most natural
# typo), 'map-25-x' and 'map-25.a' match; 'map-250' does not. Limits, on purpose: no left boundary,
# so 'xmap-25' is refused too (refusing more is the safe direction); 'MAP.25', 'MAP/25' and
# 'MAP-025' are not recognised (S1 re-review, round 4).
$script:ProductionMapTokenPattern = 'map-?25(?![0-9])'
# 58005/58007 are the MVP server, 58009 its dashboard port (unused today but reserved by
# Install-ControlServerLocal.ps1's default), 5088 the production MesIngest.
$script:ProductionPorts = @{
    58005 = 'the MVP onboard NDJSON transport'
    58007 = 'the MVP health and vehicle-safety binding'
    58009 = "the MVP dashboard's reserved port"
    5088  = 'the production MesIngest'
}

<#
    control-server#535. mesIngest.source: 'fake' (absent means fake; the behaviour before #535,
    unchanged) or 'production'. The user decided on 2026-10-09 that v2 may read the production
    MesIngest, for STAGING_TO_WIRE only, while the MVP keeps WIRE_TO_GATE.

    The split rests on one fact and nothing else: the two work type sets are disjoint. MesIngest
    is read-only and has no claim -- both instances see the same catalog, and neither writes back --
    so "a demand one instance took is gone for the other" is not true, and nothing stops two
    instances from acting on the same material except each refusing the other's type. The MVP
    (and agv01's field line) only ever accepts WIRE_TO_GATE (JourneyRuntimeEngine); v2 refuses
    anything outside allowedWorkTypes before admission (WorkTypeScopeCriterion). So 'production'
    pins allowedWorkTypes to exactly the one type, and the shipped seven -- WIRE_TO_GATE among them --
    are refused by name. Hard-coded on purpose, like the vehicle whitelist: a rule read out of the
    file being checked is edited in the same keystroke as the file.
#>
$script:MesIngestSources = @('fake', 'production')
$script:ProductionMesIngestBaseUrl = 'http://127.0.0.1:5088'
$script:ProductionSourceWorkTypes = @('STAGING_TO_WIRE')
$script:MvpWorkType = 'WIRE_TO_GATE'

<#
    control-server#564. The production cutover switch (docs/production-cutover-mode-plan.md, user decision 2026-10-13: D1, D6).
    The MVP is stopped and v2 takes every task type and all three cars, so two of the guards above stop being true and are
    lifted -- only while the deployment says so twice: the command carries -ProductionCutover AND the definition carries
    productionCutover.approvalRecord. Either half alone is refused. Everything else that keeps this instance off the MVP
    (its service name, paths, ports, map 25) is refused exactly as before: the MVP stays on disk as the rollback.

    The seven types are the full set the package ships (Test-ParallelInstance.ps1 asserts them against appsettings.json), and
    a cutover definition must name all of them: "the cutover" is v2 taking over the MVP's work, not some of it. The seventh,
    WIRE_TO_FULL_INSPECTION, since control-server#737 (program#195).
#>
$script:CutoverWorkTypes = @('DIE_TO_WIRE_STAGING', 'DIE_TO_OVEN', 'WIRE_TO_GATE', 'WIRE_TO_OPTICAL', 'STAGING_TO_WIRE', 'WIRE_TO_NITROGEN', 'WIRE_TO_FULL_INSPECTION')
$script:CutoverPhases = @('trial', 'production')
$script:CutoverPlanReference = 'Production cutover: docs/production-cutover-mode-plan.md -- it takes -ProductionCutover on the command and productionCutover.approvalRecord in the definition.'

<#
    The two spare vehicles, as pairs. Hard-coded on purpose: a whitelist read out of the file
    being checked is not a whitelist, because whoever edits the vehicle edits the list in the
    same keystroke.

    Source: remote-ops/fleet.md, itself read from RIoT GET /api/device/v1/devices on
    2026-09-03. deviceKey is the durable coordinate -- deviceName is human-editable and RIoT
    does not enforce uniqueness on it -- so the key is what anchors each row, and the name
    must agree with the key rather than being trusted on its own. RiotId is RIoT's own id, the
    other durable coordinate; a roster row (control-server#571) states it, the single-vehicle
    keys do not.
#>
$script:AllowedVehicles = @(
    [pscustomobject]@{ Alias = 'agv02'; AgvId = '老厂前线新多仓位2'; VehicleKey = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4'; RiotId = 59 }
    [pscustomobject]@{ Alias = 'agv03'; AgvId = '老厂前线新多仓位3'; VehicleKey = 'BROKERX-7daca4ee91da498d8026c68b7b941127'; RiotId = 60 }
)

# Named separately from "not in the allowed list" so that pointing the parallel instance at
# the production vehicle produces its own message rather than a generic one. agv01 is what
# the MVP service is driving right now; the shipped appsettings.json still carries its
# identity, so inheriting the default is exactly how this goes wrong.
$script:ProductionVehicle = [pscustomobject]@{
    Alias = 'agv01'
    AgvId = '老厂前线新多仓位1'
    VehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
    RiotId = 58
}

<#
    control-server#564 (coordinator 10, 2026-10-13, after cs#565's review). The user's dedicated split (2026-10-10, program#171):
    each car takes exactly these task types. Hard-coded like the vehicle whitelist: a stage definition that wrote one more type
    into a row would pass every other check and send that car to a station another car is parked on (plan section 8). Keyed by
    the registry vehicleKey; a stage that leaves a car out of its roster is not checked for it. control-server#737 (program#195
    decisions 1 and 9, 2026-10-16): agv01 takes WIRE_TO_FULL_INSPECTION too, four types; the other two cars are unchanged.
#>
$script:CutoverDedicatedTaskTypes = @{
    'BROKERX-0c20ff0600d644869a6a80c186065d85' = @('WIRE_TO_GATE', 'WIRE_TO_OPTICAL', 'WIRE_TO_NITROGEN', 'WIRE_TO_FULL_INSPECTION')
    'BROKERX-f38975561adf46ccb1d2f23833c7d0e4' = @('STAGING_TO_WIRE')
    'BROKERX-7daca4ee91da498d8026c68b7b941127' = @('DIE_TO_OVEN', 'DIE_TO_WIRE_STAGING')
}

# control-server#564. With the cutover switch on, agv01 joins the whitelist -- as its registry row, checked on all four
# coordinates like the spare cars, never by name alone.
$script:CutoverVehicles = @($script:AllowedVehicles) + @([pscustomobject]@{
        Alias = $script:ProductionVehicle.Alias; AgvId = $script:ProductionVehicle.AgvId
        VehicleKey = $script:ProductionVehicle.VehicleKey; RiotId = $script:ProductionVehicle.RiotId
    })

<#
    The keys each section may carry, spelled exactly. Anything else is refused.

    Why a whitelist rather than reading the keys this module knows about: ConvertFrom-Json
    -AsHashtable is case-sensitive, and .NET configuration is not. A definition carrying both
    "agvId" and "AgvId" -- or a "Fleet" roster -- passes every check that reads keys by name,
    is copied verbatim into the overlay, and is then bound by a configuration system that
    treats the two spellings as one setting. Checking by name can only see the keys it thought
    of; refusing the ones it did not is what closes the rest.

    journeyRuntime mirrors JourneyRuntimeOptions. Fleet was excluded until control-server#571:
    it is a second vehicle list, the options validator only requires it to *contain* the primary
    pair, and the pair check never looked inside it. Since #571 it is read row by row
    (Test-FleetIdentity, $script:FleetRowKeys) and stands instead of the single-vehicle keys,
    never beside them. Test-ParallelInstance.ps1 asserts these names against the C# properties,
    so a rename there fails here instead of binding to nothing.
#>
$script:AllowedKeys = [ordered]@{
    '' = @('instanceId', 'serviceName', 'installRoot', 'dataRoot', 'backupRoot', 'packageRoot',
        'opsRoot', 'stagingRoot', 'listenAddress', 'healthBindAddress', 'onboardPort', 'healthPort',
        'dashboardPort', 'mesIngest', 'fakeMesIngest', 'routeGraph', 'riotCreateDispatch', 'riotForeignOrderCancel',
        'journeyRuntime', 'vehicleFaultRecovery', 'foreignRunningOrderRelease', 'fieldOperatorRoles', 'taskTypeStations',
        'mapStationCatalog', 'productionCutover', 'emergencyStopRelease', 'idleReturn', 'recoveryGrant')
    # control-server#564. Who approved the production cutover, when, and where that is recorded; and which phase it is --
    # 'trial' (real MES, simulated slots) or 'production' (cs#564 review S3: the step between them needs a new database).
    # This deployment's, like mesIngest.source -- the overlay does not write it; installed-instance.json keeps it.
    'productionCutover' = @('approvalRecord', 'phase')
    # control-server#535. source is this deployment's, not a product option: the overlay does not write it.
    'mesIngest' = @('baseUrl', 'source')
    'fakeMesIngest' = @('installRoot', 'port', 'taskName', 'seedPath')
    'routeGraph' = @('enabled', 'mapId', 'designStateTtl', 'runtimeRefreshPeriod', 'runtimeStateMaxAge')
    'riotCreateDispatch' = @('enabled')
    'riotForeignOrderCancel' = @('enabled')
    'journeyRuntime' = @('enabled', 'pollInterval', 'agvId', 'vehicleKey', 'agvLifecycleGeneration', 'fleet',
        'mapId', 'mapIdentity', 'dispatchZone', 'dispatchGeneration',
        'maximumEvidenceAge', 'departureSafetyResultWait', 'stationDepartureWaitTimeout',
        'sameStationAppendCorrectionGrace', 'cargoHoldingTimeout', 'sublotBoxCountPath', 'allowedWorkTypes', 'allowedDispatchZones',
        'admissionPolicyVersion', 'admissionPolicyDeploymentId', 'checkpointWaitBudget',
        'areaEndAdmissionRevokedTimeout', 'clearanceToWaitingPointEnabled', 'stationTimeoutRedispatchLimit', 'stagingDepartureRules',
        'catalogAgeWindow', 'trialSublotCharacterPosition', 'trialSublotCharacters')
    # control-server#454. credentialEnvironmentVariable is deliberately absent: the overlay writes
    # this instance's own variable name, so no definition can point the service at the MVP's.
    'vehicleFaultRecovery' = @('enabled')
    # control-server#617 (review M1). The foreign running order release entry's own switch; it shares the credential above.
    'foreignRunningOrderRelease' = @('enabled')
    # workspace#49. REQ-0356's two release entries (EmergencyStopReleaseOptions). Both credential variable names are
    # deliberately absent, as for vehicleFaultRecovery: the overlay writes this instance's own.
    'emergencyStopRelease' = @('enabled')
    # control-server#781. REQ-0291's idle return switch (IdleReturnOptions, section IdleReturn): on, a vehicle with no demand drives
    # itself to a waiting point (agv02 to its STAGING_TO_WIRE origin, CP-0016).
    'idleReturn' = @('enabled')
    # control-server#797. ADR-cross-0066's per-vehicle, time-limited recovery grant (RecoveryGrantOptions, section RecoveryGrant):
    # on, an exception recovery session, every fault recovery request and the two vehicle charging confirmations need a grant.
    'recoveryGrant' = @('required')
    'fieldOperatorRoles' = @('path', 'onboardClearanceEntryDeclared')
    # control-server#518. TaskTypeStationPreset.SettingsFileKey; there is no options class to mirror.
    'taskTypeStations' = @('settingsFile')
    # control-server#531. REQ-0302's two approved values (MapStationCatalogOptions), and approvalRecord: who approved them
    # and when. The record is this deployment's, like mesIngest.source -- the overlay does not write it.
    'mapStationCatalog' = @('approvedSyncPeriod', 'approvedMaxUnconfirmed', 'approvalRecord')
}

<#
    control-server#571. The keys a journeyRuntime.fleet row may carry, spelled exactly. The first six
    are FleetVehicleOptions' (Test-ParallelInstance.ps1 asserts them against the C# properties) and are
    what the overlay writes. deviceKey and riotId are this deployment's: remote-ops/fleet.md's RIoT
    coordinates, stated so that each row is checked against the registry on all of them, and never
    written to the overlay. roundTimeoutMilliseconds alone may be left out (the product default).
#>
$script:FleetRowProductKeys = @('agvId', 'vehicleKey', 'agvLifecycleGeneration', 'allowedTaskTypes', 'zones', 'roundTimeoutMilliseconds')
$script:FleetRowKeys = @($script:FleetRowProductKeys) + @('deviceKey', 'riotId')
# The single-vehicle keys a roster replaces. Writing one beside the roster is refused, not reconciled.
$script:SingleVehicleKeys = @('agvId', 'vehicleKey', 'agvLifecycleGeneration')
<#
    program#184. The keys journeyRuntime.stagingDepartureRules may carry (StagingDepartureRulesOptions, control-server#709),
    spelled exactly; Test-ParallelInstance.ps1 asserts them against the C# properties. Only the switch:
    LoadNotSettledAlarmAfter keeps the package default, and a definition that names it is refused rather than passed
    through unchecked -- whitelisting it takes a duration check and a read-back row, not one more name here.
#>
$script:StagingDepartureRulesKeys = @('enabled')

# Names only this instance uses, shared by the installer and the uninstaller so the two cannot
# drift apart. The certificate variable matters most: Update-ControlServerLocal.ps1 deletes the
# machine-scope variable it is pointed at, and the default name is the MVP's.
$script:CertificatePasswordVariable = 'CONTROL_SERVER_V2_ONBOARD_CERTIFICATE_PASSWORD'
$script:ProductionCertificatePasswordVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
$script:FirewallRuleFormat = '8005 AGV ControlServer V2 {0}'
# control-server#454. Its own name, not the product default CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL,
# as for every variable the two instances could otherwise share (coordinator, 2026-10-03).
$script:FaultRecoveryCredentialVariable = 'CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL'
# control-server#600. One vehicle-safety projection credential per roster row, named by the row's RIoT id (the durable
# coordinate in remote-ops/fleet.md): RIOT59 is agv02, RIOT60 agv03. This instance's own names, like the one above.
$script:OnboardSafetyCredentialVariablePrefix = 'CONTROL_SERVER_V2_ONBOARD_SAFETY_CREDENTIAL_RIOT'
# control-server#797. One exception recovery proof per roster row, named by the row's RIoT id like the one above: the onboard
# half writes the same value into that vehicle's CONTROL_SERVER_RECOVERY_PROOF, and the Host accepts only that vehicle's.
$script:RecoveryProofVariablePrefix = 'CONTROL_SERVER_V2_RECOVERY_PROOF_RIOT'
# workspace#49. The emergency stop release entries' two credentials (empty vehicle; loaded vehicle, CP-0011), under this
# instance's own names: the product defaults CONTROL_SERVER_EMERGENCY_RELEASE_CREDENTIAL and ..._LOADED_... are the MVP's.
$script:EmergencyReleaseCredentialVariable = 'CONTROL_SERVER_V2_EMERGENCY_RELEASE_CREDENTIAL'
$script:EmergencyLoadedReleaseCredentialVariable = 'CONTROL_SERVER_V2_EMERGENCY_LOADED_RELEASE_CREDENTIAL'

function Get-ParallelInstanceAllowedKey {
    <#
        .SYNOPSIS
            The per-section key whitelist; '' is the top level.
    #>
    [CmdletBinding()]
    param()
    return $script:AllowedKeys
}

function Get-ParallelInstanceFleetRowKey {
    <#
        .SYNOPSIS
            The keys a journeyRuntime.fleet row may carry (control-server#571).
    #>
    [CmdletBinding()]
    param()
    return $script:FleetRowKeys
}

function Get-ParallelInstanceAllowedVehicle {
    <#
        .SYNOPSIS
            The agv02/agv03 registry the checks below are written against.
    #>
    [CmdletBinding()]
    param()
    return $script:AllowedVehicles
}

function Read-ParallelInstanceDefinition {
    <#
        .SYNOPSIS
            Reads an instance definition as a hashtable tree.

        .DESCRIPTION
            -AsHashtable rather than the default object graph: several checks turn on whether
            a key is present at all, and ContainsKey answers that where a missing property on
            a PSCustomObject under StrictMode only throws.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Instance definition not found: $Path"
    }
    $text = Get-Content -LiteralPath $Path -Raw -Encoding utf8
    return ConvertFrom-Json -InputObject $text -AsHashtable -Depth 12
}

function Test-KeyPresent {
    param([hashtable] $Node, [string] $Key)
    return ($null -ne $Node) -and $Node.ContainsKey($Key)
}

function Get-Node {
    param([hashtable] $Root, [string] $Key)
    if (-not (Test-KeyPresent -Node $Root -Key $Key)) { return $null }
    $value = $Root[$Key]
    return ($value -is [hashtable]) ? $value : $null
}

function Get-MesIngestSource {
    <#
        control-server#535. 'fake' when mesIngest.source is absent (or mesIngest itself is: that is
        refused elsewhere), the value when it is exactly 'fake' or 'production', otherwise $null --
        which the definition check refuses. Case-sensitive: 'Production' is a typo, not a mode.
    #>
    param([hashtable] $Definition)
    $mesIngest = Get-Node -Root $Definition -Key 'mesIngest'
    if (-not (Test-KeyPresent -Node $mesIngest -Key 'source')) { return 'fake' }
    $value = $mesIngest['source']
    return ($value -is [string] -and $script:MesIngestSources -ccontains $value) ? $value : $null
}

function ConvertTo-IntegerOrNull {
    <#
        JSON integers arrive as Int64 under -AsHashtable, while a literal assigned in a test is
        Int32 -- so neither type alone is the right check. Booleans and strings are excluded
        rather than coerced: PowerShell would happily turn $true into 1 and "58105" into 58105,
        and a port written as a JSON string is a configuration mistake worth naming.
    #>
    param($Value)
    if ($Value -is [bool] -or $Value -is [string]) { return $null }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [short] -or $Value -is [byte]) {
        [long] $asLong = [long] $Value
        if ($asLong -ge [int]::MinValue -and $asLong -le [int]::MaxValue) { return [int] $asLong }
    }
    return $null
}

function ConvertTo-MapComparisonKey {
    <#
        The form map names and map tokens are compared in: Unicode compatibility-normalised
        (full-width letters become ASCII), format characters such as zero-width spaces removed,
        trimmed, lower case, and every run of '_' or whitespace turned into '-'. '老厂前线new ',
        '老厂前线NEW' and '老厂前线ｎｅｗ' all become '老厂前线new'; 'MAP_25-X' becomes 'map-25-x'.
        '老厂前线new_wk', the v2 map, becomes '老厂前线new-wk' and stays distinct. 'map_-_25' and
        'MAP<U+2013>25' (en dash) become 'map-25'.

        Not handled on purpose: look-alike letters from other scripts (a Cyrillic 'а' in 'map').
        The server compares the map identity ordinally against RIoT's real value, so such a string
        names no map at all rather than the MVP's (S1 re-review, round 3).

        Why remove format characters when -ceq below would ignore them anyway: -ceq is a culture
        comparison, and culture comparisons skip zero-width characters ('a<U+200B>b' -ceq 'ab' is
        True; measured). The map-25 TOKEN is found with a regex, which compares ordinally and
        does not skip them, so 'MAP-2<U+200B>5' needs the removal. Note the direction: that
        leniency of -ceq makes a refusal refuse more, which is safe; in an allowlist it would
        accept more, which is not.
    #>
    param([AllowNull()][string] $Value)
    if ($null -eq $Value) { return '' }
    $key = $Value.Normalize([Text.NormalizationForm]::FormKC)
    $key = [regex]::Replace($key, '\p{Cf}', '').Trim().ToLowerInvariant()
    # Every run of separators -- whitespace, '_', '-', and the Unicode hyphens and dashes NFKC
    # leaves alone (U+2010 hyphen, U+2011 non-breaking hyphen, U+2012-U+2015 figure/en/em dash and
    # bar, U+2212 minus) -- becomes ONE '-'. Collapsing matters: 'map_-_25' used to become
    # 'map---25' and slip past the token pattern.
    return [regex]::Replace($key, '[\s_\-\u2010-\u2015\u2212]+', '-')
}

function Test-CanonicalWindowsPath {
    <#
        .SYNOPSIS
            $null when the path is written exactly one way; otherwise the reason it is not.

        .DESCRIPTION
            control-server#262 re-review, S1. The first version compared paths as strings after
            trimming a trailing slash, so every other spelling of a production path passed as
            "not production": forward slashes, '.\', 'x\..\', 8.3 short names ('PROGRA~1'),
            '\\?\' and UNC prefixes. The re-review deleted a real directory on its own machine
            through '<root>\.\Prod' and '<root>\x\..\Prod'. Forward slashes are also the most
            natural way to write a Windows path in JSON.

            Resolving every spelling to one would need the file system (short names, junctions),
            and this module does no I/O. So the rule is the other way round: only one spelling
            is accepted -- drive letter, backslashes, no empty/'.'/'..' segment, no '~', no
            segment ending in '.' or ' ' (Windows strips those, so 'ControlServer.' IS
            'ControlServer'), no characters Windows reserves -- and GetFullPath must hand it
            back unchanged, as a final check that nothing was left for Windows to reinterpret.
    #>
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return 'is empty' }
    if ($Path.Contains('/')) { return "uses '/'; write the path with backslashes only" }
    if ($Path.StartsWith('\\')) { return "is a UNC or device path ('\\...'); only a local drive path is accepted" }
    if ($Path -notmatch '^[A-Za-z]:\\') { return 'is not an absolute path on a drive (X:\...)' }
    if ($Path.Contains('~')) { return "contains '~' (an 8.3 short name spells some other path in a way nothing here can check)" }
    $segments = $Path.Substring(3).Split('\')
    foreach ($segment in $segments) {
        if ($segment -eq '') { return 'has an empty segment (a doubled or trailing backslash)' }
        if ($segment -in @('.', '..')) { return "has a '$segment' segment" }
        if ($segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment.StartsWith(' ')) {
            return "has a segment ('$segment') with a leading space or a trailing dot or space, which Windows silently strips"
        }
        if ($segment.IndexOfAny([char[]] '<>:"|?*') -ge 0 -or $segment -match '[\x00-\x1F]') {
            return "has a segment ('$segment') with a character Windows reserves"
        }
    }
    $full = $null
    try { $full = [IO.Path]::GetFullPath($Path) } catch { return "is not a valid path: $($_.Exception.Message)" }
    if ($full -cne $Path) { return "is not in canonical form (Windows reads it as '$full')" }
    return $null
}

function Test-OwnedPath {
    <#
        .SYNOPSIS
            $null when the path is one this instance may create and delete; otherwise why not.

        .DESCRIPTION
            The first layer. Canonical, a direct child of one of $script:AllowedParents, and named
            with the instance marker. "Direct child" is deliberate: a V2-named directory nested
            *inside* a production directory ('C:\ProgramData\8005\ControlServer\v2') would take
            its parent's ACL and its parent's fate, and is refused here even though its leaf
            carries the marker.
    #>
    param([string] $Path)
    $canonical = Test-CanonicalWindowsPath $Path
    if ($canonical) { return $canonical }
    $parent = Split-Path -Parent $Path
    $leaf = Split-Path -Leaf $Path
    # OrdinalIgnoreCase, not -ieq: -ieq is a culture comparison that skips zero-width characters,
    # so 'C:\Program Files\8005 AGV<U+200B>' would count as the root it only looks like. Windows
    # paths are case-insensitive, so ignoring case is right; ignoring characters is not.
    if (-not ($script:AllowedParents | Where-Object { [string]::Equals($_, $parent, [StringComparison]::OrdinalIgnoreCase) })) {
        return "is not directly under one of $($script:AllowedParents -join ', ')"
    }
    if ($leaf -notmatch $script:InstanceMarkerPattern) {
        return "is named '$leaf', which does not carry the instance marker (a 'V2' token such as '.V2' or '-v2-')"
    }
    return $null
}

function Get-NormalizedForComparison {
    <#
        Lower-cased GetFullPath without a trailing separator, or $null when the path cannot be
        resolved without the file system -- which the callers treat as a collision.
    #>
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Contains('~') -or $Path.StartsWith('\\')) { return $null }
    try {
        return [IO.Path]::GetFullPath($Path.Replace('/', '\')).TrimEnd('\').ToLowerInvariant()
    } catch {
        return $null
    }
}

function Test-PathCollision {
    <#
        Equal, or nested either way, after normalisation. A parallel data root placed *inside*
        the production data root shares the production directory's fate on an uninstall, and one
        placed *around* it hands the parallel instance's ACL tightening the production files.

        Fails closed: a path that cannot be normalised without touching the disk (a short name,
        a device path) counts as a collision. This is the second layer; the first
        (Test-OwnedPath) never lets such a path through, but a second layer that says "fine"
        about what it could not read is not a second layer.
    #>
    param([string] $Candidate, [string] $Production)
    $a = Get-NormalizedForComparison $Candidate
    $b = Get-NormalizedForComparison $Production
    if ($null -eq $a -or $null -eq $b) { return $true }
    if ($a -eq $b) { return $true }
    return $a.StartsWith("$b\", [StringComparison]::Ordinal) -or $b.StartsWith("$a\", [StringComparison]::Ordinal)
}

function Test-InstanceName {
    <#
        $null when a Windows service or scheduled-task name is safe to hand to Get-Service,
        Stop-Service, Get-ScheduledTask and Unregister-ScheduledTask; otherwise why not.

        control-server#262 re-review, M1: those cmdlets take wildcards in -Name/-TaskName, and
        Get-Service also matches display names. '8005 AGV ControlServer*' passed the old literal
        "-eq production" check and would have stopped the MVP service; a task name of '*' matched
        all 196 tasks on the host. Wildcard characters are refused, and the name must carry the
        instance marker -- the same allowlist idea as the paths.
    #>
    param([string] $Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return 'is empty' }
    if ($Name -ne $Name.Trim()) { return 'has leading or trailing whitespace' }
    if ($Name.IndexOfAny([char[]] '*?[]') -ge 0) { return "contains a wildcard character (* ? [ ]), which Get-Service and Get-ScheduledTask would expand" }
    if ($Name -notmatch $script:InstanceMarkerPattern) { return "does not carry the instance marker (a 'V2' token)" }
    return $null
}

function Test-InstancePath {
    <#
        .SYNOPSIS
            Every directory and file path in the definition, through both layers.

        .DESCRIPTION
            Each directory yields at most one failure, from the first layer that refuses it:
            allowlist (Test-OwnedPath), then the production denylist. Then the accepted ones are
            checked against each other -- including the derived <packageRoot>.previous and the
            staging globs, which the uninstaller deletes although the definition never names them
            -- and seedPath must sit inside opsRoot, so that it is removed with it instead of
            being left behind wherever someone pointed it.
    #>
    param([System.Collections.IDictionary] $Definition)

    [string[]] $failures = @()
    $fake = Get-Node -Root $Definition -Key 'fakeMesIngest'
    $entries = [ordered]@{}
    foreach ($key in @('installRoot', 'dataRoot', 'backupRoot', 'packageRoot', 'opsRoot', 'stagingRoot')) {
        $entries[$key] = (Test-KeyPresent -Node $Definition -Key $key) ? [string] $Definition[$key] : $null
    }
    if ($null -ne $fake) {
        $entries['fakeMesIngest.installRoot'] = (Test-KeyPresent -Node $fake -Key 'installRoot') ? [string] $fake['installRoot'] : $null
    }

    $accepted = [ordered]@{}
    foreach ($label in $entries.Keys) {
        $value = $entries[$label]
        if ([string]::IsNullOrWhiteSpace($value)) {
            $failures += "$label must be a non-empty path."
            continue
        }
        $owned = Test-OwnedPath $value
        if ($owned) {
            $failures += "$label ('$value') $owned."
            continue
        }
        $hits = @($script:ProductionPaths | Where-Object { Test-PathCollision -Candidate $value -Production $_ })
        if ($hits.Count -gt 0) {
            $failures += "$label ('$value') collides with the production path(s) $($hits -join ', ')."
            continue
        }
        $accepted[$label] = $value
    }

    # Derived paths the uninstaller removes. They inherit the marker from packageRoot, and are
    # compared with everything else so that no key can be named such that deleting a package
    # generation deletes, say, the data root.
    if ($accepted.Contains('packageRoot')) {
        $accepted['packageRoot.previous (derived)'] = "$($accepted['packageRoot']).previous"
    }
    $labels = @($accepted.Keys)
    for ($i = 0; $i -lt $labels.Count; $i++) {
        for ($j = $i + 1; $j -lt $labels.Count; $j++) {
            if (Test-PathCollision -Candidate $accepted[$labels[$i]] -Production $accepted[$labels[$j]]) {
                $failures += "$($labels[$i]) ('$($accepted[$labels[$i]])') and $($labels[$j]) ('$($accepted[$labels[$j]])') are the same directory or nested; each must be its own directory, or removing one removes the other."
            }
        }
    }
    if ($accepted.Contains('packageRoot')) {
        $leaf = Split-Path -Leaf $accepted['packageRoot']
        foreach ($label in $labels) {
            if ($label -like 'packageRoot*') { continue }
            $other = Split-Path -Leaf $accepted[$label]
            if ($other -like "$leaf.incoming-*" -or $other -like "$leaf.rollback-*") {
                $failures += "$label ('$($accepted[$label])') matches packageRoot's staging glob, so an install's cleanup would delete it."
            }
        }
    }

    if ($null -ne $fake) {
        $seed = (Test-KeyPresent -Node $fake -Key 'seedPath') ? [string] $fake['seedPath'] : $null
        if ([string]::IsNullOrWhiteSpace($seed)) {
            $failures += 'fakeMesIngest.seedPath must be a non-empty path.'
        } else {
            $canonical = Test-CanonicalWindowsPath $seed
            if ($canonical) {
                $failures += "fakeMesIngest.seedPath ('$seed') $canonical."
            } elseif ($accepted.Contains('opsRoot') -and
                -not $seed.StartsWith("$($accepted['opsRoot'])\", [StringComparison]::OrdinalIgnoreCase)) {
                $failures += "fakeMesIngest.seedPath ('$seed') must be inside opsRoot ('$($accepted['opsRoot'])'), so that it is removed with it rather than left behind."
            }
        }
    }

    # control-server#454. The roster lives in opsRoot for the seed file's reasons, and one more: the
    # install root is replaced by every install, so a roster kept there would vanish with the next.
    $roles = Get-Node -Root $Definition -Key 'fieldOperatorRoles'
    if ($null -ne $roles) {
        $rosterPath = (Test-KeyPresent -Node $roles -Key 'path') ? [string] $roles['path'] : $null
        if ([string]::IsNullOrWhiteSpace($rosterPath)) {
            $failures += 'fieldOperatorRoles.path must be a non-empty path.'
        } else {
            $canonical = Test-CanonicalWindowsPath $rosterPath
            if ($canonical) {
                $failures += "fieldOperatorRoles.path ('$rosterPath') $canonical."
            } elseif ($accepted.Contains('opsRoot') -and
                -not $rosterPath.StartsWith("$($accepted['opsRoot'])\", [StringComparison]::OrdinalIgnoreCase)) {
                $failures += "fieldOperatorRoles.path ('$rosterPath') must be inside opsRoot ('$($accepted['opsRoot'])'): every install replaces the install root, and a roster kept anywhere else is not this instance's to create."
            }
        }
    }
    return $failures
}

function Test-BindableAddress {
    <#
        A concrete IPv4 address of a real interface. Three rejections, each with its own
        reason:

          * a wildcard (0.0.0.0 and friends) also publishes the service on the Hyper-V
            AGV-Internal switch, where the CI runner lives -- the mistake the MVP first
            deployment made;
          * loopback cannot be reached by agv02/agv03, so a loopback binding produces a
            server that looks healthy from the machine it runs on and is invisible to the
            vehicles;
          * anything that is not a literal IPv4 address would be resolved at bind time,
            and this deployment has no DNS worth trusting.
    #>
    param([string] $Address)

    if ([string]::IsNullOrWhiteSpace($Address)) { return 'is empty' }
    if ($Address -in @('0.0.0.0', '*', '+', '::', '[::]', '::0')) {
        return "is the wildcard '$Address', which would also expose the service on the Hyper-V AGV-Internal switch where the CI runner lives"
    }
    [ipaddress] $parsed = $null
    if (-not [ipaddress]::TryParse($Address, [ref] $parsed)) {
        return "is '$Address', which is not a literal IPv4 address"
    }
    if ($parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        return "is '$Address', which is not IPv4"
    }
    if ([ipaddress]::IsLoopback($parsed)) {
        return "is loopback '$Address', which agv02/agv03 cannot reach"
    }
    return $null
}

function Test-ParallelProductionCutoverDefinition {
    <#
        .SYNOPSIS
            control-server#564. Whether the definition carries the productionCutover section -- the definition's half of the
            cutover switch. Says nothing about whether it is well formed; Test-ParallelInstanceDefinition does that.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory = $true)][AllowNull()] $Definition)
    return ($Definition -is [System.Collections.IDictionary]) -and $Definition.Contains('productionCutover')
}

function Get-ParallelCutoverContractVersion {
    <#
        .SYNOPSIS
            control-server#564 review round 2, L3. Which revision of the cutover rules this module carries; raised whenever a
            rule a script relies on is added. 2: phase, the governed exit, the MVP checks with port and process evidence.
            A script that acts on an installed cutover requires at least the version it was written for, instead of the
            existence of a function an earlier revision of #677 already had.
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param()
    return 2
}

function Get-ParallelCutoverWorkType {
    <#
        .SYNOPSIS
            control-server#564. The seven task types a production cutover definition must name (control-server#737 added the seventh).
    #>
    [CmdletBinding()]
    param()
    return @($script:CutoverWorkTypes)
}

function Get-ParallelCutoverInstallRefusal {
    <#
        .SYNOPSIS
            control-server#564. $null when this install or rollback may go ahead as far as the production cutover is concerned;
            otherwise why not. Pure: the installer passes in what it read.

        .DESCRIPTION
            Two rules, both about the instance's history rather than the definition alone (Test-ParallelInstanceDefinition has
            already checked that the switch and the record agree):

              * CUTOVER_SWITCH_MISSING. The installed record (installed-instance.json) is a cutover and this command is not.
                Reinstalling a parallel definition over a cutover would drop agv01 and five task types from an instance that
                may be carrying them; leaving the cutover is the rollback in docs/production-cutover-mode-plan.md section 7,
                not an install without the switch.
              * CUTOVER_REQUIRES_NEW_DATABASE / CUTOVER_FIRST_INSTALL_NOT_ROLLBACK. The first cutover install -- the installed
                record is absent or not a cutover -- happens on a new database, and as an install of the current package.
                The parallel instance's database holds STAGING_TO_WIRE demands accepted for good, the old WIRE zone and
                agv02/agv03 state; the cutover definition is meant for an empty data root (PR #676, program#170). DatabaseFiles
                is every SQLite file (*.db, *.db-wal, *.db-shm) found anywhere under the data root.

            Review S3 and S4 (2026-10-13) add two more, by productionCutover.phase:

              * CUTOVER_PRODUCTION_REQUIRES_NEW_DATABASE / CUTOVER_PHASE_BACKWARDS. From an installed 'trial' to 'production' the
                data root must be empty again: trial demands are accepted for good and stay in MES (plan section 5). Back from
                'production' to 'trial' is refused.
              * The governed exit (-LeaveProductionCutover): a parallel (non-cutover) definition over an installed cutover, onto
                an empty data root -- the cutover database renamed aside as evidence, as at the first cutover install. Without
                the switch that is CUTOVER_SWITCH_MISSING; with a database there, CUTOVER_LEAVE_REQUIRES_NEW_DATABASE; with
                -ProductionCutover as well, CUTOVER_SWITCHES_EXCLUSIVE.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [AllowNull()] $Previous,
        [switch] $ProductionCutover,
        [switch] $LeaveProductionCutover,
        [switch] $Rollback,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]] $DatabaseFiles,
        [Parameter(Mandatory = $true)][string] $DataRoot
    )
    $previousIsCutover = Test-ParallelProductionCutoverDefinition -Definition $Previous
    $nothing = ' Nothing was stopped or changed.'
    $databaseNamed = ($DatabaseFiles.Count -gt 0) ? "$DataRoot already holds a database ($($DatabaseFiles -join ', ')). Stop the service, rename the data root aside, create it empty, then run this again" : $null
    if ($ProductionCutover -and $LeaveProductionCutover) {
        return ('CUTOVER_SWITCHES_EXCLUSIVE: -ProductionCutover installs a cutover and -LeaveProductionCutover leaves one; pass one.' + $nothing)
    }
    if ($LeaveProductionCutover) {
        if (-not $previousIsCutover) {
            return ('CUTOVER_LEAVE_NOTHING_TO_LEAVE: -LeaveProductionCutover was passed, but the installed definition is not a production cutover.' + $nothing)
        }
        if (Test-ParallelProductionCutoverDefinition -Definition $Definition) {
            return ('CUTOVER_LEAVE_WITH_CUTOVER_DEFINITION: -LeaveProductionCutover installs a parallel definition, and this one carries productionCutover.' + $nothing)
        }
        if ($Rollback) {
            return ('CUTOVER_LEAVE_NOT_ROLLBACK: leaving the cutover is an install of a parallel definition on a new database, not a -Rollback.' + $nothing)
        }
        if ($databaseNamed) {
            return ("CUTOVER_LEAVE_REQUIRES_NEW_DATABASE: leaving the production cutover and $databaseNamed (docs/production-cutover-mode-plan.md section 7)." + $nothing)
        }
        return $null
    }
    if ($previousIsCutover -and -not $ProductionCutover) {
        return ('CUTOVER_SWITCH_MISSING: the installed definition is a production cutover, and this command does not pass ' +
            '-ProductionCutover. Leaving the cutover is the rollback in docs/production-cutover-mode-plan.md section 7 -- ' +
            'its last step reinstalls a parallel definition on a new database with -LeaveProductionCutover -- not a reinstall ' +
            'without the switch.' + $nothing)
    }
    $cutover = $ProductionCutover -and (Test-ParallelProductionCutoverDefinition -Definition $Definition)
    if (-not $cutover) { return $null }
    if ($previousIsCutover) {
        $from = ($Previous['productionCutover'] -is [System.Collections.IDictionary]) ? $Previous['productionCutover']['phase'] : $null
        $to = ($Definition['productionCutover'] -is [System.Collections.IDictionary]) ? $Definition['productionCutover']['phase'] : $null
        if ($from -ceq 'production' -and $to -ceq 'trial') {
            return ('CUTOVER_PHASE_BACKWARDS: the installed cutover is in production and this definition says trial. A trial runs ' +
                'on its own database; going back is the rollback, not a reinstall.' + $nothing)
        }
        if ($from -cne 'production' -and $to -ceq 'production') {
            if ($Rollback) {
                return ('CUTOVER_PRODUCTION_NOT_ROLLBACK: going from the trial to production is an install on a new database, not a -Rollback.' + $nothing)
            }
            if ($databaseNamed) {
                return ("CUTOVER_PRODUCTION_REQUIRES_NEW_DATABASE: the installed cutover is the trial ('$from'), this definition is production, and " +
                    "$databaseNamed. Demands the trial accepted stay accepted for good (docs/production-cutover-mode-plan.md section 5)." + $nothing)
            }
        }
        return $null
    }
    if ($Rollback) {
        return ('CUTOVER_FIRST_INSTALL_NOT_ROLLBACK: the first production cutover install is an install of the current package ' +
            'on a new database, not a -Rollback to the previous generation.' + $nothing)
    }
    if ($databaseNamed) {
        return ("CUTOVER_REQUIRES_NEW_DATABASE: this is the first production cutover install and $databaseNamed " +
            '(docs/production-cutover-mode-plan.md section 6, step 7).' + $nothing)
    }
    return $null
}

function Get-ParallelCutoverMvpRefusal {
    <#
        .SYNOPSIS
            control-server#564 review M3. $null when the MVP service is out of the way for a production cutover install or
            gate opening: absent, or Stopped with start type Disabled. Otherwise why not. Pure: takes Get-MvpFingerprint.

        .DESCRIPTION
            The cutover definition drives agv01 and takes every task type, which is only safe while the MVP is stopped. After
            a rollback the MVP runs again and agv01 is back on it, while the v2 instance may still carry the cutover
            definition: opening its gate then could place orders for agv01, and a foreign-order cancel could stop the MVP's.
            So installing a cutover and opening its gate both require the MVP to be stopped and disabled. Closing the gate
            does not: the way out is never refused.

            Review round 2, S2: the service is not the only way the MVP can run -- registered under another name, or started
            as a plain process. So, whatever the service says, a listener on 58005 or 58007 (-ListeningPorts) or a process
            running from the MVP's install directory (-ProcessPaths) is refused too (CUTOVER_MVP_STILL_RUNNING). The host
            half collects both (Get-ParallelMvpRuntimeEvidence in ParallelHost.psm1).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Fingerprint,
        [Parameter(Mandatory = $true)][string] $Action,
        [AllowEmptyCollection()][int[]] $ListeningPorts = @(),
        [AllowEmptyCollection()][string[]] $ProcessPaths = @()
    )
    $nothing = ' Nothing was stopped or changed.'
    if ($ListeningPorts.Count -gt 0 -or $ProcessPaths.Count -gt 0) {
        $what = @(@($ListeningPorts | Sort-Object -Unique | ForEach-Object { "a listener on $_" }) + @($ProcessPaths | Sort-Object -Unique | ForEach-Object { "a process running $_" })) -join ', '
        return ("CUTOVER_MVP_STILL_RUNNING: $Action with a production cutover definition needs the MVP not running at all, and " +
            "there is $what -- the MVP's ports or install directory, whatever its service says. Stop it (docs/production-cutover-mode-plan.md " +
            'section 6, step 4).' + $nothing)
    }
    if (-not $Fingerprint['present']) { return $null }
    if ([string] $Fingerprint['status'] -ceq 'Stopped' -and [string] $Fingerprint['startType'] -ceq 'Disabled') { return $null }
    return ("CUTOVER_MVP_NOT_STOPPED: $Action with a production cutover definition needs the MVP service '$script:ProductionServiceName' " +
        "Stopped and Disabled; it is status '$($Fingerprint['status'])', start type '$($Fingerprint['startType'])'. The cutover " +
        'drives agv01 and every task type, which the MVP must not be doing at the same time (docs/production-cutover-mode-plan.md ' +
        'section 6, step 4). Nothing was stopped or changed.')
}

function Test-ProductionMesIngestSource {
    <#
        control-server#535. The rules of mesIngest.source = 'production', each with its own reason:

          * baseUrl is exactly http://127.0.0.1:5088, the production MesIngest on this machine. Not
            "any URL": this mode exists to read that one catalog, and the 'fake' rules (loopback,
            never 5088) do not apply to it.
          * no fakeMesIngest section. A double the instance does not read is a scheduled task and a
            port nobody looks at, and a definition carrying both says two things about where demand
            comes from.
          * journeyRuntime.allowedWorkTypes is exactly ["STAGING_TO_WIRE"]: one string, that
            spelling. Absent is refused too -- the package default is not one type. WIRE_TO_GATE is
            named in its own message, because that is the type the MVP takes from the same catalog.

        Not relaxed by -ForStopDirection: no definition that breaks these could have been installed.

        control-server#564. -Cutover (the production cutover switch, both halves present) replaces the third rule: the MVP is
        stopped, so allowedWorkTypes must be exactly the full set $script:CutoverWorkTypes -- every one of the seven, once
        each, that spelling, in any order. The first two rules are unchanged.
    #>
    param([hashtable] $Definition, [switch] $Cutover)

    [string[]] $failures = @()
    $mesIngest = Get-Node -Root $Definition -Key 'mesIngest'
    $baseUrl = (Test-KeyPresent -Node $mesIngest -Key 'baseUrl') ? $mesIngest['baseUrl'] : $null
    if ($baseUrl -isnot [string] -or $baseUrl -cne $script:ProductionMesIngestBaseUrl) {
        $failures += "mesIngest.baseUrl is '$baseUrl'; with mesIngest.source 'production' it must be exactly '$script:ProductionMesIngestBaseUrl', the production MesIngest on this machine."
    }

    if (Test-KeyPresent -Node $Definition -Key 'fakeMesIngest') {
        $failures += "fakeMesIngest must be absent when mesIngest.source is 'production': this instance reads the production catalog, and a double beside it would be a task and a port nobody reads. Remove the section."
    }

    $journey = Get-Node -Root $Definition -Key 'journeyRuntime'
    if ($null -ne $journey) {
        $types = (Test-KeyPresent -Node $journey -Key 'allowedWorkTypes') ? $journey['allowedWorkTypes'] : $null
        $listed = ($types -is [System.Collections.IList]) ? @($types) : @($types | Where-Object { $null -ne $_ })
        $exact = $types -is [System.Collections.IList] -and $listed.Count -eq $script:ProductionSourceWorkTypes.Count -and
            @($listed | Where-Object { $_ -is [string] -and $script:ProductionSourceWorkTypes -ccontains $_ }).Count -eq $listed.Count
        $shown = ($null -eq $types) ? '(absent)' : (ConvertTo-Json -InputObject $types -Compress)
        if ($Cutover) {
            $strings = @($listed | Where-Object { $_ -is [string] })
            $distinct = [System.Collections.Generic.HashSet[string]]::new([string[]] $strings, [StringComparer]::Ordinal)
            $fullSet = $types -is [System.Collections.IList] -and $strings.Count -eq $listed.Count -and
                $distinct.Count -eq $strings.Count -and $strings.Count -eq $script:CutoverWorkTypes.Count -and
                @($strings | Where-Object { $script:CutoverWorkTypes -ccontains $_ }).Count -eq $strings.Count
            if (-not $fullSet) {
                $failures += "CUTOVER_WORK_TYPES_NOT_FULL_SET: journeyRuntime.allowedWorkTypes is $shown; under the production cutover it must be exactly the seven types $(ConvertTo-Json -InputObject $script:CutoverWorkTypes -Compress), once each -- the MVP is stopped and v2 takes all of its work (control-server#564)."
            }
            return $failures
        }
        if (@($listed | Where-Object { $_ -is [string] -and $_.Trim() -ieq $script:MvpWorkType }).Count -gt 0) {
            $failures += "journeyRuntime.allowedWorkTypes $shown includes $script:MvpWorkType, which the MVP takes from the same catalog: with mesIngest.source 'production' both instances read one MesIngest with no claim, so this instance would compete with the MVP for the same material. It must be exactly [""STAGING_TO_WIRE""] (user decision 2026-10-09). $script:CutoverPlanReference"
        } elseif (-not $exact) {
            $failures += "journeyRuntime.allowedWorkTypes is $shown; with mesIngest.source 'production' it must be exactly [""STAGING_TO_WIRE""] (user decision 2026-10-09: v2 does STAGING_TO_WIRE only, the MVP WIRE_TO_GATE only)."
        }
    }
    return $failures
}

function Test-ParallelInstanceDefinition {
    <#
        .SYNOPSIS
            Every reason this definition must not be deployed, as a list of strings.

        .DESCRIPTION
            An empty list means deployable. Each failure names the key it is about and starts
            with that key's path, so a caller -- or Test-ParallelInstance.ps1 -- can assert
            which conjunct fired rather than only that something did.

        .PARAMETER Definition
            The parsed definition, from Read-ParallelInstanceDefinition.

        .PARAMETER AllowRiotCreateDispatch
            Permits riotCreateDispatch.enabled = true. Placing real RIoT orders moves a
            vehicle, which the workspace CLAUDE.md authorizes one run at a time with somebody
            on site; the parallel deployment path is not that authorization, so the switch
            exists to make opening the gate a visible argument rather than a config edit.

        .PARAMETER AllowRiotForeignOrderCancel
            Permits riotForeignOrderCancel.enabled = true (control-server#330). Cancelling an
            order RIoT shows running on one of this instance's vehicles stops a vehicle someone
            else set moving -- a person moving it in RIoT, an experiment -- so it is a RIoT write
            authorized on its own, apart from placing orders, and made a visible argument for the
            same reason as -AllowRiotCreateDispatch.

        .PARAMETER ForStopDirection
            For the two ways out only: uninstalling, and closing the RIoT dispatch gate. Tolerates a
            definition with no taskTypeStations at all -- one installed before control-server#518 --
            and any journeyRuntime.cargoHoldingTimeout, absent or above five minutes (every definition
            installed before control-server#609 says 00:30:00), and nothing else: a taskTypeStations
            that is written is checked as always. Never for
            installing, rolling back or opening the gate (PR #523 review, item 1): those start
            something, and a definition without the preset must not.

        .PARAMETER ProductionCutover
            control-server#564 (docs/production-cutover-mode-plan.md). The production cutover switch. With it, and with
            productionCutover.approvalRecord in the definition, exactly two guards are lifted: agv01 may be a roster row
            (checked against its registry row like the spare cars), and the production MesIngest source must take the full
            seven task types instead of STAGING_TO_WIRE alone. A cutover names its cars in journeyRuntime.fleet, gives no task
            type to two cars, and reads the production MesIngest. Either half without the other is refused
            (CUTOVER_SWITCH_WITHOUT_RECORD, CUTOVER_RECORD_WITHOUT_SWITCH). Every other refusal is unchanged.

            Scripts that act on an already installed definition (uninstall, the gate, retiring a double) pass
            -ProductionCutover:(Test-ParallelProductionCutoverDefinition $definition): the record got into installed-instance.json
            only through an install that carried the switch, and stopping or closing must never be the step that refuses.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)] $Definition,
        [switch] $AllowRiotCreateDispatch,
        [switch] $AllowRiotForeignOrderCancel,
        [switch] $ForStopDirection,
        [switch] $ProductionCutover
    )

    [string[]] $failures = @()
    if ($Definition -isnot [System.Collections.IDictionary]) {
        return @('The instance definition is not a JSON object.')
    }

    # ------------------------------------------------------ production cutover ---

    # control-server#564. Both halves or neither. The record is checked for shape even when the switch is missing, so a
    # malformed record says so instead of only "the switch is missing".
    $hasCutoverRecord = Test-ParallelProductionCutoverDefinition -Definition $Definition
    if ($hasCutoverRecord) {
        $cutoverNode = $Definition['productionCutover']
        if ($cutoverNode -isnot [System.Collections.IDictionary] -or -not $cutoverNode.Contains('approvalRecord') -or
            $cutoverNode['approvalRecord'] -isnot [string] -or [string]::IsNullOrWhiteSpace($cutoverNode['approvalRecord'])) {
            $failures += 'productionCutover.approvalRecord must say who approved the production cutover, when, and where that is recorded; a cutover without its approval is not an approval.'
        }
        if ($cutoverNode -is [System.Collections.IDictionary] -and
            ($cutoverNode['phase'] -isnot [string] -or $script:CutoverPhases -cnotcontains $cutoverNode['phase'])) {
            $failures += "productionCutover.phase must be 'trial' (real MES, simulated slots) or 'production', exactly, got '$($cutoverNode['phase'])'. Going from trial to production takes a new database, and the installer checks it by this field (docs/production-cutover-mode-plan.md section 5)."
        }
    }
    if ($ProductionCutover -and -not $hasCutoverRecord) {
        $failures += 'CUTOVER_SWITCH_WITHOUT_RECORD: -ProductionCutover was passed, but the definition has no productionCutover.approvalRecord. The switch lifts the agv01 and work-type guards only for a definition that records who approved the cutover (control-server#564).'
    } elseif ($hasCutoverRecord -and -not $ProductionCutover) {
        $failures += 'CUTOVER_RECORD_WITHOUT_SWITCH: the definition carries productionCutover, but -ProductionCutover was not passed. A cutover definition is deployed only by a command that says so (control-server#564).'
    }
    $cutover = $ProductionCutover -and $hasCutoverRecord

    # ------------------------------------------------------------ key whitelist ---

    foreach ($section in $script:AllowedKeys.Keys) {
        $node = $section -eq '' ? $Definition : $Definition[$section]
        if ($node -isnot [System.Collections.IDictionary]) { continue }
        $failures += @(Test-SectionKey -Node $node -Section $section)
    }

    # ---------------------------------------------------------------- identity ---

    foreach ($key in @('instanceId', 'serviceName')) {
        if (-not (Test-KeyPresent -Node $Definition -Key $key) -or
            [string]::IsNullOrWhiteSpace([string] $Definition[$key])) {
            $failures += "$key must be a non-empty string."
        }
    }

    if ((Test-KeyPresent -Node $Definition -Key 'serviceName') -and
        -not [string]::IsNullOrWhiteSpace([string] $Definition['serviceName'])) {
        $serviceName = [string] $Definition['serviceName']
        if ($serviceName.Trim() -ieq $script:ProductionServiceName) {
            $failures += "serviceName is the production service '$script:ProductionServiceName'; the parallel instance must not install over the MVP service."
        } else {
            $problem = Test-InstanceName $serviceName
            if ($problem) { $failures += "serviceName '$serviceName' $problem." }
        }
    }

    # ------------------------------------------------------------------- paths ---

    $failures += @(Test-InstancePath -Definition $Definition)

    # ------------------------------------------------------------------- ports ---

    $portKeys = [ordered]@{
        onboardPort = 'the onboard NDJSON transport'
        healthPort = 'the health and vehicle-safety binding'
        dashboardPort = 'the dashboard'
    }
    $seenPorts = @{}
    foreach ($key in $portKeys.Keys) {
        if (-not (Test-KeyPresent -Node $Definition -Key $key)) {
            $failures += "$key must be set explicitly."
            continue
        }
        $value = ConvertTo-IntegerOrNull $Definition[$key]
        if ($null -eq $value -or $value -lt 1 -or $value -gt 65535) {
            $failures += "$key must be an integer TCP port, got '$($Definition[$key])'."
            continue
        }
        if ($script:ProductionPorts.ContainsKey($value)) {
            $failures += "$key is $value, which is $($script:ProductionPorts[$value])."
        }
        if ($seenPorts.ContainsKey($value)) {
            $failures += "$key is $value, already taken by $($seenPorts[$value])."
        } else {
            $seenPorts[$value] = $key
        }
    }

    # ---------------------------------------------------------------- bindings ---

    foreach ($key in @('listenAddress', 'healthBindAddress')) {
        if (-not (Test-KeyPresent -Node $Definition -Key $key)) {
            $failures += "$key must be set explicitly."
            continue
        }
        $problem = Test-BindableAddress -Address ([string] $Definition[$key])
        if ($problem) { $failures += "$key $problem." }
    }

    # ------------------------------------------------------------- MES isolation ---

    # The isolation table's first row. Both versions default to the production MesIngest on
    # 127.0.0.1:5088. In 'fake' mode -- the default, and the only mode before control-server#535 --
    # v2 reads an injected catalog and every check below the 'production' branch applies exactly as
    # it always has: 5088 is refused, the double is required. 'production' (the user's 2026-10-09
    # decision) is a separate branch with its own, narrower rules; see $script:MesIngestSources.
    $mesIngest = Get-Node -Root $Definition -Key 'mesIngest'
    $mesSource = Get-MesIngestSource -Definition $Definition
    # control-server#564. Said on its own, beside whatever the branches below say about the source.
    if ($cutover -and ($null -eq $mesIngest -or $mesSource -cne 'production')) {
        $failures += "CUTOVER_REQUIRES_PRODUCTION_SOURCE: mesIngest.source is '$($mesSource ?? '(absent)')'; the production cutover reads the production MesIngest (mesIngest.source 'production'). A fake catalog under the cutover switch would lift the agv01 guard for no cutover at all."
    }
    if ($null -ne $mesIngest -and $null -eq $mesSource) {
        $failures += "mesIngest.source must be 'fake' or 'production' (exactly, lower case), got '$($mesIngest['source'])'; leave it out for 'fake'."
    } elseif ($null -ne $mesIngest -and $mesSource -ceq 'production') {
        $failures += @(Test-ProductionMesIngestSource -Definition $Definition -Cutover:$cutover)
    } elseif ($null -eq $mesIngest) {
        $failures += 'mesIngest must be an object naming the fake catalog this instance reads.'
    } elseif (-not (Test-KeyPresent -Node $mesIngest -Key 'baseUrl')) {
        $failures += 'mesIngest.baseUrl must be set explicitly.'
    } else {
        $baseUrl = [string] $mesIngest['baseUrl']
        [uri] $parsedUrl = $null
        if (-not [uri]::TryCreate($baseUrl, [UriKind]::Absolute, [ref] $parsedUrl)) {
            $failures += "mesIngest.baseUrl is not an absolute URL: '$baseUrl'."
        } elseif ($parsedUrl.Port -eq 5088) {
            $failures += "mesIngest.baseUrl ('$baseUrl') points at port 5088, the production MesIngest; the parallel instance must never claim real demand."
        } elseif (-not $parsedUrl.IsLoopback) {
            $failures += "mesIngest.baseUrl ('$baseUrl') is not loopback; the fake catalog runs on this machine and must not be reachable from the plant network."
        }
    }

    # Fake mode only; 'production' refuses the section outright, and an unknown source has said so above.
    $fake = Get-Node -Root $Definition -Key 'fakeMesIngest'
    if ($mesSource -cne 'fake') {
        # Nothing more to check here.
    } elseif ($null -eq $fake) {
        $failures += 'fakeMesIngest must be an object describing the resident fake catalog.'
    } else {
        # installRoot and seedPath are checked with the other paths (Test-InstancePath).
        if (-not (Test-KeyPresent -Node $fake -Key 'taskName') -or
            [string]::IsNullOrWhiteSpace([string] $fake['taskName'])) {
            $failures += 'fakeMesIngest.taskName must be a non-empty string.'
        } else {
            $problem = Test-InstanceName ([string] $fake['taskName'])
            if ($problem) { $failures += "fakeMesIngest.taskName '$($fake['taskName'])' $problem." }
        }
        if (-not (Test-KeyPresent -Node $fake -Key 'port')) {
            $failures += 'fakeMesIngest.port must be set explicitly.'
        } else {
            $fakePort = ConvertTo-IntegerOrNull $fake['port']
            if ($null -eq $fakePort -or $fakePort -lt 1 -or $fakePort -gt 65535) {
                $failures += "fakeMesIngest.port must be an integer TCP port, got '$($fake['port'])'."
            } else {
                if ($script:ProductionPorts.ContainsKey($fakePort)) {
                    $failures += "fakeMesIngest.port is $fakePort, which is $($script:ProductionPorts[$fakePort])."
                }
                if ($seenPorts.ContainsKey($fakePort)) {
                    $failures += "fakeMesIngest.port is $fakePort, already taken by $($seenPorts[$fakePort])."
                }
                if ($null -ne $parsedUrl -and $parsedUrl.Port -ne $fakePort) {
                    $failures += "fakeMesIngest.port ($fakePort) does not match the port in mesIngest.baseUrl ($($parsedUrl.Port))."
                }
            }
        }
    }

    # ------------------------------------------------------------- route graph ---

    # control-server#262: the deployment refuses an inherited default rather than accepting
    # one. RouteGraphOptions.Enabled is a plain bool, so an absent section and a section that
    # says false are the same thing to the product -- and the difference matters, because
    # mid-journey append does nothing at all with the engine off and looks exactly like a
    # busy vehicle while doing it.
    $routeGraph = Get-Node -Root $Definition -Key 'routeGraph'
    $routeGraphMapId = $null
    if ($null -eq $routeGraph) {
        $failures += 'routeGraph must be an object; an absent section would leave the engine at its compiled default with nothing recording that anyone decided.'
    } elseif (-not (Test-KeyPresent -Node $routeGraph -Key 'enabled')) {
        $failures += 'routeGraph.enabled must be stated explicitly; inheriting the default leaves an environment nobody can diagnose.'
    } elseif ($routeGraph['enabled'] -isnot [bool]) {
        $failures += "routeGraph.enabled must be a JSON boolean, got '$($routeGraph['enabled'])'."
    } elseif ($routeGraph['enabled']) {
        if (-not (Test-KeyPresent -Node $routeGraph -Key 'mapId')) {
            $failures += 'routeGraph.mapId must be set explicitly when the engine is enabled.'
        } else {
            $candidateMapId = ConvertTo-IntegerOrNull $routeGraph['mapId']
            if ($null -eq $candidateMapId -or $candidateMapId -le 0) {
                $failures += "routeGraph.mapId must be a positive Map id, got '$($routeGraph['mapId'])'."
            } else {
                $routeGraphMapId = $candidateMapId
            }
        }

        # Same two relations RouteGraphOptionsValidator enforces at startup. Repeated here so
        # a bad pair is refused before the service is stopped, not after it fails to come up.
        $spans = @{}
        foreach ($key in @('runtimeRefreshPeriod', 'runtimeStateMaxAge', 'designStateTtl')) {
            if (-not (Test-KeyPresent -Node $routeGraph -Key $key)) {
                $failures += "routeGraph.$key must be set explicitly when the engine is enabled."
                continue
            }
            [timespan] $span = [timespan]::Zero
            if (-not [timespan]::TryParse([string] $routeGraph[$key], [cultureinfo]::InvariantCulture, [ref] $span)) {
                $failures += "routeGraph.$key is not a timespan: '$($routeGraph[$key])'."
                continue
            }
            $spans[$key] = $span
        }
        if ($spans.ContainsKey('runtimeRefreshPeriod') -and $spans.ContainsKey('runtimeStateMaxAge') -and
            $spans['runtimeStateMaxAge'] -le $spans['runtimeRefreshPeriod']) {
            $failures += "routeGraph.runtimeStateMaxAge ($($spans['runtimeStateMaxAge'])) must exceed routeGraph.runtimeRefreshPeriod ($($spans['runtimeRefreshPeriod']))."
        }
        if ($spans.ContainsKey('runtimeRefreshPeriod') -and $spans.ContainsKey('designStateTtl') -and
            $spans['designStateTtl'] -le $spans['runtimeRefreshPeriod']) {
            $failures += "routeGraph.designStateTtl ($($spans['designStateTtl'])) must exceed routeGraph.runtimeRefreshPeriod ($($spans['runtimeRefreshPeriod']))."
        }
    }

    # ---------------------------------------------------- map/station catalog ---

    # control-server#531. REQ-0302's two approved values. Optional, because "not approved" is a state the Host boots into
    # and reports (REQ-0303) -- but then every Map/Station-dependent demand is refused with 2110
    # CATALOG_PARAMETERS_NOT_APPROVED, which is what an install without them did on 10-09..10-12 until someone hand-edited
    # appsettings.Production.json. Written, the pair is checked here as MapStationCatalogOptionsValidator checks it at
    # startup -- both or neither, positive, the maximum above the period -- so a bad pair is refused before the service is
    # stopped, not after it fails to come up. The format is stricter than .NET's: TimeSpan's converter reads "30" as thirty
    # days and "00:00:30.5" keeps a fraction the catalog state stores as whole seconds, so only hh:mm:ss is taken.
    if (Test-KeyPresent -Node $Definition -Key 'mapStationCatalog') {
        $catalog = $Definition['mapStationCatalog']
        if ($catalog -isnot [System.Collections.IDictionary]) {
            $failures += 'mapStationCatalog must be an object holding approvedSyncPeriod, approvedMaxUnconfirmed and approvalRecord, or be left out (not approved: the Host refuses every Map/Station-dependent demand with 2110).'
        } else {
            $hasSync = $catalog.Contains('approvedSyncPeriod')
            $hasMax = $catalog.Contains('approvedMaxUnconfirmed')
            if (-not $hasSync -or -not $hasMax) {
                $failures += 'mapStationCatalog.approvedSyncPeriod and mapStationCatalog.approvedMaxUnconfirmed must be written together (REQ-0302 approves the pair; the Host refuses to start with one of them). Leave the whole section out for "not approved".'
            }
            $catalogSpans = @{}
            foreach ($key in @('approvedSyncPeriod', 'approvedMaxUnconfirmed')) {
                if (-not $catalog.Contains($key)) { continue }
                # Not $span: the route graph check above declares that one [timespan], which cannot hold $null.
                $catalogSpan = ConvertTo-ParallelCatalogSpan $catalog[$key]
                if ($null -eq $catalogSpan) {
                    $failures += "mapStationCatalog.$key must be a string hh:mm:ss, such as ""00:00:30"", got '$($catalog[$key])'. (.NET would read ""30"" as thirty days.)"
                } else {
                    $catalogSpans[$key] = $catalogSpan
                }
            }
            if ($catalogSpans.ContainsKey('approvedSyncPeriod') -and $catalogSpans['approvedSyncPeriod'] -le [timespan]::Zero) {
                $failures += "mapStationCatalog.approvedSyncPeriod must be positive, got '$($catalog['approvedSyncPeriod'])'."
            } elseif ($catalogSpans.ContainsKey('approvedSyncPeriod') -and $catalogSpans.ContainsKey('approvedMaxUnconfirmed') -and
                $catalogSpans['approvedMaxUnconfirmed'] -le $catalogSpans['approvedSyncPeriod']) {
                $failures += "mapStationCatalog.approvedMaxUnconfirmed ($($catalogSpans['approvedMaxUnconfirmed'])) must exceed mapStationCatalog.approvedSyncPeriod ($($catalogSpans['approvedSyncPeriod'])); equal would make the catalog stale the moment one sync is missed."
            }
            if (-not $catalog.Contains('approvalRecord') -or $catalog['approvalRecord'] -isnot [string] -or
                [string]::IsNullOrWhiteSpace($catalog['approvalRecord'])) {
                $failures += 'mapStationCatalog.approvalRecord must say who approved the two values, when, and where that is recorded; values without their approval are not an approval.'
            }
        }
    }

    # --------------------------------------------------------- journey runtime ---

    $journey = Get-Node -Root $Definition -Key 'journeyRuntime'
    if ($null -eq $journey) {
        $failures += 'journeyRuntime must be an object.'
    } else {
        if (-not (Test-KeyPresent -Node $journey -Key 'enabled')) {
            $failures += 'journeyRuntime.enabled must be stated explicitly.'
        } elseif ($journey['enabled'] -isnot [bool]) {
            $failures += "journeyRuntime.enabled must be a JSON boolean, got '$($journey['enabled'])'."
        }

        # control-server#638. REQ-0178's clearance move (JourneyRuntimeOptions.ClearanceToWaitingPointEnabled): on, a vehicle
        # in the clearing loop drives itself to a waiting point. Optional -- absent is off, and the overlay writes false --
        # but when written it is a JSON boolean. This check is what makes a non-boolean loud: the overlay
        # (Get-ParallelClearanceToWaitingPoint) writes anything that is not a boolean as false, and the read-back expects
        # that same false, so without this check a definition saying "true" would install off and read back green --
        # written on, not in effect, and nobody told (PR #640 review L3).
        if ((Test-KeyPresent -Node $journey -Key 'clearanceToWaitingPointEnabled') -and $journey['clearanceToWaitingPointEnabled'] -isnot [bool]) {
            $failures += "journeyRuntime.clearanceToWaitingPointEnabled must be a JSON boolean, got '$($journey['clearanceToWaitingPointEnabled'])'."
        }

        # control-server#703, narrowed by control-server#751. How often one demand goes back to the backlog at a station deadline
        # before it ends with a manual-carry alarm (JourneyRuntimeOptions.StationTimeoutRedispatchLimit). Optional -- absent, the
        # overlay writes 0 -- but when written it is the JSON integer 0: REQ-0365 (CP-0016) ends the demand at the first deadline
        # and states that a non-zero value does not conform. v4 production ran with 2; a definition still carrying it is refused
        # here rather than installed. A string "0" or a fraction is refused too: the Host's binder would read them anyway.
        if (Test-KeyPresent -Node $journey -Key 'stationTimeoutRedispatchLimit') {
            $limit = $journey['stationTimeoutRedispatchLimit']
            if (-not (($limit -is [int] -or $limit -is [long]) -and $limit -eq 0)) {
                $failures += "journeyRuntime.stationTimeoutRedispatchLimit must be the JSON integer 0 (REQ-0365: the first station deadline ends the demand), got '$limit'."
            }
        }

        # control-server#827. The catalog age window (JourneyRuntimeOptions.CatalogAgeWindow, REQ-0366): an unaccepted demand
        # MesIngest first read longer ago than this is not dispatched. Optional -- absent, the overlay writes the default
        # 00:30:00 -- but when written it is a positive hh:mm:ss, for the reason ConvertTo-ParallelCatalogSpan gives. Any other
        # value than thirty minutes takes the user's approval (REQ-0366: a commissioning value); this does not check that.
        if ((Test-KeyPresent -Node $journey -Key 'catalogAgeWindow') -and
            ($null -eq ($windowSpan = ConvertTo-ParallelCatalogSpan $journey['catalogAgeWindow']) -or $windowSpan -le [timespan]::Zero)) {
            $failures += "journeyRuntime.catalogAgeWindow must be a positive string hh:mm:ss, got '$($journey['catalogAgeWindow'])'."
        }

        # control-server#828. The trial sublot rule (JourneyRuntimeOptions.TrialSublotCharacterPosition/-Characters, REQ-0368): an
        # unaccepted demand whose SUBLOT has one of the characters at the position (1-based, the leading Q is 1) is not dispatched.
        # Optional -- absent, the overlay writes 0 and "", off -- but when written the position is a JSON integer, the characters a
        # JSON string, and both are set or both empty: the Host refuses half a rule at startup, and this refuses it before the
        # install. Character 6 and "9" are the values the user confirmed on 2026-10-21; any other takes the user's approval
        # (REQ-0368: a commissioning value), which this does not check.
        $trialPosition = (Test-KeyPresent -Node $journey -Key 'trialSublotCharacterPosition') ? $journey['trialSublotCharacterPosition'] : 0
        $trialCharacters = (Test-KeyPresent -Node $journey -Key 'trialSublotCharacters') ? $journey['trialSublotCharacters'] : ''
        if (-not (($trialPosition -is [int] -or $trialPosition -is [long]) -and $trialPosition -ge 0)) {
            $failures += "journeyRuntime.trialSublotCharacterPosition must be a non-negative JSON integer, got '$trialPosition'."
        } elseif ($trialCharacters -isnot [string]) {
            $failures += "journeyRuntime.trialSublotCharacters must be a JSON string, got '$trialCharacters'."
        } elseif ($trialCharacters -match '\s') {
            # Review L4: " 9" would filter every SUBLOT with a space at the position; the Host refuses it as well, and the
            # read-back result line (EFFECTIVE_JOURNEY_BINDING=) cannot carry a space.
            $failures += "journeyRuntime.trialSublotCharacters must not contain white space, got '$trialCharacters'."
        } elseif (($trialPosition -gt 0) -ne ($trialCharacters.Length -gt 0)) {
            $failures += "journeyRuntime.trialSublotCharacterPosition and trialSublotCharacters must both be set or both be left empty, got $trialPosition and '$trialCharacters'."
        }

        # program#184. The staging station's departure rules (JourneyRuntimeOptions.StagingDepartureRules, control-server#709):
        # optional -- absent leaves the package default, off -- but when written it is an object carrying exactly the switch,
        # and the switch is a JSON boolean. The overlay copies the object as written, so a string "true" would reach the Host's
        # binder, which reads it as on, while the read-back row (stagingDepartureRulesEnabled) compares JSON booleans only and
        # would refuse that install after the fact. Refusing it here says so before anything is installed.
        if (Test-KeyPresent -Node $journey -Key 'stagingDepartureRules') {
            $staging = $journey['stagingDepartureRules']
            if ($staging -isnot [System.Collections.IDictionary]) {
                $failures += "journeyRuntime.stagingDepartureRules must be an object, got '$staging'."
            } else {
                $failures += @(Test-SectionKey -Node $staging -Allowed $script:StagingDepartureRulesKeys -Prefix 'journeyRuntime.stagingDepartureRules.')
                if (-not (Test-KeyPresent -Node $staging -Key 'enabled')) {
                    $failures += 'journeyRuntime.stagingDepartureRules.enabled must be stated explicitly when the section is written.'
                } elseif ($staging['enabled'] -isnot [bool]) {
                    $failures += "journeyRuntime.stagingDepartureRules.enabled must be a JSON boolean, got '$($staging['enabled'])'."
                }
            }
        }

        # The route graph only refreshes from inside a JourneyRuntimeWorker iteration
        # (JourneyRuntimeWorker.cs). An enabled engine under a disabled runtime is a
        # configuration that reads as "the engine is on" and measures nothing.
        if ((Test-KeyPresent -Node $journey -Key 'enabled') -and $journey['enabled'] -is [bool] -and
            -not $journey['enabled'] -and $null -ne $routeGraph -and
            (Test-KeyPresent -Node $routeGraph -Key 'enabled') -and $routeGraph['enabled'] -is [bool] -and
            $routeGraph['enabled']) {
            $failures += 'routeGraph.enabled is true while journeyRuntime.enabled is false; the engine only refreshes inside a runtime iteration, so this combination runs nothing.'
        }

        if (-not (Test-KeyPresent -Node $journey -Key 'mapId')) {
            $failures += 'journeyRuntime.mapId must be set explicitly.'
        } else {
            $journeyMapId = ConvertTo-IntegerOrNull $journey['mapId']
            if ($null -eq $journeyMapId -or $journeyMapId -le 0) {
                $failures += "journeyRuntime.mapId must be a positive Map id, got '$($journey['mapId'])'."
            } elseif ($null -ne $routeGraphMapId -and $journeyMapId -ne $routeGraphMapId) {
                $failures += "journeyRuntime.mapId ($journeyMapId) and routeGraph.mapId ($routeGraphMapId) disagree; the engine would hold a graph for a different Map than the runtime dispatches on."
            }
        }

        # control-server#614 review L2. Left out, the overlay writes nothing and the Host falls back to the package's
        # appsettings.json -- the executable-set version, below what this instance has bound -- and the first runtime
        # round reads it as the version moving backwards. 19 accepts any tracked definition, evidence copies included.
        if (-not (Test-KeyPresent -Node $journey -Key 'admissionPolicyVersion')) {
            $failures += 'journeyRuntime.admissionPolicyVersion must be set explicitly; left out, the package default applies, which is below the version this instance has bound.'
        } elseif ($null -eq (ConvertTo-IntegerOrNull $journey['admissionPolicyVersion']) -or (ConvertTo-IntegerOrNull $journey['admissionPolicyVersion']) -le 0) {
            $failures += "journeyRuntime.admissionPolicyVersion must be a positive JSON integer, got '$($journey['admissionPolicyVersion'])'."
        }

        # control-server#609. Left out, the overlay writes nothing and the Host falls back to the package's appsettings.json:
        # five minutes from #609 on, but thirty in every older package, so a rollback would quietly restore the value the user
        # ruled out on 2026-10-11. hh:mm:ss for the reason ConvertTo-ParallelCatalogSpan gives ("30" binds as thirty days).
        # The ceiling is five minutes for every deployment, field drills included (they may run shorter); the coordinator chose
        # 2026-10-13 to hold it here rather than in the Host, so synthetic tests that never deploy keep their own values.
        # Not checked at all for -ForStopDirection: every definition installed before #609 says 00:30:00, and closing the
        # gate or uninstalling that instance must not be the step that refuses (the taskTypeStations precedent below).
        if ($ForStopDirection) {
            # Tolerated: see above. Installing, rolling back and opening the gate are checked in full.
        } elseif (-not (Test-KeyPresent -Node $journey -Key 'cargoHoldingTimeout')) {
            $failures += 'journeyRuntime.cargoHoldingTimeout must be set explicitly; left out, the package default applies, which is thirty minutes in any package older than control-server#609.'
        } elseif ($null -eq ($holdingSpan = ConvertTo-ParallelCatalogSpan $journey['cargoHoldingTimeout']) -or $holdingSpan -le [timespan]::Zero) {
            $failures += "journeyRuntime.cargoHoldingTimeout must be a positive string hh:mm:ss, got '$($journey['cargoHoldingTimeout'])'."
        } elseif ($holdingSpan -gt [timespan]::FromMinutes(5)) {
            $failures += "journeyRuntime.cargoHoldingTimeout is $($journey['cargoHoldingTimeout']); it must be at most 00:05:00 (user decision 2026-10-11: cargo holding lasts at most five minutes, production included)."
        }

        # @() for the same reason as in Assert-: a function returning an empty array returns
        # nothing, and += against an unwrapped result is where a stray $null element comes from.
        $failures += @(Test-VehicleIdentity -Journey $journey -Cutover:$cutover)
    }

    # ------------------------------------------------ the MVP's map; placeholders ---

    # control-server#262 re-review, M3. The documentation used to say "as shipped, this points at
    # the MVP's map, the checks cannot see it, do not install until fixed" -- a guarantee held by
    # whoever reads the documentation. Now that the user has settled "MVP 25, v2 26", it is a
    # check: map 25, its identity, and any MAP-25-* token are refused outright.
    foreach ($section in @('routeGraph', 'journeyRuntime')) {
        $node = Get-Node -Root $Definition -Key $section
        if ($null -ne $node -and (Test-KeyPresent -Node $node -Key 'mapId') -and
            (ConvertTo-IntegerOrNull $node['mapId']) -eq $script:ProductionMapId) {
            $failures += "$section.mapId is $script:ProductionMapId, the MVP's map. The parallel instance runs on map 26."
        }
    }
    # Both comparisons go through ConvertTo-MapComparisonKey (S1 re-review, question 3): an exact,
    # case-sensitive comparison let '老厂前线new ' (trailing space), '老厂前线NEW' and 'MAP_25-...'
    # through. The refusal is meant to catch a person typing the MVP's map, and people type
    # spaces, capitals and underscores.
    $productionIdentityKey = ConvertTo-MapComparisonKey $script:ProductionMapIdentity
    if ($null -ne $journey) {
        if ((Test-KeyPresent -Node $journey -Key 'mapIdentity') -and
            (ConvertTo-MapComparisonKey ([string] $journey['mapIdentity'])) -ceq $productionIdentityKey) {
            $failures += "journeyRuntime.mapIdentity is '$($journey['mapIdentity'])', the MVP's map name ('$script:ProductionMapIdentity')."
        }
    }
    foreach ($leaf in @(Get-StringLeaf -Node $Definition -Path '')) {
        if ((ConvertTo-MapComparisonKey $leaf.Value) -match $script:ProductionMapTokenPattern) {
            $failures += "$($leaf.Path) is '$($leaf.Value)', an identifier of the MVP's map 25."
        }
        # The same refusal the onboard deployment makes of its site files: a value still marked
        # as "fill me in" is refused, not guessed. The shipped definition carried these for the
        # map-26 values until control-server#411 filled them (see scripts/parallel/README.md); the
        # check stays for the next placeholder. Covered by Test-ParallelInstance.ps1 ('a placeholder
        # left in dispatchZone') and Invoke-ReverseCheck.ps1 (case 19).
        if ($leaf.Value -match '(?i)REPLACE_') {
            $failures += "$($leaf.Path) is still the placeholder '$($leaf.Value)'; fill in the value from the site before deploying."
        }
    }

    # ------------------------------------------------------ RIoT create dispatch ---

    # Separate from journeyRuntime by design (RiotCreateDispatchOptions): enabling the runtime
    # is not by itself an authorization to place a real order, and a real order moves a car.
    $createDispatch = Get-Node -Root $Definition -Key 'riotCreateDispatch'
    if ($null -eq $createDispatch) {
        $failures += 'riotCreateDispatch must be an object; the gate that decides whether this instance can move a vehicle is not something to inherit.'
    } elseif (-not (Test-KeyPresent -Node $createDispatch -Key 'enabled')) {
        $failures += 'riotCreateDispatch.enabled must be stated explicitly.'
    } elseif ($createDispatch['enabled'] -isnot [bool]) {
        $failures += "riotCreateDispatch.enabled must be a JSON boolean, got '$($createDispatch['enabled'])'."
    } elseif ($createDispatch['enabled'] -and -not $AllowRiotCreateDispatch) {
        $failures += 'riotCreateDispatch.enabled is true. Placing RIoT orders moves a vehicle and is authorized one run at a time with somebody on site; pass -AllowRiotCreateDispatch to deploy such a configuration deliberately.'
    }

    # ------------------------------------------------ RIoT foreign order cancel ---

    # Separate from journeyRuntime and from riotCreateDispatch (RiotForeignOrderCancelOptions,
    # control-server#330): the runtime cancels an order someone else put on one of this
    # instance's vehicles only while this gate is open, and a cancel stops a vehicle that is
    # moving for somebody else. Closed, the order is only held and alarmed.
    $foreignCancel = Get-Node -Root $Definition -Key 'riotForeignOrderCancel'
    if ($null -eq $foreignCancel) {
        $failures += 'riotForeignOrderCancel must be an object; the gate that decides whether this instance cancels orders on its vehicles is not something to inherit.'
    } elseif (-not (Test-KeyPresent -Node $foreignCancel -Key 'enabled')) {
        $failures += 'riotForeignOrderCancel.enabled must be stated explicitly.'
    } elseif ($foreignCancel['enabled'] -isnot [bool]) {
        $failures += "riotForeignOrderCancel.enabled must be a JSON boolean, got '$($foreignCancel['enabled'])'."
    } elseif ($foreignCancel['enabled'] -and -not $AllowRiotForeignOrderCancel) {
        $failures += 'riotForeignOrderCancel.enabled is true. Cancelling an order running on one of this instance''s vehicles stops a vehicle someone else set moving; pass -AllowRiotForeignOrderCancel to deploy such a configuration deliberately.'
    }

    # ------------------------------------------------- station clearance exit ---

    # control-server#454. The two sections the manual station clearance exit needs. Stated, never
    # inherited: an absent section deploys a server whose exit is quietly unavailable -- alarms
    # 2271/2272, an uncharged vehicle left on ORDER_HANG, no onboard entry -- and before this they
    # were merged in by hand and lost on the next first install. The path is checked with the
    # other paths (Test-InstancePath).
    $recovery = Get-Node -Root $Definition -Key 'vehicleFaultRecovery'
    if ($null -eq $recovery) {
        $failures += 'vehicleFaultRecovery must be an object; whether this instance offers the Host recovery and clearance entries is not something to inherit.'
    } elseif (-not (Test-KeyPresent -Node $recovery -Key 'enabled')) {
        $failures += 'vehicleFaultRecovery.enabled must be stated explicitly.'
    } elseif ($recovery['enabled'] -isnot [bool]) {
        $failures += "vehicleFaultRecovery.enabled must be a JSON boolean, got '$($recovery['enabled'])'."
    }
    # control-server#617 (review M1). Stated, never inherited, for the same reason: off, a vehicle held by a foreign order RIoT
    # can no longer show has no way out but the database.
    # An installed definition older than #617 has no such section; closing the gate and uninstalling must not refuse it
    # (-ForStopDirection; the same reasoning as taskTypeStations below). An absent section only.
    $foreignRelease = Get-Node -Root $Definition -Key 'foreignRunningOrderRelease'
    if ($ForStopDirection -and -not (Test-KeyPresent -Node $Definition -Key 'foreignRunningOrderRelease')) {
        # Tolerated: see above.
    } elseif ($null -eq $foreignRelease) {
        $failures += 'foreignRunningOrderRelease must be an object; whether this instance offers the foreign running order release entry is not something to inherit.'
    } elseif (-not (Test-KeyPresent -Node $foreignRelease -Key 'enabled')) {
        $failures += 'foreignRunningOrderRelease.enabled must be stated explicitly.'
    } elseif ($foreignRelease['enabled'] -isnot [bool]) {
        $failures += "foreignRunningOrderRelease.enabled must be a JSON boolean, got '$($foreignRelease['enabled'])'."
    }
    # workspace#49. The emergency stop release entries. Optional -- absent is off, the product default, so every definition
    # written before this deploys as before -- but required on in a production-phase cutover definition: in production a
    # vehicle this server stopped (door lock while driving, CP-0011) has no other governed way out, and an RIoT hand unlock
    # is read as an external release and stopped again (docs/emergency-stop-field-fallback.md).
    if (Test-KeyPresent -Node $Definition -Key 'emergencyStopRelease') {
        $releaseNode = $Definition['emergencyStopRelease']
        if ($releaseNode -isnot [System.Collections.IDictionary]) {
            $failures += 'emergencyStopRelease must be an object with enabled; leave the section out to keep the entries off.'
        } elseif (-not (Test-KeyPresent -Node $releaseNode -Key 'enabled')) {
            $failures += 'emergencyStopRelease.enabled must be stated explicitly.'
        } elseif ($releaseNode['enabled'] -isnot [bool]) {
            $failures += "emergencyStopRelease.enabled must be a JSON boolean, got '$($releaseNode['enabled'])'."
        }
    }
    # control-server#781. The idle return switch (IdleReturnOptions). Optional -- absent is off, the product default, and the overlay
    # writes false -- but when written it is an object with an explicit JSON boolean: .NET would bind the string "true" as on.
    if (Test-KeyPresent -Node $Definition -Key 'idleReturn') {
        $idleNode = $Definition['idleReturn']
        if ($idleNode -isnot [System.Collections.IDictionary]) {
            $failures += 'idleReturn must be an object with enabled; leave the section out to keep idle return off.'
        } elseif (-not (Test-KeyPresent -Node $idleNode -Key 'enabled')) {
            $failures += 'idleReturn.enabled must be stated explicitly.'
        } elseif ($idleNode['enabled'] -isnot [bool]) {
            $failures += "idleReturn.enabled must be a JSON boolean, got '$($idleNode['enabled'])'."
        }
    }
    # control-server#797. The recovery grant requirement (RecoveryGrantOptions). Optional: absent, it follows
    # vehicleFaultRecovery.enabled (Test-ParallelRecoveryGrantRequired) -- with the Host recovery entries on and no grant asked for,
    # anyone holding the shared credential recovers any vehicle (ADR-cross-0066), so turning the entries on turns grants on with
    # them. Stated, it is an explicit JSON boolean; true needs those entries, which are where a grant is given.
    $grantRequired = $false
    $grantStatedOff = $false
    if (Test-KeyPresent -Node $Definition -Key 'recoveryGrant') {
        $grantNode = $Definition['recoveryGrant']
        if ($grantNode -isnot [System.Collections.IDictionary]) {
            $failures += 'recoveryGrant must be an object with required.'
        } elseif (-not (Test-KeyPresent -Node $grantNode -Key 'required')) {
            $failures += 'recoveryGrant.required must be stated explicitly.'
        } elseif ($grantNode['required'] -isnot [bool]) {
            $failures += "recoveryGrant.required must be a JSON boolean, got '$($grantNode['required'])'."
        } else {
            $grantRequired = $grantNode['required']
            $grantStatedOff = -not $grantRequired
        }
    }
    # Not $recovery: Get-Node returns a [hashtable] only, and a section built with [ordered] is not one.
    $entries = $Definition.Contains('vehicleFaultRecovery') ? $Definition['vehicleFaultRecovery'] : $null
    if ($grantRequired -and -not ($entries -is [System.Collections.IDictionary] -and $entries.Contains('enabled') -and
            $entries['enabled'] -is [bool] -and $entries['enabled'])) {
        $failures += ('RECOVERY_GRANT_WITHOUT_ENTRY: recoveryGrant.required is true while vehicleFaultRecovery.enabled is not: a grant ' +
            'is given through the Host recovery entries, so no exception recovery session could ever open.')
    }
    $phaseNode = (Test-KeyPresent -Node $Definition -Key 'productionCutover') ? $Definition['productionCutover'] : $null
    # Review M2 (2026-10-22): a trial or production instance drives real vehicles, and 06-deploy -RecoveryResume leaves a
    # vehicle's recovery entry on for good on the promise that this server asks for a grant. Stating it off there is refused; a
    # rig without productionCutover may still turn it off.
    if ($grantStatedOff -and $phaseNode -is [System.Collections.IDictionary] -and $script:CutoverPhases -ccontains $phaseNode['phase']) {
        $failures += ("RECOVERY_GRANT_OFF_IN_CUTOVER: recoveryGrant.required is false while productionCutover.phase is " +
            "'$($phaseNode['phase'])'. A vehicle deployed with 06-deploy -RecoveryResume keeps its recovery entry on, and only the " +
            'per-vehicle grant stops anyone with the shared credential recovering it; leave recoveryGrant out or state it true.')
    }
    if ($phaseNode -is [System.Collections.IDictionary] -and $phaseNode['phase'] -ceq 'production' -and
        -not (Test-ParallelEmergencyReleaseEnabled -Definition $Definition)) {
        $failures += ('EMERGENCY_STOP_RELEASE_REQUIRED_IN_PRODUCTION: productionCutover.phase is production, so emergencyStopRelease.enabled ' +
            'must be true. Without the release entries a vehicle this server stopped has no governed way out, and an RIoT hand unlock ' +
            'is stopped again as an external release. Add "emergencyStopRelease": { "enabled": true } (docs/production-cutover-mode-plan.md ' +
            'step 18a; workspace#49) and generate both credentials with Set-ControlServerSecret.ps1 -NewEmergencyReleaseCredential Empty, Loaded.')
    }
    $roles = Get-Node -Root $Definition -Key 'fieldOperatorRoles'
    if ($null -eq $roles) {
        $failures += 'fieldOperatorRoles must be an object naming the roster file and whether the onboard clearance entry is declared.'
    } elseif (-not (Test-KeyPresent -Node $roles -Key 'onboardClearanceEntryDeclared')) {
        $failures += 'fieldOperatorRoles.onboardClearanceEntryDeclared must be stated explicitly.'
    } elseif ($roles['onboardClearanceEntryDeclared'] -isnot [bool]) {
        $failures += "fieldOperatorRoles.onboardClearanceEntryDeclared must be a JSON boolean, got '$($roles['onboardClearanceEntryDeclared'])'."
    }

    # ------------------------------------------------ task type station preset ---

    # control-server#518. The package's default preset binds map 25; with the runtime on, the Host
    # refuses to start unless the preset's map is JourneyRuntime:mapId (BindingMapMismatch). So the
    # definition names the per-map preset the package ships beside it, always -- the runtime is
    # switched on later by hand, and an install that does not carry the name then fails at that
    # step instead of here. A bare file name only: it resolves against the install root, which every
    # install replaces with the package, so the file always comes from the same build as the Host.
    # The map in the name has to be the runtime's map; the Host test pins each file's content to it.
    #
    # An installed definition older than #518 has no such section, and the new module meets it on the way out: 20
    # copies the current module before every gate change, and a reinstall that fails after copying the scripts but
    # before recording its definition leaves the new module beside the old definition for 19 -Uninstall. Closing
    # the gate and uninstalling must not be the steps that refuse (-ForStopDirection); an absent section only.
    $stations = Get-Node -Root $Definition -Key 'taskTypeStations'
    if ($ForStopDirection -and -not (Test-KeyPresent -Node $Definition -Key 'taskTypeStations')) {
        # Tolerated: see above.
    } elseif ($null -eq $stations) {
        $failures += 'taskTypeStations must be an object naming the per-map station preset the package ships (settingsFile).'
    } else {
        $presetFile = (Test-KeyPresent -Node $stations -Key 'settingsFile') ? $stations['settingsFile'] : $null
        $runtimeMap = ($null -ne $journey -and (Test-KeyPresent -Node $journey -Key 'mapId')) ? (ConvertTo-IntegerOrNull $journey['mapId']) : $null
        if ($presetFile -isnot [string] -or [string]::IsNullOrWhiteSpace($presetFile)) {
            $failures += 'taskTypeStations.settingsFile must be a non-empty file name.'
        # \z, not $: in .NET '$' also matches before a final line feed (PR #523 review).
        } elseif ($presetFile -cnotmatch '\Atask-type-stations\.map-([1-9][0-9]*)\.settings\.json\z') {
            $failures += "taskTypeStations.settingsFile ('$presetFile') must be a bare file name of the form task-type-stations.map-<mapId>.settings.json, one of the per-map presets the package ships next to the Host."
        } elseif ([int] $Matches[1] -ne $runtimeMap) {
            $failures += "taskTypeStations.settingsFile ('$presetFile') is the preset for map $($Matches[1]), but journeyRuntime.mapId is $runtimeMap; the Host would refuse to start the runtime (BindingMapMismatch)."
        }
    }

    return $failures
}

function ConvertTo-ParallelCatalogSpan {
    <#
        control-server#531. A mapStationCatalog value as a [timespan], or $null when it is not a JSON string of exactly
        hh:mm:ss (two digits each, hours 00-23). ParseExact rather than TimeSpan's own parser, which takes "30" as days.
    #>
    param($Value)
    if ($Value -isnot [string] -or $Value -notmatch '^\d{2}:\d{2}:\d{2}$') { return $null }
    [timespan] $span = [timespan]::Zero
    if (-not [timespan]::TryParseExact($Value, 'hh\:mm\:ss', [cultureinfo]::InvariantCulture, [ref] $span)) { return $null }
    return $span
}

function Get-ParallelMapStationCatalogApproval {
    <#
        .SYNOPSIS
            control-server#531. The two approved values as the overlay writes them and the read-back expects them: each
            "hh:mm:ss" ([timespan] 'c' format, which is also how the Host logs them), or both $null when the definition
            leaves mapStationCatalog out. ApprovalRecord is the definition's provenance line, never written to the overlay.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    $catalog = $Definition.Contains('mapStationCatalog') ? $Definition['mapStationCatalog'] : $null
    $result = [ordered]@{ approvedSyncPeriod = $null; approvedMaxUnconfirmed = $null; approvalRecord = $null }
    if ($catalog -isnot [System.Collections.IDictionary]) { return $result }
    foreach ($key in @('approvedSyncPeriod', 'approvedMaxUnconfirmed')) {
        if (-not $catalog.Contains($key)) { continue }
        $span = ConvertTo-ParallelCatalogSpan $catalog[$key]
        # A value the definition check refuses is carried as written, so nothing downstream can read it as "not approved".
        $result[$key] = ($null -ne $span) ? $span.ToString('c', [cultureinfo]::InvariantCulture) : $catalog[$key]
    }
    $result['approvalRecord'] = $catalog.Contains('approvalRecord') ? $catalog['approvalRecord'] : $null
    return $result
}

function Format-ParallelMapStationCatalogReadBack {
    <#
        .SYNOPSIS
            control-server#531. The installer's line for the two approved values the Host bound, and a warning when the
            Host bound none: Line (always) and Warning ($null, or the text to Write-Warning).

        .DESCRIPTION
            Compared beforehand by Get-ParallelEffectiveConfigurationRefusal; this only says it out loud, on its own line,
            because 19-deploy-control-server-parallel.ps1 compares the EFFECTIVE_CONFIGURATION= result line whole. Not
            approved is a state the Host may legitimately run in (REQ-0303), so it warns rather than fails -- but it names
            what follows, because a field session spent three nights finding 2110 in the log after a green install.
    #>
    [CmdletBinding()]
    param([AllowNull()] $Effective)
    $bound = ($null -ne $Effective -and $null -ne $Effective.PSObject.Properties['MapStationCatalog']) ? $Effective.MapStationCatalog : $null
    if ($null -eq $bound) {
        return [pscustomobject]@{ Line = 'mapStationCatalog not logged (package older than control-server#531)'; Warning = $null }
    }
    $line = "mapStationCatalog approvedSyncPeriod=$($bound.ApprovedSyncPeriod ?? 'null') approvedMaxUnconfirmed=$($bound.ApprovedMaxUnconfirmed ?? 'null')"
    $warning = ($null -eq $bound.ApprovedSyncPeriod -and $null -eq $bound.ApprovedMaxUnconfirmed) ?
        ('MAP_STATION_CATALOG_NOT_APPROVED: the Host bound no MapStationCatalog approved values, so it refuses every Map/Station-dependent ' +
            'demand with 2110 CATALOG_PARAMETERS_NOT_APPROVED. Put the approved pair into the definition''s mapStationCatalog and install again.') : $null
    return [pscustomobject]@{ Line = $line; Warning = $warning }
}

function Get-StringLeaf {
    <#
        Every string value in the tree with its dotted path; array elements as path[i].
    #>
    param($Node, [string] $Path)
    if ($Node -is [string]) {
        return [pscustomobject]@{ Path = $Path; Value = $Node }
    }
    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($key in @($Node.Keys)) {
            Get-StringLeaf -Node $Node[$key] -Path ($Path -eq '' ? [string] $key : "$Path.$key")
        }
        return
    }
    if ($Node -is [System.Collections.IList]) {
        for ($i = 0; $i -lt $Node.Count; $i++) {
            Get-StringLeaf -Node $Node[$i] -Path "$($Path)[$i]"
        }
    }
}

function Test-SectionKey {
    <#
        Every key in the section that is not on the whitelist, spelled exactly. Two messages,
        because the two ways to get here need two different fixes: a case variant of a known key
        (delete the duplicate), and anything else (a typo, or a new option this module has not
        been taught). -Allowed and -Prefix are for a node that is not a whitelisted section: a
        journeyRuntime.fleet row (control-server#571).
    #>
    param([System.Collections.IDictionary] $Node, [string] $Section, [string[]] $Allowed, [string] $Prefix)

    [string[]] $failures = @()
    $allowed = $PSBoundParameters.ContainsKey('Allowed') ? $Allowed : $script:AllowedKeys[$Section]
    $prefix = $PSBoundParameters.ContainsKey('Prefix') ? $Prefix : ($Section -eq '' ? '' : "$Section.")
    foreach ($key in @($Node.Keys)) {
        # Ordinal: -ccontains is a culture comparison that skips zero-width characters, and would
        # accept 'enabled<U+200B>' as 'enabled' while .NET configuration binds it as a different key
        # (see the allowlist note on ConvertTo-MapComparisonKey; evidence review3-string-equality.txt).
        if (@($allowed | Where-Object { [string]::Equals($_, $key, [StringComparison]::Ordinal) }).Count -gt 0) { continue }
        $twin = $allowed | Where-Object { [string]::Equals($_, $key, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ($twin) {
            $failures += "$prefix$key differs only in case from $prefix$twin. .NET configuration is case-insensitive, so the two would be bound as one setting while this check reads only '$twin'."
        } else {
            $failures += "$prefix$key is not a key this deployment knows. Unknown keys are refused rather than passed through; if it is a real option, add it to the whitelist in ParallelInstance.psm1."
        }
    }
    return $failures
}

function Test-VehicleIdentity {
    <#
        .SYNOPSIS
            The agv02/agv03 whitelist, checked as a pair.

        .DESCRIPTION
            Checking agvId alone would accept a name that RIoT does not enforce as unique, and
            checking vehicleKey alone would accept a definition whose human-readable half names
            a different car than the one it will actually drive. Requiring the pair to match a
            registry row is what rejects both, and it is also what rejects the halfway state a
            partial edit leaves behind -- the one case neither single-field check sees.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][hashtable] $Journey, [switch] $Cutover)

    # control-server#571: a roster stands instead of the single-vehicle keys. Ordinal 'fleet' only: a case
    # variant ('Fleet') is refused by the key whitelist, and so is the whole definition.
    if ($Journey.ContainsKey('fleet')) {
        return @(Test-FleetIdentity -Journey $Journey -Cutover:$Cutover)
    }
    # control-server#564. A cutover names its cars, each with its own task types: the single-vehicle keys would give one
    # car every type, which is not the dedicated split the user decided (2026-10-10).
    if ($Cutover) {
        return @('CUTOVER_REQUIRES_FLEET: under the production cutover the cars are named in journeyRuntime.fleet, one row per car with its own allowedTaskTypes and zones; the single-vehicle keys (agvId, vehicleKey) are refused.')
    }

    [string[]] $failures = @()

    foreach ($key in @('agvId', 'vehicleKey')) {
        if (-not (Test-KeyPresent -Node $Journey -Key $key)) {
            $failures += "journeyRuntime.$key must be set explicitly."
            continue
        }
        $value = $Journey[$key]
        # An array here would bind to the first element or to nothing depending on the
        # configuration provider, and JourneyRuntime takes one vehicle, not a fleet.
        if ($value -is [array] -or $value -is [hashtable]) {
            $failures += "journeyRuntime.$key must be a single string; JourneyRuntime drives one vehicle, not a list."
        } elseif ([string]::IsNullOrWhiteSpace([string] $value)) {
            $failures += "journeyRuntime.$key must be a non-empty string."
        }
    }
    if ($failures.Count -gt 0) { return $failures }

    $agvId = [string] $Journey['agvId']
    $vehicleKey = [string] $Journey['vehicleKey']

    # The production vehicle gets its own message. It is the value the shipped appsettings.json
    # carries, so "nobody changed it" and "somebody chose agv01" produce the same file.
    if ($vehicleKey -eq $script:ProductionVehicle.VehicleKey -or $agvId -eq $script:ProductionVehicle.AgvId) {
        return @("journeyRuntime names agv01 ('$agvId' / '$vehicleKey'), the vehicle the MVP service is driving in production. The parallel instance may only drive agv02 or agv03. $script:CutoverPlanReference")
    }

    # Both halves of the pair compare Ordinal -- exact, character for character. -eq here was a
    # case-insensitive culture comparison: a lower-cased key and a key with a zero-width character
    # appended both matched a spare vehicle, and went into the configuration as a string RIoT does
    # not have. The agv01 refusal above keeps -eq on purpose: in a refusal, lenient refuses more.
    $match = $script:AllowedVehicles | Where-Object { [string]::Equals($_.VehicleKey, $vehicleKey, [StringComparison]::Ordinal) } | Select-Object -First 1
    if ($null -eq $match) {
        $allowed = ($script:AllowedVehicles | ForEach-Object { "$($_.Alias)=$($_.VehicleKey)" }) -join ', '
        return @("journeyRuntime.vehicleKey '$vehicleKey' is not a spare vehicle. Allowed: $allowed.")
    }
    if (-not [string]::Equals($agvId, $match.AgvId, [StringComparison]::Ordinal)) {
        return @("journeyRuntime.agvId '$agvId' does not match the RIoT deviceName of $($match.Alias), which is '$($match.AgvId)'. The name and the key must describe the same car.")
    }

    return @()
}

function Get-RowIndexLabel {
    # 'journeyRuntime.fleet[1].vehicleKey' -> 'journeyRuntime.fleet[1]'; anything not under a row -> the roster itself.
    param([string] $Path)
    $match = [regex]::Match($Path, '\AjourneyRuntime\.fleet\[\d+\]')
    return $match.Success ? $match.Value : 'journeyRuntime.fleet'
}

function Test-FleetIdentity {
    <#
        .SYNOPSIS
            control-server#571. The two-car roster, checked row by row against remote-ops/fleet.md.

        .DESCRIPTION
            Four steps, each a gate for the next:

              1. agv01 anywhere in the roster -- any string that is its name or its key, or a riotId of 58,
                 in any row, under any key -- refuses the whole definition, one message per row naming the
                 fields, and nothing else is said: the ticket's failure definition is "agv01 in the roster
                 passed", and the cheapest way to never pass it is to look before anything can return early.
                 Compared leniently (-eq), as the single-vehicle agv01 refusal is: in a refusal, lenient
                 refuses more.
              2. The roster beside the single-vehicle keys is refused: two ways of naming the vehicles, and
                 this deployment does not guess which one counts.
              3. The shape: a non-empty list of objects, each with whitelisted keys only.
              4. Each row's identity quadruple -- vehicleKey, deviceKey, agvId, riotId -- must be one registry
                 row (Ordinal, as the single-vehicle pair is), and its policy slice must be one the Host and
                 this instance can honour: task types within allowedWorkTypes, zones within
                 allowedDispatchZones (the Host refuses to start otherwise), no car twice.

            control-server#564. -Cutover (the production cutover switch, both halves present) skips step 1 and checks
            step 4 against $script:CutoverVehicles -- the two spare cars plus agv01's registry row, all four coordinates
            -- and adds one rule: no task type in two rows (the user's dedicated split, 2026-10-10: one car per type,
            queueing rather than reassigning). A roster of fewer than three cars is a stage (S1, S2), and accepted.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][hashtable] $Journey, [switch] $Cutover)

    $fleet = $Journey['fleet']
    $vehicles = $Cutover ? $script:CutoverVehicles : $script:AllowedVehicles

    # ---- 1. agv01 ----
    $hits = [ordered]@{}
    # control-server#564: under the cutover switch agv01 is not refused here; step 4 checks it like the other cars.
    if (-not $Cutover) {
        foreach ($leaf in @(Get-StringLeaf -Node $fleet -Path 'journeyRuntime.fleet')) {
            if ($leaf.Value -eq $script:ProductionVehicle.AgvId -or $leaf.Value -eq $script:ProductionVehicle.VehicleKey) {
                $row = Get-RowIndexLabel $leaf.Path
                if (-not $hits.Contains($row)) { $hits[$row] = [System.Collections.Generic.List[string]]::new() }
                $hits[$row].Add($leaf.Path.Substring([Math]::Min($row.Length + 1, $leaf.Path.Length)))
            }
        }
        $rowsToScan = ($fleet -is [System.Collections.IList]) ? @($fleet) : @()
        for ($i = 0; $i -lt $rowsToScan.Count; $i++) {
            $row = $rowsToScan[$i]
            if ($row -isnot [System.Collections.IDictionary]) { continue }
            foreach ($key in @($row.Keys | Where-Object { $_ -ieq 'riotId' })) {
                # Lenient, like the strings above: 58 written as a string is refused as agv01 too, not only as malformed.
                if ((ConvertTo-IntegerOrNull $row[$key]) -eq $script:ProductionVehicle.RiotId -or "$($row[$key])".Trim() -eq "$($script:ProductionVehicle.RiotId)") {
                    $label = "journeyRuntime.fleet[$i]"
                    if (-not $hits.Contains($label)) { $hits[$label] = [System.Collections.Generic.List[string]]::new() }
                    $hits[$label].Add($key)
                }
            }
        }
    }
    if ($hits.Count -gt 0) {
        return @($hits.Keys | ForEach-Object {
                "$_ names agv01 ($($hits[$_] -join ', ')), the vehicle the MVP service is driving in production. The parallel instance may only drive agv02 or agv03; the whole definition is refused. $script:CutoverPlanReference"
            })
    }

    # ---- 2. one way of naming the vehicles ----
    $single = @($Journey.Keys | Where-Object { $key = $_; $script:SingleVehicleKeys | Where-Object { [string]::Equals($_, $key, [StringComparison]::Ordinal) } })
    if ($single.Count -gt 0) {
        $named = ($script:SingleVehicleKeys | Where-Object { $single -ccontains $_ } | ForEach-Object { "journeyRuntime.$_" }) -join ', '
        return @("journeyRuntime.fleet and $named are both set. The roster and the single-vehicle keys ($($script:SingleVehicleKeys -join ', ')) are two ways of naming the vehicles, and this deployment does not guess which one counts: write one of them.")
    }

    # ---- 3. shape ----
    if ($fleet -isnot [System.Collections.IList] -or $fleet -is [string]) {
        return @('journeyRuntime.fleet must be a JSON array of vehicles, one object per car.')
    }
    $rows = @($fleet)
    if ($rows.Count -eq 0) {
        return @('journeyRuntime.fleet names no vehicle. Leave the roster out and use agvId/vehicleKey for one car, or list the cars.')
    }

    [string[]] $failures = @()
    $allowedWorkTypes = ($Journey['allowedWorkTypes'] -is [System.Collections.IList]) ? @($Journey['allowedWorkTypes']) : $null
    $allowedZones = ($Journey['allowedDispatchZones'] -is [System.Collections.IList]) ? @($Journey['allowedDispatchZones']) : $null
    $seen = @{}
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $prefix = "journeyRuntime.fleet[$i]"
        $row = $rows[$i]
        if ($row -isnot [System.Collections.IDictionary]) {
            $failures += "$prefix must be an object describing one car."
            continue
        }
        $keyFailures = @(Test-SectionKey -Node $row -Allowed $script:FleetRowKeys -Prefix "$prefix.")
        if ($keyFailures.Count -gt 0) {
            $failures += $keyFailures
            continue
        }

        $rowFailures = @()
        foreach ($key in @('agvId', 'vehicleKey', 'deviceKey')) {
            if (-not $row.Contains($key)) {
                $rowFailures += "$prefix.$key must be set explicitly."
            } elseif ($row[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($row[$key])) {
                $rowFailures += "$prefix.$key must be a non-empty string."
            }
        }
        foreach ($key in @('riotId', 'agvLifecycleGeneration')) {
            if (-not $row.Contains($key)) {
                $rowFailures += "$prefix.$key must be set explicitly."
                continue
            }
            $number = ConvertTo-IntegerOrNull $row[$key]
            if ($null -eq $number -or $number -le 0) {
                $rowFailures += ($key -ceq 'riotId') ? "$prefix.riotId must be RIoT's positive integer id, got '$($row[$key])'." `
                    : "$prefix.agvLifecycleGeneration must be a positive integer, got '$($row[$key])'."
            }
        }
        if ($row.Contains('roundTimeoutMilliseconds')) {
            $timeout = ConvertTo-IntegerOrNull $row['roundTimeoutMilliseconds']
            if ($null -eq $timeout -or $timeout -lt 1000 -or $timeout -gt 600000) {
                $rowFailures += "$prefix.roundTimeoutMilliseconds must be an integer in 1000..600000, got '$($row['roundTimeoutMilliseconds'])'."
            }
        }
        foreach ($list in @(
                @{ Key = 'allowedTaskTypes'; Scope = $allowedWorkTypes
                    Outside = "which journeyRuntime.allowedWorkTypes does not allow; the instance refuses that type before this car's slice is consulted" }
                @{ Key = 'zones'; Scope = $allowedZones
                    Outside = 'which journeyRuntime.allowedDispatchZones omits; the Host would refuse to start' })) {
            $key = $list.Key
            if (-not $row.Contains($key)) {
                $rowFailures += "$prefix.$key must be set explicitly (an empty list means none)."
                continue
            }
            $value = $row[$key]
            if ($value -isnot [System.Collections.IList] -or $value -is [string]) {
                $rowFailures += "$prefix.$key must be a JSON array of names, got '$value'."
                continue
            }
            foreach ($item in @($value)) {
                if ($item -isnot [string] -or [string]::IsNullOrWhiteSpace($item)) {
                    $rowFailures += "$prefix.$key must not name an empty or non-string entry."
                } elseif ($null -ne $list.Scope -and -not ($list.Scope | Where-Object { [string]::Equals($_, $item, [StringComparison]::Ordinal) })) {
                    $rowFailures += "$prefix.$key names '$item', $($list.Outside)."
                }
            }
        }
        if ($rowFailures.Count -gt 0) {
            $failures += $rowFailures
            continue
        }

        # ---- 4. the identity quadruple ----
        $vehicleKey = [string] $row['vehicleKey']
        $deviceKey = [string] $row['deviceKey']
        $match = $vehicles | Where-Object { [string]::Equals($_.VehicleKey, $vehicleKey, [StringComparison]::Ordinal) } | Select-Object -First 1
        if ($null -eq $match) {
            $allowed = ($vehicles | ForEach-Object { "$($_.Alias)=$($_.VehicleKey)" }) -join ', '
            $failures += $Cutover ? "$prefix.vehicleKey '$vehicleKey' is not one of the three registry cars. Allowed: $allowed." `
                : "$prefix.vehicleKey '$vehicleKey' is not a spare vehicle. Allowed: $allowed."
            continue
        }
        if (-not [string]::Equals($deviceKey, $vehicleKey, [StringComparison]::Ordinal)) {
            $failures += "$prefix.deviceKey '$deviceKey' is not $prefix.vehicleKey '$vehicleKey'. RIoT's deviceKey is the vehicle key; the two must be the same string."
        }
        if (-not [string]::Equals([string] $row['agvId'], $match.AgvId, [StringComparison]::Ordinal)) {
            $failures += "$prefix.agvId '$($row['agvId'])' does not match the RIoT deviceName of $($match.Alias), which is '$($match.AgvId)'. The name and the key must describe the same car."
        }
        $riotId = ConvertTo-IntegerOrNull $row['riotId']
        if ($riotId -ne $match.RiotId) {
            $failures += "$prefix.riotId $riotId does not match the RIoT id of $($match.Alias), which is $($match.RiotId)."
        }
        if ($seen.ContainsKey($match.Alias)) {
            $failures += "journeyRuntime.fleet names $($match.Alias) twice (rows $($seen[$match.Alias]) and $i)."
        } else {
            $seen[$match.Alias] = $i
        }
    }

    # ---- 5. control-server#564: one car per task type under the cutover, and each car exactly its dedicated types ----
    if ($Cutover) {
        for ($i = 0; $i -lt $rows.Count; $i++) {
            $row = $rows[$i]
            if ($row -isnot [System.Collections.IDictionary] -or $row['vehicleKey'] -isnot [string]) { continue }
            $vehicle = $script:CutoverVehicles | Where-Object { [string]::Equals($_.VehicleKey, $row['vehicleKey'], [StringComparison]::Ordinal) } | Select-Object -First 1
            if ($null -eq $vehicle -or -not $script:CutoverDedicatedTaskTypes.ContainsKey($vehicle.VehicleKey)) { continue }
            $expected = @($script:CutoverDedicatedTaskTypes[$vehicle.VehicleKey])
            $actual = ($row['allowedTaskTypes'] -is [System.Collections.IList]) ? @($row['allowedTaskTypes']) : @()
            $strings = @($actual | Where-Object { $_ -is [string] })
            $distinct = [System.Collections.Generic.HashSet[string]]::new([string[]] $strings, [StringComparer]::Ordinal)
            $same = $strings.Count -eq $actual.Count -and $distinct.Count -eq $strings.Count -and $strings.Count -eq $expected.Count -and
                @($expected | Where-Object { $distinct.Contains($_) }).Count -eq $expected.Count
            if (-not $same) {
                $failures += ("CUTOVER_VEHICLE_TASK_TYPES_MISMATCH: journeyRuntime.fleet[$i] is $($vehicle.Alias), whose allowedTaskTypes is " +
                    "$(ConvertTo-Json -InputObject @($actual) -Compress); under the production cutover it must be exactly " +
                    "$(ConvertTo-Json -InputObject $expected -Compress) (the user's dedicated split, 2026-10-10, program#171). One more type " +
                    'would send this car to a station another car may be parked on (docs/production-cutover-mode-plan.md section 8).')
            }
        }
        $owner = [System.Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
        for ($i = 0; $i -lt $rows.Count; $i++) {
            $row = $rows[$i]
            if ($row -isnot [System.Collections.IDictionary] -or $row['allowedTaskTypes'] -isnot [System.Collections.IList]) { continue }
            foreach ($type in @($row['allowedTaskTypes'] | Where-Object { $_ -is [string] })) {
                if ($owner.ContainsKey($type)) {
                    $failures += "CUTOVER_TASK_TYPE_SHARED: $type is in journeyRuntime.fleet[$($owner[$type])].allowedTaskTypes and journeyRuntime.fleet[$i].allowedTaskTypes. Under the production cutover each task type has one car (user decision 2026-10-10)."
                } else {
                    $owner[$type] = $i
                }
            }
        }
    }
    return $failures
}

function Test-ParallelInstancePathIsProduction {
    <#
        .SYNOPSIS
            True when the path equals, contains or sits inside a production path -- or cannot be
            normalised well enough to tell. The second layer; see Test-ParallelInstanceOwnedPath.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory = $true)][string] $Path)
    foreach ($production in $script:ProductionPaths) {
        if (Test-PathCollision -Candidate $Path -Production $production) { return $true }
    }
    return $false
}

function Test-ParallelInstanceOwnedPath {
    <#
        .SYNOPSIS
            $null when the path is one this instance may delete; otherwise the reason it is not.

        .DESCRIPTION
            The first layer, exported for the uninstaller: canonical spelling, a direct child of
            one of the three known roots, named with the instance marker. The uninstaller asks
            this before every deletion, including for paths the definition never names directly
            (<packageRoot>.previous, the staging globs' matches).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Path)
    return Test-OwnedPath $Path
}

function Get-ParallelInstanceDeleteRefusal {
    <#
        .SYNOPSIS
            $null when both path layers allow deleting this path; otherwise why not. Text only.

        .DESCRIPTION
            The two string checks every deletion makes, in one place so no caller can make one
            and forget the other. The file-system check (Test-ParallelInstanceReparsePoint) is
            separate because the removal sequence takes it as an injected action.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Path)
    $owned = Test-OwnedPath $Path
    if ($owned) { return "it $owned" }
    if (Test-ParallelInstancePathIsProduction -Path $Path) { return 'it collides with a production path' }
    return $null
}

function Test-ParallelInstanceReparsePoint {
    <#
        .SYNOPSIS
            True when the path itself is a junction, symbolic link or other reparse point.

        .DESCRIPTION
            control-server#262 S1 re-review, question 2. Every path check above reads a string;
            a junction named ControlServer.V2 that points at the MVP's install root passes all of
            them. Measured on pwsh 7.6.6 (evidence review3-junction-probe.txt): Remove-Item
            -Recurse -Force on a directory that CONTAINS a junction removes the link and leaves
            the target's files alone, and so does removing a path that IS a junction. So the
            deletion itself does not follow links today -- but that is the current behaviour of
            one cmdlet, not something this code makes true, and whether a path we are about to
            delete as "our directory" is really a link to somewhere else is a question worth
            refusing on. The self-test pins the measured behaviour so a pwsh that changes it
            goes red.

            GetAttributes reads the link itself, not its target, so a dangling junction is still
            seen; a path that does not exist is not a reparse point.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory = $true)][string] $Path)
    try {
        $attributes = [IO.File]::GetAttributes($Path)
    } catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] {
        return $false
    }
    return [bool]($attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Remove-ParallelInstanceDirectory {
    <#
        .SYNOPSIS
            The only way this deployment deletes an instance directory. Refuses first.

        .DESCRIPTION
            control-server#262 S1 re-review, question 4: the installer's deletions (the double's
            directory, a stale staging directory, the previous generation, the incoming glob)
            relied on the one assertion at the start of the script. Each now re-checks the exact
            path it is about to delete, the same two layers and the reparse check the uninstaller
            uses. Throws on a refusal; does nothing when the path does not exist.

            control-server#715: a file still in use is retried, briefly. The cutover trial's first
            install stopped on "Access to the path '...\clrjit.dll' is denied." -- the retired
            FakeMesIngest had been stopped and waited for, but the image mapping outlived the process
            object by a moment. Only "in use" errors are retried (Test-ParallelInstanceRemovalRetryable),
            anything else is thrown at once, and the refusal checks run again before EVERY attempt: a
            retry is a new deletion and gets the same checks as the first. When the attempts run out
            the error names the file and, when Windows can tell, the process holding it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)
    for ($attempt = 1; ; $attempt++) {
        # The link check comes first only so the self-test can reach it: a test can make a junction in
        # a temporary directory, but not at a path the allowlist accepts, so with the string checks
        # first this branch would be unreachable from any test and its removal would go unnoticed.
        # Both refuse; the order changes which reason is given, not whether the delete happens.
        if (Test-ParallelInstanceReparsePoint -Path $Path) {
            throw "Refusing to delete '$Path': it is a junction or symbolic link, not a directory this instance created."
        }
        $refusal = Get-ParallelInstanceDeleteRefusal -Path $Path
        if ($refusal) { throw "Refusing to delete '$Path': $refusal." }
        if (-not (Test-Path -LiteralPath $Path)) { return }
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        } catch {
            if (-not (Test-ParallelInstanceRemovalRetryable -Exception $_.Exception)) { throw }
            if ($attempt -ge $script:DirectoryRemovalAttempts) {
                throw (Format-ParallelInstanceRemovalFailure -Path $Path -ErrorRecord $_ -Attempts $attempt)
            }
            Wait-ParallelInstanceRemovalRetry -Milliseconds $script:DirectoryRemovalRetryDelayMilliseconds
        }
    }
}

# control-server#715: 10 attempts, 1 s apart -- at most 9 s of waiting on top of the deletes themselves. The
# field case cleared in well under a second (the rerun a minute later succeeded); a handle held longer than
# this is not a release race but a process nobody stopped, and the install should stop and say which.
$script:DirectoryRemovalAttempts = 10
$script:DirectoryRemovalRetryDelayMilliseconds = 1000

function Wait-ParallelInstanceRemovalRetry {
    # The pause between two attempts, a function of its own so the self-test can count it or act in it.
    param([int] $Milliseconds)
    Start-Sleep -Milliseconds $Milliseconds
}

function Test-ParallelInstanceRemovalRetryable {
    <#
        True for the errors a file still in use produces, and only those: access denied (a mapped image
        such as clrjit.dll, or a file another process is deleting), a sharing or lock violation (a file
        another process has open), and "directory not empty" (a file inside was delete-pending -- still
        open elsewhere with delete sharing -- when its directory was removed). Looked for along the
        inner-exception chain. Everything else, including every refusal, is not retried.
    #>
    param([Parameter(Mandatory = $true)][Exception] $Exception)
    for ($e = $Exception; $null -ne $e; $e = $e.InnerException) {
        if ($e -is [UnauthorizedAccessException]) { return $true }
        # FACILITY_WIN32 (7) with ERROR_SHARING_VIOLATION 32, ERROR_LOCK_VIOLATION 33, ERROR_DIR_NOT_EMPTY 145.
        if ($e -is [IO.IOException] -and (($e.HResult -shr 16) -band 0x1FFF) -eq 7 -and ($e.HResult -band 0xFFFF) -in @(32, 33, 145)) {
            return $true
        }
    }
    return $false
}

function Format-ParallelInstanceRemovalFailure {
    # The error once the attempts run out: the directory, the entry the last attempt stopped on, the original
    # message, and who holds it -- or that nobody could be found, and why.
    #
    # Review of #762: the entry the error names goes into the sentence whenever there is one -- a delete-pending
    # file fails Test-Path but is exactly the file to name, and a directory (ERROR_DIR_NOT_EMPTY) is named with
    # what is still in it. And "access denied" with no process holding the file is not reported as "in use":
    # that is a permission or attribute problem, and calling it a release race sends the reader the wrong way.
    param([string] $Path, [Management.Automation.ErrorRecord] $ErrorRecord, [int] $Attempts)
    $target = [string] $ErrorRecord.TargetObject
    $isDirectory = $target -and (Test-Path -LiteralPath $target -PathType Container)
    $files = ($target -and -not $isDirectory) ? @($target) :
        @(Get-ChildItem -LiteralPath ($target ? $target : $Path) -Recurse -File -Force -ErrorAction SilentlyContinue |
                Select-Object -First 500 -ExpandProperty FullName)
    $ids = $null
    $lookupError = $null
    try { $ids = @(Get-ParallelInstanceFileHolderId -Path $files) } catch { $lookupError = $_.Exception.Message }
    $holders = $lookupError ? "could not be determined ($lookupError)" :
        ($ids.Count -eq 0 ? 'no process found (the handle may have closed after the last attempt)' :
            (@($ids | ForEach-Object {
                        $name = (Get-Process -Id $_ -ErrorAction SilentlyContinue)?.ProcessName
                        "pid $_ ($($name ?? 'exited'))"
                    }) -join ', '))
    $denied = $false
    for ($e = $ErrorRecord.Exception; $null -ne $e; $e = $e.InnerException) { if ($e -is [UnauthorizedAccessException]) { $denied = $true } }
    $named = $target ? "'$target'" : 'a file in it'
    $what = if ($isDirectory) {
        $left = @(Get-ChildItem -LiteralPath $target -Force -Name -ErrorAction SilentlyContinue)
        $shown = @($left | Select-Object -First 5) -join ', '
        "$named is not empty (still in it: $($left.Count -eq 0 ? 'nothing now' : $shown)$($left.Count -gt 5 ? ", and $($left.Count - 5) more" : ''))"
    } elseif ($denied -and $null -ne $ids -and $ids.Count -eq 0) {
        "access to $named is denied and no process holds it -- likely a permission or attribute problem, not a file still in use"
    } else {
        "$named is still in use"
    }
    $seconds = ($Attempts - 1) * $script:DirectoryRemovalRetryDelayMilliseconds / 1000
    return "Could not delete '$Path' after $Attempts attempts over ${seconds} s: $what ($($ErrorRecord.Exception.Message)). Held by: $holders."
}

function Get-ParallelInstanceFileHolderId {
    <#
        The ids of the processes holding any of these files open or mapped, from the Restart Manager --
        the same API Windows Installer asks "which programs use this file". Only called after a delete has
        already failed; throws when the API does, and the caller reports that instead of a holder.
    #>
    param([AllowEmptyCollection()][string[]] $Path)
    if ($Path.Count -eq 0) { return @() }
    if (-not ('ParallelInstance.RestartManager' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace ParallelInstance {
    public static class RestartManager {
        [StructLayout(LayoutKind.Sequential)]
        struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RM_PROCESS_INFO {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType; public uint AppStatus; public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint handle);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint handle, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, string[] services);
        [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] info, ref uint reasons);
        public static int[] GetHolders(string[] files) {
            uint handle;
            int rc = RmStartSession(out handle, 0, new StringBuilder(33));
            if (rc != 0) throw new Win32Exception(rc);
            try {
                rc = RmRegisterResources(handle, (uint)files.Length, files, 0, IntPtr.Zero, 0, null);
                if (rc != 0) throw new Win32Exception(rc);
                for (int tries = 0; tries < 3; tries++) {
                    uint needed, count = 0, reasons = 0;
                    rc = RmGetList(handle, out needed, ref count, null, ref reasons);
                    if (rc == 0) return new int[0];
                    if (rc != 234) throw new Win32Exception(rc);
                    var info = new RM_PROCESS_INFO[needed];
                    count = needed;
                    rc = RmGetList(handle, out needed, ref count, info, ref reasons);
                    if (rc == 234) continue;
                    if (rc != 0) throw new Win32Exception(rc);
                    var ids = new List<int>();
                    for (int i = 0; i < count; i++) ids.Add(info[i].Process.dwProcessId);
                    return ids.ToArray();
                }
                throw new Win32Exception(234);
            } finally {
                RmEndSession(handle);
            }
        }
    }
}
'@
    }
    return [ParallelInstance.RestartManager]::GetHolders($Path)
}

function Wait-ParallelInstanceProcessExit {
    <#
        .SYNOPSIS
            Waits for processes that were just stopped to exit, and throws naming the ones that did not.

        .DESCRIPTION
            control-server#715. The installer stops the FakeMesIngest before deleting its directory. It
            used to wait with Wait-Process -ErrorAction SilentlyContinue, which turned a timeout into
            silence (and the redeploy path did not wait at all), so the delete that followed met a process
            still running and failed with a less useful error. A process still there after the timeout is
            now the error, with its id. Exiting is necessary, not sufficient -- the image can stay mapped a
            moment longer -- which is what the retry in Remove-ParallelInstanceDirectory is for.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][Diagnostics.Process[]] $Process,
        [Parameter(Mandatory = $true)][int] $TimeoutSeconds
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lingering = @(foreach ($p in $Process) {
            $left = [Math]::Max(0, [int] ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if (-not $p.WaitForExit($left)) { $p }
        })
    if ($lingering.Count -gt 0) {
        $named = @($lingering | ForEach-Object { "pid $($_.Id) ($(try { $_.ProcessName } catch { '?' }))" }) -join ', '
        throw "Still running $TimeoutSeconds s after being stopped: $named. Not deleting the directory it runs from."
    }
}

function Test-ParallelInstanceDeploymentConfigPath {
    <#
        .SYNOPSIS
            $null when the installer's -DeploymentConfigPath is the one file it may read and later
            delete; otherwise why not.

        .DESCRIPTION
            S1 re-review, round 3: the installer deleted whatever path it was handed, in its
            finally, without checking it was a file or where it was. The control host always passes
            <opsRoot>\deploy-config.json; anything else is refused before the install starts, and
            the delete (Remove-ParallelInstanceDeploymentConfig) takes the path from the layout,
            never from the caller.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Path,
        [Parameter(Mandatory = $true)] $Layout
    )
    if (-not [string]::Equals($Path, $Layout.DeploymentConfigPath, [StringComparison]::OrdinalIgnoreCase)) {
        return "is '$Path'; the only accepted path is '$($Layout.DeploymentConfigPath)'"
    }
    if (Test-ParallelInstanceReparsePoint -Path $Path) { return 'is a symbolic link, not a file the control host copied' }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'is not an existing file' }
    return $null
}

function Remove-ParallelInstanceDeploymentConfig {
    <#
        .SYNOPSIS
            Deletes the secrets file the control host copied, if it is safe to. Returns $null when
            nothing is left behind (deleted, or never there); otherwise the reason it was left.

        .DESCRIPTION
            The only other delete in this deployment besides Remove-ParallelInstanceDirectory.

            deploy-config.json carries the RIoT call API key, the MesIngest shared secret and the fault recovery credential in
            plain text. From the moment the control host copies it, every way out of the install
            must remove it (S1 re-review, round 3): the installer calls this from a finally that
            starts before its first check, and the control host calls it again over ssh after the
            install step, which covers an installer that never started.

            Which file may be deleted depends on how far the install got:
              * with -Layout (the definition was accepted): only the layout's own path. A different
                path is not deleted -- it failed Test-ParallelInstanceDeploymentConfigPath, and a
                path we have refused is not one we then act on;
              * without it (the definition was refused, or never read): only a file named
                deploy-config.json directly in -FallbackDirectory, which is the directory the
                installer runs from. The control host puts the installer and the config in the
                same ops directory, so this rule needs no definition.
            In both cases it must be a plain file, not a link.

            Never throws: it runs in finally blocks, where a throw would replace the real failure.
            The caller turns a non-null result into SECRET_FILE_LEFT_BEHIND -- never silence.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string] $Path,
        $Layout,
        [Parameter(Mandatory = $true)][string] $FallbackDirectory
    )
    try {
        if ([string]::IsNullOrEmpty($Path)) { return $null }
        $isLink = Test-ParallelInstanceReparsePoint -Path $Path
        if (-not $isLink -and -not (Test-Path -LiteralPath $Path)) { return $null }

        if ($null -ne $Layout) {
            if (-not [string]::Equals($Path, $Layout.DeploymentConfigPath, [StringComparison]::OrdinalIgnoreCase)) {
                return "it is not the layout's config path '$($Layout.DeploymentConfigPath)', so it was not deleted"
            }
        } else {
            $expected = (Join-Path $FallbackDirectory 'deploy-config.json')
            if (-not [string]::Equals($Path, $expected, [StringComparison]::OrdinalIgnoreCase)) {
                return "without an accepted definition (a refused one, or the control host's after-check) only '$expected' may be deleted"
            }
        }
        if ($isLink) { return 'it is a symbolic link or junction, not the file the control host copied' }
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return 'it is not a plain file' }

        Remove-Item -LiteralPath $Path -Force
        if (Test-Path -LiteralPath $Path) { return 'it is still there after Remove-Item' }
        return $null
    } catch {
        return "it could not be removed: $($_.Exception.Message)"
    }
}

function Get-ParallelInstanceLayout {
    <#
        .SYNOPSIS
            Every name and path this instance uses on the machine, from one place.

        .DESCRIPTION
            control-server#262 re-review, M4. The installer used to read the definition's keys
            itself while the footprint read them separately; they agreed only because the same
            person wrote both. Now the installer takes every path and name from here, and the
            footprint is derived from here, so the list of what an uninstall removes and the list
            of what an install creates are the same list by construction. Test-ParallelInstance.ps1
            checks the installer for reads that bypass this function.

            Subdirectories created *inside* a layout directory (results\, logs\) are not listed
            separately: they go with their parent.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)

    $packageRoot = [string] $Definition['packageRoot']
    $opsRoot = [string] $Definition['opsRoot']
    # control-server#535: a 'production' definition has no double, and every name and path of the
    # double is then $null -- which the footprint leaves out rather than removing a nameless task.
    $source = Get-MesIngestSource -Definition $Definition
    $fake = ($source -ceq 'fake' -and $Definition.Contains('fakeMesIngest')) ? $Definition['fakeMesIngest'] : $null
    $packageLeaf = Split-Path -Leaf $packageRoot
    # String concatenation, not Join-Path: Join-Path resolves the drive and fails on a machine
    # without one (the control host has no D:), and this function must stay free of the file system.
    return [pscustomobject]@{
        ServiceName = [string] $Definition['serviceName']
        MesIngestSource = $source
        TaskName = ($null -ne $fake) ? [string] $fake['taskName'] : $null
        InstallRoot = [string] $Definition['installRoot']
        DataRoot = [string] $Definition['dataRoot']
        BackupRoot = [string] $Definition['backupRoot']
        PackageRoot = $packageRoot
        PreviousRoot = "$packageRoot.previous"
        PackageParent = Split-Path -Parent $packageRoot
        # Leaf globs in PackageParent. Prefixed with this instance's own leaf so they cannot match
        # the MVP's package directories beside it (control-server#262 review, finding 1).
        IncomingFilter = "$packageLeaf.incoming-*"
        RollbackFilter = "$packageLeaf.rollback-*"
        OpsRoot = $opsRoot
        StagingRoot = [string] $Definition['stagingRoot']
        ResultRoot = "$opsRoot\results"
        FakeLogPath = ($null -ne $fake) ? "$opsRoot\logs\fake-mes-ingest.log" : $null
        InstalledDefinitionPath = "$opsRoot\installed-instance.json"
        # Where the control host copies the secrets file (19-deploy-control-server-parallel.ps1
        # writes "$opsRoot\deploy-config.json"). The installer accepts no other path and deletes
        # only this one.
        DeploymentConfigPath = "$opsRoot\deploy-config.json"
        FakeInstallRoot = ($null -ne $fake) ? [string] $fake['installRoot'] : $null
        SeedPath = ($null -ne $fake) ? [string] $fake['seedPath'] : $null
        # control-server#454. Inside opsRoot (Test-InstancePath), so it goes with it.
        FieldOperatorRosterPath = [string] $Definition['fieldOperatorRoles']['path']
        FirewallRules = @(
            ($script:FirewallRuleFormat -f [int] $Definition['onboardPort'])
            ($script:FirewallRuleFormat -f [int] $Definition['healthPort'])
        )
        CertificatePasswordVariable = $script:CertificatePasswordVariable
    }
}

function Get-ParallelInstanceFootprint {
    <#
        .SYNOPSIS
            Everything a deployment of this definition leaves on the machine, derived from
            Get-ParallelInstanceLayout.

        .DESCRIPTION
            Data is marked so the uninstaller can keep it unless asked: the SQLite database, the
            upgrade backups and the ops directory (results, logs, the seed file and the definition
            the instance was installed from) are the record of what the instance did.

            Not listed, deliberately: the user-scope CONTROL_SERVER_RIOT_CALL_API_KEY. The MVP
            deployment reads the same variable on every install, so removing it with the parallel
            instance would break the next MVP deployment.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)

    $layout = Get-ParallelInstanceLayout -Definition $Definition
    $items = @([pscustomobject]@{ Kind = 'Service'; Name = $layout.ServiceName; Data = $false })
    # control-server#535: no double in 'production' mode, so no task and no double directory.
    if ($null -ne $layout.TaskName) {
        $items += [pscustomobject]@{ Kind = 'ScheduledTask'; Name = $layout.TaskName; Data = $false }
    }
    foreach ($rule in $layout.FirewallRules) {
        $items += [pscustomobject]@{ Kind = 'FirewallRule'; Name = $rule; Data = $false }
    }
    $items += @(
        [pscustomobject]@{ Kind = 'MachineEnvironment'; Name = $layout.CertificatePasswordVariable; Data = $false }
    )
    if ($null -ne $layout.FakeInstallRoot) {
        $items += [pscustomobject]@{ Kind = 'Directory'; Name = $layout.FakeInstallRoot; Data = $false }
    }
    $items += @(
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.InstallRoot; Data = $false }
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.PackageRoot; Data = $false }
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.PreviousRoot; Data = $false }
        [pscustomobject]@{ Kind = 'DirectoryPattern'; Name = "$($layout.PackageParent)\$($layout.IncomingFilter)"; Data = $false }
        [pscustomobject]@{ Kind = 'DirectoryPattern'; Name = "$($layout.PackageParent)\$($layout.RollbackFilter)"; Data = $false }
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.StagingRoot; Data = $false }
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.DataRoot; Data = $true }
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.BackupRoot; Data = $true }
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.OpsRoot; Data = $true }
    )
    return $items
}

function Get-ParallelInstanceName {
    <#
        .SYNOPSIS
            The fixed names this instance uses outside its definition.
    #>
    [CmdletBinding()]
    param()
    return [pscustomobject]@{
        CertificatePasswordVariable = $script:CertificatePasswordVariable
        ProductionCertificatePasswordVariable = $script:ProductionCertificatePasswordVariable
        FirewallRuleFormat = $script:FirewallRuleFormat
        FaultRecoveryCredentialVariable = $script:FaultRecoveryCredentialVariable
        OnboardSafetyCredentialVariablePrefix = $script:OnboardSafetyCredentialVariablePrefix
        RecoveryProofVariablePrefix = $script:RecoveryProofVariablePrefix
        EmergencyReleaseCredentialVariable = $script:EmergencyReleaseCredentialVariable
        EmergencyLoadedReleaseCredentialVariable = $script:EmergencyLoadedReleaseCredentialVariable
    }
}

function Invoke-ParallelRemovalSequence {
    <#
        .SYNOPSIS
            Runs an uninstall over a footprint, in a fixed order, with the actions injected.

        .DESCRIPTION
            control-server#262 re-review, S1, root cause three. The first uninstaller wrapped every
            step in the same catch-and-continue. When the product uninstaller refused -- correctly,
            with "Refusing to uninstall the production deployment" -- that refusal was logged as one
            failed item among many, and the directory loop that followed deleted the MVP's install
            root anyway. The inner guard saw the danger; the outer loop treated its refusal as a
            recoverable error and did the damage itself. Two guards together were weaker than the
            inner one alone.

            So the order and the stop conditions live here, where Test-ParallelInstance.ps1 can
            drive them with injected actions, and not in the script that touches the machine:

              1. Service first. ANY failure there aborts the whole uninstall before anything else
                 is removed. A refusal from the product uninstaller is the single most important
                 signal an uninstall can get; nothing after it runs.
              2. Scheduled task, the double's process, firewall rules, the machine variable.
                 Failures here are recorded and the sequence continues: none of these can reach
                 the MVP, and one complete list of what did and did not go diagnoses a partial
                 uninstall better than the first exception.
              3. Directories last. Before EVERY deletion -- including each match of a staging glob --
                 the path must pass the allowlist (Test-OwnedPath) and the production denylist,
                 and must not itself be a junction or symbolic link (the injected ReparsePoint
                 action; a throw from it counts as "yes"). A refusal aborts the directory phase at
                 that point. Data directories are skipped unless -RemoveData.

            "Any failure" in step 1 means a throw from the Service action, and only that. A script
            the action calls can fail without throwing -- exit 1, Write-Error under Continue, a
            native command's exit code -- so the action must turn those into a throw itself. The
            uninstaller's does, by requiring positive confirmation of success
            (Invoke-ParallelProductUninstaller in ParallelHost.psm1), not by listing failure modes.

            Actions' output is discarded. The product uninstaller prints a PASS line; before this
            was discarded it came back from this function beside the result object, and callers
            read .Aborted off a two-element array.

        .PARAMETER Actions
            Hashtable of scriptblocks, all required: Service, ScheduledTask, Process, FirewallRule,
            MachineEnvironment and Directory take one footprint item; DirectoryPattern takes one
            and returns the matching directory paths (the sequence checks and deletes them through
            Directory); ReparsePoint takes a path and returns whether it is a link. A missing one
            is refused up front -- a sequence quietly running without its link check is the
            failure this parameter exists to prevent.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [object[]] $Footprint,
        [Parameter(Mandatory = $true)] [hashtable] $Actions,
        [switch] $RemoveData
    )

    $required = @('Service', 'ScheduledTask', 'Process', 'FirewallRule', 'MachineEnvironment', 'Directory', 'DirectoryPattern', 'ReparsePoint')
    $missing = @($required | Where-Object { -not ($Actions.ContainsKey($_) -and $Actions[$_] -is [scriptblock]) })
    if ($missing.Count -gt 0) {
        throw "Invoke-ParallelRemovalSequence: missing action(s) $($missing -join ', '); nothing was run."
    }

    $removed = [System.Collections.Generic.List[string]]::new()
    $failed = [System.Collections.Generic.List[string]]::new()
    $result = { param($aborted, $reason) [pscustomobject]@{
            Removed = @($removed); Failed = @($failed); Aborted = $aborted; AbortReason = $reason } }

    foreach ($item in @($Footprint | Where-Object Kind -eq 'Service')) {
        try {
            $null = & $Actions.Service $item
            $removed.Add("service '$($item.Name)'")
        } catch {
            return & $result $true "the service step failed, so nothing else was removed: $($_.Exception.Message)"
        }
    }

    foreach ($kind in @('ScheduledTask', 'FirewallRule', 'MachineEnvironment')) {
        foreach ($item in @($Footprint | Where-Object Kind -eq $kind)) {
            try {
                $null = & $Actions[$kind] $item
                $removed.Add("$kind '$($item.Name)'")
            } catch {
                $failed.Add("$kind '$($item.Name)': $($_.Exception.Message)")
            }
            if ($kind -eq 'ScheduledTask') {
                try { $null = & $Actions.Process $item } catch { $failed.Add("process of '$($item.Name)': $($_.Exception.Message)") }
            }
        }
    }

    foreach ($item in @($Footprint | Where-Object { $_.Kind -in @('Directory', 'DirectoryPattern') })) {
        if ($item.Data -and -not $RemoveData) { continue }
        $targets = if ($item.Kind -eq 'DirectoryPattern') {
            try { @(& $Actions.DirectoryPattern $item) } catch {
                $failed.Add("glob '$($item.Name)': $($_.Exception.Message)")
                @()
            }
        } else {
            @($item.Name)
        }
        foreach ($target in $targets) {
            $refusal = Get-ParallelInstanceDeleteRefusal -Path $target
            if ($refusal) {
                return & $result $true "refused to delete '$target': $refusal. Directory phase stopped here."
            }
            $isLink = $true
            try { $isLink = [bool](& $Actions.ReparsePoint $target) } catch { $isLink = $true }
            if ($isLink) {
                return & $result $true "refused to delete '$target': it is a junction or symbolic link (or could not be checked). Directory phase stopped here."
            }
            try {
                $null = & $Actions.Directory ([pscustomobject]@{ Kind = 'Directory'; Name = $target; Data = $item.Data })
                $removed.Add("directory $target")
            } catch {
                $failed.Add("directory $($target): $($_.Exception.Message)")
            }
        }
    }

    return & $result $false $null
}

function Assert-ParallelInstanceDefinition {
    <#
        .SYNOPSIS
            Throws with every failure listed, or returns the definition unchanged.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Definition,
        [switch] $AllowRiotCreateDispatch,
        [switch] $AllowRiotForeignOrderCancel,
        # See Test-ParallelInstanceDefinition: uninstalling and closing the gate only.
        [switch] $ForStopDirection,
        # See Test-ParallelInstanceDefinition (control-server#564).
        [switch] $ProductionCutover
    )

    # @() around the call, not just the [string[]] cast: PowerShell unwraps an empty array to
    # $null on return, and [string[]] $null is $null rather than an empty array -- so under
    # StrictMode the .Count below threw on exactly the input this function is supposed to
    # accept. The self-test only exercised Test-, which its own callers already wrapped.
    [string[]] $failures = @(Test-ParallelInstanceDefinition -Definition $Definition -AllowRiotCreateDispatch:$AllowRiotCreateDispatch `
            -AllowRiotForeignOrderCancel:$AllowRiotForeignOrderCancel -ForStopDirection:$ForStopDirection -ProductionCutover:$ProductionCutover)
    if ($failures.Count -gt 0) {
        $listed = ($failures | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
        throw ("The parallel instance definition was refused ($($failures.Count) reason(s)):" +
            [Environment]::NewLine + $listed)
    }
    return $Definition
}

function Format-ParallelMesIngestAudit {
    <#
        .SYNOPSIS
            control-server#535. The one line that says where this instance's demand comes from.

        .DESCRIPTION
            Printed by the installer on every install and rollback, before anything changes and again
            in its result lines, where 19-deploy-control-server-parallel.ps1 asserts it per mode. In
            'production' mode it is loud on purpose: the instance reads the catalog the MVP serves
            customers from, and whoever reads the log should not have to infer that from a port.
            Call it only on a definition Assert-ParallelInstanceDefinition accepted.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][hashtable] $Definition)

    $source = Get-MesIngestSource -Definition $Definition
    $types = @($Definition['journeyRuntime']['allowedWorkTypes']) -join ','
    $line = "MES_INGEST_SOURCE=$source baseUrl=$($Definition['mesIngest']['baseUrl']) allowedWorkTypes=$types"
    if ($source -ceq 'production' -and (Test-ParallelProductionCutoverDefinition -Definition $Definition)) {
        return "$line -- PRODUCTION CUTOVER ($($Definition['productionCutover']['phase'])): this instance reads the PRODUCTION MesIngest for all seven task types and drives agv01; the MVP must be stopped (control-server#564)"
    }
    if ($source -ceq 'production') {
        return "$line -- this instance reads the PRODUCTION MesIngest the MVP also reads; it may take STAGING_TO_WIRE only, the MVP keeps WIRE_TO_GATE (user decision 2026-10-09, control-server#535)"
    }
    return "$line -- injected demand from this instance's own FakeMesIngest double, task '$($Definition['fakeMesIngest']['taskName'])'"
}

function Get-ParallelMesIngestSecretRefusal {
    <#
        .SYNOPSIS
            control-server#535. $null, or why a 'production' install must not go ahead without the
            MesIngest shared secret.

        .DESCRIPTION
            The production MesIngest requires a Bearer token on every request (its host binds a
            non-loopback address, and its middleware does not tell callers apart). Without
            CONTROL_SERVER_MES_INGEST_SHARED_SECRET in the service's Environment every read is 401 and
            nothing is dispatched -- the safe direction, but an install that looks done and does
            nothing. So it is refused before anything changes. 'fake' mode does not need it: the
            double checks no token.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][hashtable] $Definition,
        [AllowNull()][AllowEmptyString()][string] $SharedSecret
    )
    if ((Get-MesIngestSource -Definition $Definition) -cne 'production') { return $null }
    if (-not [string]::IsNullOrWhiteSpace($SharedSecret)) { return $null }
    return ("mesIngest.source is 'production', but there is no MesIngest shared secret to write into the service's " +
        "Environment (deploy-config.json mesIngestSharedSecret, or on a rollback CONTROL_SERVER_MES_INGEST_SHARED_SECRET already " +
        "in the service). Every read of the production MesIngest would be 401 and nothing would be dispatched. Add " +
        "'MesIngestSharedSecret' to the control host's secret store with Set-ControlServerSecret.ps1. Nothing was stopped or changed.")
}

function Get-ParallelRetiredFakeMesIngest {
    <#
        .SYNOPSIS
            control-server#535. The previous install's FakeMesIngest task and directory, when this
            install switches the instance to 'production'; otherwise $null.

        .DESCRIPTION
            The uninstaller removes what installed-instance.json names, and an install records the new
            definition there before it does anything else. A 'production' definition names no double,
            so a double left from an earlier 'fake' install would never be found again -- a SYSTEM task
            restarting a process on a port nobody reads. The installer retires it (task, process,
            directory) before recording the new definition, so the record keeps naming it until it is gone.

            The previous definition is asserted first, as an uninstall would (-ForStopDirection): a task
            name or directory is acted on only when it passed the same checks. A previous definition
            that does not pass throws, before anything changes.

            control-server#775: a previous definition that is itself 'production' names no double, so there
            is nothing to retire and it is not asserted. Asserting it took this version's rules to an older
            install's definition: the v4 -> v5 cutover upgrade was refused because the installed definition
            had the six types and agv01 three, which the v5 cutover rules no longer accept.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()] $Previous,
        [Parameter(Mandatory = $true)][hashtable] $Current
    )
    if ($null -eq $Previous) { return $null }
    if ((Get-MesIngestSource -Definition $Current) -cne 'production') { return $null }
    if ($Previous -isnot [hashtable]) { throw 'The previously installed definition is not a JSON object; its FakeMesIngest cannot be retired.' }
    if ((Get-MesIngestSource -Definition $Previous) -ceq 'production') { return $null }
    # control-server#564: an installed cutover definition carries its record; it got there only through an install with the switch.
    [string[]] $failures = @(Test-ParallelInstanceDefinition -Definition $Previous -AllowRiotCreateDispatch -AllowRiotForeignOrderCancel -ForStopDirection `
            -ProductionCutover:(Test-ParallelProductionCutoverDefinition -Definition $Previous))
    if ($failures.Count -gt 0) {
        throw ("The previously installed definition was refused, so its FakeMesIngest is not retired and nothing was changed:" +
            [Environment]::NewLine + (($failures | ForEach-Object { "  - $_" }) -join [Environment]::NewLine))
    }
    $previousLayout = Get-ParallelInstanceLayout -Definition $Previous
    if ($null -eq $previousLayout.TaskName) { return $null }
    return [pscustomobject]@{
        TaskName = $previousLayout.TaskName
        FakeInstallRoot = $previousLayout.FakeInstallRoot
    }
}

function Find-ParallelEffectiveConfiguration {
    <#
        .SYNOPSIS
            control-server#535 review M2. The newest (by @t) EFFECTIVE_CONFIGURATION event the Host logged at or
            after -Since, from Serilog compact JSON lines; $null when there is none.

        .DESCRIPTION
            Program.cs logs it once the Host has started: the AllowedWorkTypes and AllowedDispatchZones
            it really bound, the MesIngest baseUrl it reads and, since control-server#571, the vehicle
            roster (Fleet: AgvId, VehicleKey, AllowedTaskTypes, Zones per car; an empty list for one car).
            Fleet is $null when the event does not carry it -- a package older than #571 -- which is not
            the same as an empty roster. Since control-server#638 it also carries ClearanceToWaitingPointEnabled,
            $null when the event does not, and since control-server#531 MapStationCatalog (ApprovedSyncPeriod,
            ApprovedMaxUnconfirmed), $null when the event does not, and since control-server#672 JourneyBinding (the admission
            policy version and deployment label, the cargo holding and station departure waits), $null when it carries none. That -- not the definition, not the
            overlay file -- is what the deployment's read-back compares, because M1 was a definition and
            an overlay that both said ["STAGING_TO_WIRE"] while the Host bound six types. -Since is the
            service process's start: a line from an earlier process proves nothing about this one.
            Lines that are not JSON, or not that event, are skipped.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][string[]] $Lines,
        [Parameter(Mandatory = $true)][datetimeoffset] $Since
    )
    $found = $null
    foreach ($line in @($Lines)) {
        if ([string]::IsNullOrWhiteSpace($line) -or -not $line.TrimStart().StartsWith('{')) { continue }
        try { $event = ConvertFrom-Json -InputObject $line -AsHashtable -Depth 10 } catch { continue }
        if ($event -isnot [hashtable] -or $event['@mt'] -isnot [string] -or
            -not $event['@mt'].StartsWith('EFFECTIVE_CONFIGURATION ', [StringComparison]::Ordinal)) { continue }
        # ConvertFrom-Json turns an ISO-8601 '@t' into a DateTime (or DateTimeOffset) itself; formatting that
        # back into a string would drop the sub-second part and depend on the machine's culture.
        $raw = $event['@t']
        [datetimeoffset] $at = [datetimeoffset]::MinValue
        if ($raw -is [datetimeoffset]) {
            $at = $raw
        } elseif ($raw -is [datetime]) {
            $at = [datetimeoffset]::new(($raw.Kind -eq [DateTimeKind]::Unspecified) ? [datetime]::SpecifyKind($raw, [DateTimeKind]::Utc) : $raw.ToUniversalTime())
        } elseif (-not [datetimeoffset]::TryParse([string] $raw, [cultureinfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal, [ref] $at)) { continue }
        if ($at -lt $Since) { continue }
        # The newest by @t, not the last one met: the installer reads the newest file first (re-review S5).
        if ($null -ne $found -and $at -le $found.At) { continue }
        $fleet = $null
        if ($event.ContainsKey('Fleet')) {
            $fleet = @(@($event['Fleet']) | ForEach-Object {
                    $vehicle = ($_ -is [System.Collections.IDictionary]) ? $_ : @{}
                    [pscustomobject]@{
                        AgvId = [string] $vehicle['AgvId']
                        VehicleKey = [string] $vehicle['VehicleKey']
                        AllowedTaskTypes = [string[]] @($vehicle['AllowedTaskTypes'])
                        Zones = [string[]] @($vehicle['Zones'])
                    }
                })
        }
        $found = [pscustomobject]@{
            At = $at
            AllowedWorkTypes = [string[]] @($event['AllowedWorkTypes'])
            AllowedDispatchZones = [string[]] @($event['AllowedDispatchZones'])
            MesIngestBaseUrl = [string] $event['MesIngestBaseUrl']
            Fleet = $fleet
            # control-server#638. As logged, not cast: $null when the event does not carry it (a package older than #638),
            # and anything that is not a JSON boolean is compared as it is, so it cannot read as false.
            ClearanceToWaitingPointEnabled = $event.ContainsKey('ClearanceToWaitingPointEnabled') ? $event['ClearanceToWaitingPointEnabled'] : $null
            # control-server#531. $null when the event does not carry it (a package older than #531); otherwise the two values
            # as logged -- "hh:mm:ss" strings, or $null for one the Host left unset -- not cast, so nothing else reads as a match.
            MapStationCatalog = ($event.ContainsKey('MapStationCatalog') -and $event['MapStationCatalog'] -is [System.Collections.IDictionary]) ?
                [pscustomobject]@{
                    ApprovedSyncPeriod = $event['MapStationCatalog']['ApprovedSyncPeriod']
                    ApprovedMaxUnconfirmed = $event['MapStationCatalog']['ApprovedMaxUnconfirmed']
                } : ($event.ContainsKey('MapStationCatalog') ?
                    # Logged, but not as an object: matches nothing, rather than reading as "not approved".
                    [pscustomobject]@{ ApprovedSyncPeriod = '(not an object)'; ApprovedMaxUnconfirmed = '(not an object)' } : $null)
            # control-server#672. One property per row of $script:JourneyBindingFields, keyed by its Logged name: $null as a whole
            # when the event carries none of them (a package older than #672); otherwise each as logged, not cast, and $null for
            # one missing from the event.
            JourneyBinding = Read-ParallelJourneyBinding -LoggedEvent $event
        }
    }
    return $found
}

function Get-ParallelEffectiveConfigurationRefusal {
    <#
        .SYNOPSIS
            control-server#535 review M2. $null when what the Host bound is exactly what the definition
            says; otherwise why not.

        .DESCRIPTION
            Each list is compared whole, in order, ordinal. $null for -Effective (no event found) is
            EFFECTIVE_CONFIGURATION_UNREAD: no evidence is not a pass. 'production' is additionally held to
            ["STAGING_TO_WIRE"] here, so a definition that slipped past the checks still cannot read back green.

            control-server#571: the roster is compared car by car, keyed by VehicleKey -- a car missing, a car
            extra, and per car its agvId, allowedTaskTypes and zones, each list whole, in order, ordinal. A
            definition with a roster read back from an event without one (a package older than #571) is
            EFFECTIVE_CONFIGURATION_FLEET_UNREAD; a single-car definition accepts that event as before, but not
            an event showing a roster it did not write (one left over from an earlier two-car install).

            control-server#638: clearanceToWaitingPointEnabled (absent in the definition is false) is compared with what
            the Host bound; anything but that JSON boolean is a mismatch. An event without it (a package older than #638)
            is EFFECTIVE_CONFIGURATION_CLEARANCE_UNREAD when the definition turns it on -- an unconfirmed "on" is not a pass
            -- and accepted as before when the definition leaves it off.

            control-server#531: mapStationCatalog's two approved values (absent in the definition is null, "not approved")
            are compared with what the Host bound; any other value is a mismatch. An event without them (a package older
            than #531) is accepted -- the overlay wrote them all the same, and a refusal there would block every rollback.

            control-server#672: admissionPolicyVersion (a JSON integer), admissionPolicyDeploymentId (ordinal), cargoHoldingTimeout and
            stationDepartureWaitTimeout (hh:mm:ss) are compared with what the Host bound, each when the definition names it. An
            event without them (a package older than #672) is accepted here, as #531's pair is.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][hashtable] $Definition,
        [AllowNull()] $Effective
    )
    if ($null -eq $Effective) {
        return 'EFFECTIVE_CONFIGURATION_UNREAD: the Host logged no EFFECTIVE_CONFIGURATION event since its process started, so what it really bound is unknown.'
    }
    [string[]] $problems = @()
    $journey = $Definition['journeyRuntime']
    foreach ($pair in @(
            @{ Name = 'allowedWorkTypes'; Expected = @($journey['allowedWorkTypes']); Actual = @($Effective.AllowedWorkTypes) }
            @{ Name = 'allowedDispatchZones'; Expected = @($journey['allowedDispatchZones']); Actual = @($Effective.AllowedDispatchZones) })) {
        if ((@($pair.Expected) -join "`n") -cne (@($pair.Actual) -join "`n")) {
            $problems += "$($pair.Name) bound [$(@($pair.Actual) -join ',')], the definition says [$(@($pair.Expected) -join ',')]"
        }
    }
    # control-server#564: a cutover definition (it passed the checks with the switch) is held to the full seven instead, as a
    # set -- the definition's own order is compared above.
    $cutoverDefinition = Test-ParallelProductionCutoverDefinition -Definition $Definition
    if ((Get-MesIngestSource -Definition $Definition) -ceq 'production' -and $cutoverDefinition) {
        $boundTypes = @($Effective.AllowedWorkTypes | Sort-Object -CaseSensitive) -join "`n"
        if ($boundTypes -cne (@($script:CutoverWorkTypes | Sort-Object -CaseSensitive) -join "`n")) {
            $problems += "production cutover, but allowedWorkTypes bound [$(@($Effective.AllowedWorkTypes) -join ',')], not the seven types"
        }
    } elseif ((Get-MesIngestSource -Definition $Definition) -ceq 'production' -and
        (@($Effective.AllowedWorkTypes) -join "`n") -cne ($script:ProductionSourceWorkTypes -join "`n")) {
        $mvpNote = (@($Effective.AllowedWorkTypes) -ccontains $script:MvpWorkType) ? " -- $script:MvpWorkType is the MVP's" : ''
        $problems += "production MesIngest source, but allowedWorkTypes bound [$(@($Effective.AllowedWorkTypes) -join ',')]$mvpNote"
    }
    if ($Effective.MesIngestBaseUrl -cne [string] $Definition['mesIngest']['baseUrl']) {
        $problems += "mesIngestBaseUrl bound '$($Effective.MesIngestBaseUrl)', the definition says '$($Definition['mesIngest']['baseUrl'])'"
    }
    $definesFleet = $journey.ContainsKey('fleet')
    $hasFleet = $null -ne $Effective.PSObject.Properties['Fleet'] -and $null -ne $Effective.Fleet
    if ($definesFleet -and -not $hasFleet) {
        return 'EFFECTIVE_CONFIGURATION_FLEET_UNREAD: the definition has a vehicle roster, and the Host''s EFFECTIVE_CONFIGURATION event does not carry one (a package older than control-server#571), so which cars it really bound is unknown.'
    }
    if ($hasFleet) {
        $problems += @(Compare-ParallelEffectiveFleet -Defined ($definesFleet ? @($journey['fleet']) : @()) -Bound @($Effective.Fleet))
    }
    $clearanceExpected = Get-ParallelClearanceToWaitingPoint -Definition $Definition
    $clearanceBound = ($null -ne $Effective.PSObject.Properties['ClearanceToWaitingPointEnabled']) ? $Effective.ClearanceToWaitingPointEnabled : $null
    if ($null -eq $clearanceBound) {
        if ($clearanceExpected) {
            return 'EFFECTIVE_CONFIGURATION_CLEARANCE_UNREAD: the definition turns journeyRuntime.clearanceToWaitingPointEnabled on, and the Host''s EFFECTIVE_CONFIGURATION event does not carry it (a package older than control-server#638), so whether vehicles will drive themselves to a waiting point is unknown.'
        }
    } elseif ($clearanceBound -isnot [bool] -or $clearanceBound -ne $clearanceExpected) {
        $problems += "clearanceToWaitingPointEnabled bound '$clearanceBound', the definition says '$clearanceExpected'"
    }
    # control-server#531. The two approved values, compared as the strings the overlay writes and the Host logs ("hh:mm:ss",
    # or null for "not approved"). An event without them is a package older than #531: accepted, as the switch above is
    # when off -- the overlay still wrote them, and a refusal would make a rollback to such a package impossible.
    $catalogBound = ($null -ne $Effective.PSObject.Properties['MapStationCatalog']) ? $Effective.MapStationCatalog : $null
    if ($null -ne $catalogBound) {
        $catalogExpected = Get-ParallelMapStationCatalogApproval -Definition $Definition
        foreach ($pair in @(@('approvedSyncPeriod', 'ApprovedSyncPeriod'), @('approvedMaxUnconfirmed', 'ApprovedMaxUnconfirmed'))) {
            $expected = $catalogExpected[$pair[0]]
            $actual = $catalogBound.($pair[1])
            $same = ($null -eq $expected) ? ($null -eq $actual) : ($actual -is [string] -and $actual -ceq $expected)
            if (-not $same) {
                $problems += "mapStationCatalog.$($pair[0]) bound '$($actual ?? 'null')', the definition says '$($expected ?? 'null')'"
            }
        }
    }
    # control-server#672. Every row of $script:JourneyBindingFields, compared when the definition gives it a value. An event
    # without any of them is a package older than #672: accepted, as #531's pair is, so a rollback to such a package is not
    # blocked; 19-deploy-control-server-parallel.ps1 refuses that on an install that is not a rollback (EFFECTIVE_JOURNEY_BINDING=not-logged).
    $problems += @(Compare-ParallelJourneyBinding -Definition $Definition -Effective $Effective)
    if ($problems.Count -eq 0) { return $null }
    return "EFFECTIVE_CONFIGURATION_MISMATCH: $($problems -join '; ')."
}

# control-server#672. The values the read-back compares beyond the lists and the roster, one row each, in the order
# EFFECTIVE_JOURNEY_BINDING= prints them. Find-ParallelEffectiveConfiguration reads, Get-ParallelEffectiveConfigurationRefusal
# compares and Format-ParallelJourneyBindingLine prints by walking this table, nothing else names a field:
#   Name     what the problem message and the result line call it;
#   Logged   the property of the EFFECTIVE_CONFIGURATION event (EffectiveConfigurationEvent.cs) it is read from, as logged;
#   Compare  Integer (a JSON integer, equal; a string "6" does not count), Ordinal (a string, ordinal), Duration (a string
#            equal to the expected value as "hh:mm:ss", [timespan] 'c', which is how the Host logs a TimeSpan) or Boolean (a
#            JSON boolean, equal; a string "True" does not count);
#   Source   where the expected value comes from, resolved by Get-ParallelJourneyBindingExpected: 'journeyRuntime.<key>' (or
#            'journeyRuntime.<section>.<key>', one level down) is that key as the definition writes it, $null (printed, not
#            compared) when it leaves it to the package's
#            appsettings.json, which this cannot know. A value derived from something else (from a phase, say) is a named
#            Source with its own case in that function -- not a scriptblock here: this module allows no dynamic command
#            (the delete guard in Test-ParallelInstance.ps1);
#   AbsentMeans  $null, or what a package that does not log this row did bind: the row is then compared as if it had logged
#            that value -- in an event that logs none of the table (older than #672, otherwise accepted whole) and in one that
#            logs the others (between #672 and the row's ticket; review M1). control-server#686: a package that does not log
#            the gate does not have it, which is Off -- so a production install of it is refused and a trial one is not.
# A new field is one row here, the event field in EffectiveConfigurationEvent.cs, and its cases in Test-ParallelInstance.ps1
# ($bindingFieldCases) and ParallelInstanceEffectiveConfigurationTests; 19 compares the result line field by field, by name.
$script:JourneyBindingFields = @(
    [pscustomobject]@{ Name = 'admissionPolicyVersion'; Logged = 'AdmissionPolicyVersion'; Compare = 'Integer'; Source = 'journeyRuntime.admissionPolicyVersion'; AbsentMeans = $null }
    [pscustomobject]@{ Name = 'admissionPolicyDeploymentId'; Logged = 'AdmissionPolicyDeploymentId'; Compare = 'Ordinal'; Source = 'journeyRuntime.admissionPolicyDeploymentId'; AbsentMeans = $null }
    [pscustomobject]@{ Name = 'cargoHoldingTimeout'; Logged = 'CargoHoldingTimeout'; Compare = 'Duration'; Source = 'journeyRuntime.cargoHoldingTimeout'; AbsentMeans = $null }
    [pscustomobject]@{ Name = 'stationDepartureWaitTimeout'; Logged = 'StationDepartureWaitTimeout'; Compare = 'Duration'; Source = 'journeyRuntime.stationDepartureWaitTimeout'; AbsentMeans = $null }
    # control-server#686. 'Off' or 'Enforcing', never a space.
    [pscustomobject]@{ Name = 'slotConfigurationReadinessGate'; Logged = 'SlotConfigurationReadinessGate'; Compare = 'Ordinal'; Source = 'productionCutover.phase'; AbsentMeans = 'Off' }
    # program#184. The staging station's departure rules switch (control-server#709, logged since its review L5). A package that
    # does not log it does not have the rules, which is off: a definition that turns them on is refused over such a package,
    # one that leaves the section out is not compared. Printed as True / False, never a space.
    [pscustomobject]@{ Name = 'stagingDepartureRulesEnabled'; Logged = 'StagingDepartureRulesEnabled'; Compare = 'Boolean'; Source = 'journeyRuntime.stagingDepartureRules.enabled'; AbsentMeans = $false }
    # control-server#781. The idle return switch, outside journeyRuntime. Derived like the gate, so always compared: the
    # definition's boolean, false when it leaves the section out -- the value the overlay always writes. An event without it
    # (a package older than #781) is compared as off: a definition that turns idle return on is refused over such a package, one
    # that leaves it off is not. That proves nothing about what such a package bound -- its Host does not say. Off is guaranteed by
    # this module's overlay, which always writes IdleReturn:enabled, not by the read-back; an install from a checkout older than
    # #781 writes no such key, and Update-ControlServerLocal.ps1 carries the installed appsettings.Production.json forward, so a
    # true left there survives it (cs#781 review H2): set it to false by hand before installing with such a checkout. Printed
    # as True / False, never a space.
    [pscustomobject]@{ Name = 'idleReturnEnabled'; Logged = 'IdleReturnEnabled'; Compare = 'Boolean'; Source = 'idleReturn.enabled'; AbsentMeans = $false }
    # control-server#828 review M1. The catalog age window (REQ-0366, control-server#827) and the trial sublot rule (REQ-0368),
    # always compared against what the overlay writes ($script:JourneyOverlayDefaults when the definition leaves them out).
    # The window's AbsentMeans is the default, 00:30:00, like idle return's: a package that does not log it (older than #827)
    # passes when the definition leaves the window at the default, so a rollback to v5 stays possible, and is refused when the
    # definition sets another value. That proves nothing about such a package, which has no window at all. The rule's two rows
    # read a package older than #828 as the rule off, which is what such a package has: a definition that turns it on (the
    # production stages) is refused over it -- a rollback to it with that definition included -- and one that leaves it off is
    # not. Printed without a space: hh:mm:ss, a number, and characters the definition check and the Host refuse white space in
    # (review L4); off prints as 'trialSublotCharacters='.
    [pscustomobject]@{ Name = 'catalogAgeWindow'; Logged = 'CatalogAgeWindow'; Compare = 'Duration'; Source = 'overlay.catalogAgeWindow'; AbsentMeans = '00:30:00' }
    [pscustomobject]@{ Name = 'trialSublotCharacterPosition'; Logged = 'TrialSublotCharacterPosition'; Compare = 'Integer'; Source = 'overlay.trialSublotCharacterPosition'; AbsentMeans = 0 }
    [pscustomobject]@{ Name = 'trialSublotCharacters'; Logged = 'TrialSublotCharacters'; Compare = 'Ordinal'; Source = 'overlay.trialSublotCharacters'; AbsentMeans = '' }
)

# control-server#828. The journeyRuntime keys the overlay always writes, with the value it writes when the definition leaves the
# key out (REQ-0366's thirty minutes; the trial sublot rule off). New-ParallelInstanceConfigurationOverlay writes them and the
# read-back's 'overlay.<key>' rows compare against them, so the two cannot disagree.
$script:JourneyOverlayDefaults = [ordered]@{
    catalogAgeWindow = '00:30:00'
    trialSublotCharacterPosition = 0
    trialSublotCharacters = ''
}

function Get-ParallelJourneyOverlayValue {
    <# control-server#828. The value the overlay writes for one of $script:JourneyOverlayDefaults: the definition's, or the default. #>
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition, [Parameter(Mandatory = $true)][string] $Key)
    $journey = $Definition['journeyRuntime']
    return ($journey -is [System.Collections.IDictionary] -and $journey.Contains($Key)) ? $journey[$Key] : $script:JourneyOverlayDefaults[$Key]
}

function Get-ParallelJourneyBindingExpected {
    <# control-server#672. The value a row of $script:JourneyBindingFields expects, from its Source; $null when not compared. #>
    param([Parameter(Mandatory = $true)] $Field, [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    if ($Field.Source -match '^journeyRuntime\.(?:(?<section>[A-Za-z]+)\.)?(?<key>[A-Za-z]+)$') {
        $node = $Definition['journeyRuntime']
        # program#184: one level down, for journeyRuntime.stagingDepartureRules.enabled. A section that is not an object has no
        # value to compare; Test-ParallelInstanceDefinition refuses such a definition before anything reads it here.
        if ($Matches['section'] -and $node -is [System.Collections.IDictionary]) {
            $node = $node.Contains($Matches['section']) ? $node[$Matches['section']] : $null
        }
        return ($node -is [System.Collections.IDictionary] -and $node.Contains($Matches['key'])) ? $node[$Matches['key']] : $null
    }
    # control-server#686. Derived, so always compared: Enforcing for a production phase, Off for every other definition.
    if ($Field.Source -ceq 'productionCutover.phase') { return Get-ParallelSlotConfigurationGateMode -Definition $Definition }
    # control-server#781. Derived, so always compared: the definition's idleReturn.enabled, false when it leaves the section out.
    if ($Field.Source -ceq 'idleReturn.enabled') { return Test-ParallelIdleReturnEnabled -Definition $Definition }
    # control-server#828. Derived, so always compared: what the overlay writes for that key.
    if ($Field.Source -cmatch '^overlay\.(?<key>[A-Za-z]+)$' -and $script:JourneyOverlayDefaults.Contains($Matches['key'])) {
        return Get-ParallelJourneyOverlayValue -Definition $Definition -Key $Matches['key']
    }
    throw "JourneyBindingFields: $($Field.Name) has an unknown Source '$($Field.Source)'."
}

function Read-ParallelJourneyBinding {
    <# control-server#672. The event's values for every row of $script:JourneyBindingFields, or $null when it carries none. #>
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $LoggedEvent)
    if (@($script:JourneyBindingFields | Where-Object { $LoggedEvent.Contains($_.Logged) }).Count -eq 0) { return $null }
    $bound = [ordered]@{}
    foreach ($field in $script:JourneyBindingFields) { $bound[$field.Logged] = $LoggedEvent.Contains($field.Logged) ? $LoggedEvent[$field.Logged] : $null }
    return [pscustomobject] $bound
}

function Compare-ParallelJourneyBinding {
    <#
        control-server#672. One problem per row of $script:JourneyBindingFields whose bound value differs from what the definition
        means, naming the row; nothing when they all agree. An event that carries none of them (a package older than #672) is
        compared only on the rows with an AbsentMeans (control-server#686), each as if it had logged that value.
    #>
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition, [AllowNull()] $Effective)
    $bound = ($null -ne $Effective -and $null -ne $Effective.PSObject.Properties['JourneyBinding']) ? $Effective.JourneyBinding : $null
    if ($null -eq $bound) {
        if ($null -eq $Effective) { return }
        foreach ($field in @($script:JourneyBindingFields | Where-Object { $null -ne $_.AbsentMeans })) {
            $expected = Get-ParallelJourneyBindingExpected -Field $field -Definition $Definition
            if ($null -ne $expected -and -not ([string] $expected -ceq [string] $field.AbsentMeans)) {
                "$($field.Name) bound 'not logged', the definition says '$expected'"
            }
        }
        return
    }
    foreach ($field in $script:JourneyBindingFields) {
        $expected = Get-ParallelJourneyBindingExpected -Field $field -Definition $Definition
        if ($null -eq $expected) { continue }
        $actual = $bound.($field.Logged)
        # control-server#686 review M1: a row the event leaves out while it logs others (a package between #672 and that row's
        # ticket) is read as its AbsentMeans too, not only when the event logs none of the table. The message still says
        # 'not logged'.
        $logged = $actual
        if ($null -eq $actual -and $null -ne $field.AbsentMeans) { $actual = $field.AbsentMeans }
        $same = switch ($field.Compare) {
            'Integer' {
                $wanted = ConvertTo-IntegerOrNull $expected
                ($actual -is [long] -or $actual -is [int]) -and $null -ne $wanted -and [long] $actual -eq [long] $wanted
            }
            'Ordinal' { $actual -is [string] -and $actual -ceq [string] $expected }
            'Duration' {
                # Not hh:mm:ss in the definition: compared as written, so it matches nothing the Host logs.
                $span = ConvertTo-ParallelCatalogSpan $expected
                $expected = ($null -ne $span) ? $span.ToString('c', [cultureinfo]::InvariantCulture) : [string] $expected
                $actual -is [string] -and $actual -ceq $expected
            }
            # program#184. Both sides JSON booleans; the definition's is one by Test-ParallelInstanceDefinition.
            'Boolean' { $actual -is [bool] -and $expected -is [bool] -and $actual -eq $expected }
            default { throw "JourneyBindingFields: $($field.Name) has an unknown Compare '$($field.Compare)'." }
        }
        if (-not $same) {
            "$($field.Name) bound '$($logged ?? 'not logged')', the definition says '$expected'"
        }
    }
}

function Format-ParallelJourneyBindingLine {
    <#
        .SYNOPSIS
            control-server#672. The EFFECTIVE_JOURNEY_BINDING= result line the installer prints after its read-back, for
            19-deploy-control-server-parallel.ps1 to compare with the definition it sent.

        .DESCRIPTION
            One name=value per row of $script:JourneyBindingFields, as the Host logged it, in table order, separated by one space:
            EFFECTIVE_JOURNEY_BINDING=admissionPolicyVersion=6 admissionPolicyDeploymentId=... cargoHoldingTimeout=00:05:00 stationDepartureWaitTimeout=00:05:00
            -- and exactly EFFECTIVE_JOURNEY_BINDING=not-logged when the event carries none of them (a package older than #672).
            A separate line, not more fields on EFFECTIVE_CONFIGURATION=: a 19 older than #672 compares that one whole.
            Values are not escaped: a value must not contain a space. 19 splits the line on spaces into name=value pairs and
            refuses a piece without '=' (EFFECTIVE_JOURNEY_BINDING_MISMATCH ... not name=value pairs), so a field added to
            $script:JourneyBindingFields whose value can hold a space would fail 19 after a good install (cs#672 review S2).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)] $Effective)
    $bound = ($null -ne $Effective.PSObject.Properties['JourneyBinding']) ? $Effective.JourneyBinding : $null
    if ($null -eq $bound) { return 'EFFECTIVE_JOURNEY_BINDING=not-logged' }
    return 'EFFECTIVE_JOURNEY_BINDING=' + (@($script:JourneyBindingFields | ForEach-Object { "$($_.Name)=$($bound.($_.Logged) ?? 'not-logged')" }) -join ' ')
}

function Get-ParallelClearanceToWaitingPoint {
    <#
        .SYNOPSIS
            control-server#638. journeyRuntime.clearanceToWaitingPointEnabled as the overlay writes it and the read-back
            expects it: the definition's boolean, false when it leaves the key out (the product default).
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory = $true)][hashtable] $Definition)
    $journey = $Definition['journeyRuntime']
    return ($journey -is [System.Collections.IDictionary] -and $journey.Contains('clearanceToWaitingPointEnabled') -and
        $journey['clearanceToWaitingPointEnabled'] -is [bool]) ? $journey['clearanceToWaitingPointEnabled'] : $false
}

function Compare-ParallelEffectiveFleet {
    <#
        control-server#571. Every difference between the defined roster and the bound one, car by car, keyed by
        VehicleKey (ordinal): missing, extra, bound twice, and per car agvId, allowedTaskTypes, zones.
    #>
    param([object[]] $Defined, [object[]] $Bound)
    [string[]] $problems = @()
    $Defined = @($Defined | Where-Object { $_ -is [System.Collections.IDictionary] })
    $Bound = @($Bound | Where-Object { $null -ne $_ })
    foreach ($row in $Defined) {
        $key = [string] $row['vehicleKey']
        $sameKey = @($Bound | Where-Object { [string]::Equals($_.VehicleKey, $key, [StringComparison]::Ordinal) })
        if ($sameKey.Count -eq 0) {
            $problems += "fleet: $key ('$($row['agvId'])') is in the definition, the Host did not bind it"
            continue
        }
        if ($sameKey.Count -gt 1) {
            $problems += "fleet: $key is bound $($sameKey.Count) times"
        }
        $car = $sameKey[0]
        if (-not [string]::Equals($car.AgvId, [string] $row['agvId'], [StringComparison]::Ordinal)) {
            $problems += "fleet: $key agvId bound '$($car.AgvId)', the definition says '$($row['agvId'])'"
        }
        foreach ($pair in @(
                @{ Name = 'allowedTaskTypes'; Expected = @($row['allowedTaskTypes']); Actual = @($car.AllowedTaskTypes) }
                @{ Name = 'zones'; Expected = @($row['zones']); Actual = @($car.Zones) })) {
            if ((@($pair.Expected) -join "`n") -cne (@($pair.Actual) -join "`n")) {
                $problems += "fleet: $key $($pair.Name) bound [$(@($pair.Actual) -join ',')], the definition says [$(@($pair.Expected) -join ',')]"
            }
        }
    }
    foreach ($car in $Bound) {
        if (-not ($Defined | Where-Object { [string]::Equals([string] $_['vehicleKey'], $car.VehicleKey, [StringComparison]::Ordinal) })) {
            $problems += "fleet: $($car.VehicleKey) ('$($car.AgvId)') is bound, the definition does not name it"
        }
    }
    return $problems
}

function Get-ParallelEffectiveConfigurationAction {
    <#
        .SYNOPSIS
            control-server#535 re-review S2. What the installer does with a read-back: Pass, Warn, or
            StopServiceAndRefuse, with the message.

        .DESCRIPTION
            A Host that bound something other than its definition must not keep running: in 'production' it
            may be reading the production catalog with WIRE_TO_GATE allowed -- a first production install
            rolled back onto a pre-#535 'fake' package does exactly that, since that package still merges
            the list by index. So a mismatch in either mode, and nothing read back in 'production', stop the
            V2 service before the installer throws. Nothing read back in 'fake' only warns: a package older
            than #535 does not log the event, and its lists are the package's own length -- unless the
            definition has a vehicle roster (control-server#571): which cars the Host drives is not something
            to warn about, so that, too, stops the service; nor, since control-server#638, is whether vehicles drive
            themselves to a waiting point (journeyRuntime.clearanceToWaitingPointEnabled on).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable] $Definition,
        [AllowNull()] $Effective
    )
    $message = Get-ParallelEffectiveConfigurationRefusal -Definition $Definition -Effective $Effective
    $action = if ($null -eq $message) { 'Pass' }
        elseif ($null -eq $Effective -and (Get-MesIngestSource -Definition $Definition) -cne 'production' -and
            -not $Definition['journeyRuntime'].ContainsKey('fleet') -and
            -not (Get-ParallelClearanceToWaitingPoint -Definition $Definition)) { 'Warn' }
        else { 'StopServiceAndRefuse' }
    return [pscustomobject]@{ Action = $action; Message = $message }
}

# control-server#578. What a Host whose journey runtime is held off must not have logged. Accepting a demand, the RIoT
# create for it and the plan sent to a vehicle log no event of their own -- they are written to the database -- so this
# looks for any sign that the loop doing them ran: anything at all under the engine's category (DispatchRoundRunner logs
# under it too), anything from the worker but its "disabled" line, any request through the MesIngest catalog client
# (the first thing every dispatch round does), and the effect events logged under other categories.
$script:HeldRuntimeDisabledEventId = 2001
$script:HeldRuntimeWorkerSource = 'ControlServer.Host.Runtime.JourneyRuntimeWorker'
$script:HeldRuntimeActivitySources = @(
    'ControlServer.Host.Runtime.JourneyRuntimeEngine'
    'System.Net.Http.HttpClient.IMesIngestCatalog.'
)
$script:HeldRuntimeActivityEventIds = @(
    2150, 2151, 2154,                   # demand release (Release/DemandReleaseService)
    2170, 2171, 2172, 2173, 2174,       # own order rebuild (a RIoT re-create)
    2198, 2220, 2223,                   # idle return committed / materialized
    2240, 2250, 2251, 2252, 2254, 2255, # charging order
    2300                                # clearance move
)

function Get-ParallelHeldRuntimeRefusal {
    <#
        .SYNOPSIS
            control-server#578. $null when the Host's log since -Since shows its journey runtime held off; otherwise
            JOURNEY_RUNTIME_NOT_HELD and why.

        .DESCRIPTION
            Two facts, both from lines at or after -Since (the service process's start), Serilog compact JSON:
              * the worker logged event 2001 ("Journey runtime is disabled") -- the runtime read enabled=false.
                Without it a configuration layer above appsettings.Production.json (an environment variable, say)
                may have set it true;
              * nothing shows the dispatch loop ran ($script:HeldRuntimeActivitySources / ActivityEventIds).
            Lines that are not JSON are skipped, as in Find-ParallelEffectiveConfiguration.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowEmptyCollection()][string[]] $Lines,
        [Parameter(Mandatory = $true)][datetimeoffset] $Since
    )
    $disabledSeen = $false
    [string[]] $activity = @()
    foreach ($line in @($Lines)) {
        if ([string]::IsNullOrWhiteSpace($line) -or -not $line.TrimStart().StartsWith('{')) { continue }
        try { $event = ConvertFrom-Json -InputObject $line -AsHashtable -Depth 10 } catch { continue }
        if ($event -isnot [hashtable]) { continue }
        $raw = $event['@t']
        [datetimeoffset] $at = [datetimeoffset]::MinValue
        if ($raw -is [datetimeoffset]) {
            $at = $raw
        } elseif ($raw -is [datetime]) {
            $at = [datetimeoffset]::new(($raw.Kind -eq [DateTimeKind]::Unspecified) ? [datetime]::SpecifyKind($raw, [DateTimeKind]::Utc) : $raw.ToUniversalTime())
        } elseif (-not [datetimeoffset]::TryParse([string] $raw, [cultureinfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal, [ref] $at)) { continue }
        if ($at -lt $Since) { continue }
        $source = [string] $event['SourceContext']
        $id = ($event['EventId'] -is [System.Collections.IDictionary]) ? $event['EventId']['Id'] : $null
        $id = ($id -is [ValueType]) ? [int] $id : $null
        if ($source -ceq $script:HeldRuntimeWorkerSource) {
            if ($id -eq $script:HeldRuntimeDisabledEventId) { $disabledSeen = $true } else { $activity += "event $id from $source" }
            continue
        }
        if (@($script:HeldRuntimeActivitySources | Where-Object { $source.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0) {
            $activity += "event $id from $source$(if ($event['Uri']) { " ($($event['Uri']))" })"
            continue
        }
        if ($null -ne $id -and $script:HeldRuntimeActivityEventIds -contains $id) {
            $activity += "event $id from $source"
        }
    }
    [string[]] $problems = @()
    if (-not $disabledSeen) {
        $problems += "no event $script:HeldRuntimeDisabledEventId (journey runtime disabled) from $script:HeldRuntimeWorkerSource since the process started, so the runtime may be on"
    }
    if ($activity.Count -gt 0) {
        $problems += "the dispatch loop ran: $(@($activity | Select-Object -Unique) -join '; ')"
    }
    if ($problems.Count -eq 0) { return $null }
    return "JOURNEY_RUNTIME_NOT_HELD: $($problems -join '; ')."
}

function Get-ParallelReadBackAction {
    <#
        .SYNOPSIS
            control-server#578. The installer's whole read-back decision for one phase, from the Host's log lines:
            Action (Pass, Warn, StopServiceAndRefuse), Message, the Effective event found, and Waiting -- $true while
            the lines read so far may simply be too early to decide.

        .DESCRIPTION
            Released: Get-ParallelEffectiveConfigurationAction on the newest EFFECTIVE_CONFIGURATION event, as before.
            Held: that, and Get-ParallelHeldRuntimeRefusal on the same lines; a held refusal stops the service whatever
            the binding says -- a Host whose runtime may be on, or did run, is not one to leave running -- including in
            'fake' mode with nothing read back, where the binding alone would only warn.
            Waiting is $true while no event has been found, or (held) while 2001 has not been read and no activity has
            either; the installer reads the log again until it is $false or its deadline passes, then acts on Action.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Held', 'Released')][string] $Phase,
        [Parameter(Mandatory = $true)][hashtable] $Definition,
        [AllowEmptyCollection()][string[]] $Lines,
        [Parameter(Mandatory = $true)][datetimeoffset] $Since
    )
    $effective = Find-ParallelEffectiveConfiguration -Lines @($Lines) -Since $Since
    $verdict = Get-ParallelEffectiveConfigurationAction -Definition $Definition -Effective $effective
    $heldRefusal = ($Phase -ceq 'Held') ? (Get-ParallelHeldRuntimeRefusal -Lines @($Lines) -Since $Since) : $null
    $waiting = $null -eq $effective -or ($null -ne $heldRefusal -and -not $heldRefusal.Contains('the dispatch loop ran'))
    if ($heldRefusal) {
        $verdict = [pscustomobject]@{ Action = 'StopServiceAndRefuse'
            Message = ($null -eq $verdict.Message) ? $heldRefusal : "$heldRefusal $($verdict.Message)" }
    }
    return [pscustomobject]@{ Action = $verdict.Action; Message = $verdict.Message; Effective = $effective; Waiting = $waiting }
}

function New-ParallelInstanceConfigurationOverlay {
    <#
        .SYNOPSIS
            The appsettings.Production.json keys the product installer does not write.

        .DESCRIPTION
            Install-ControlServerLocal.ps1 emits a fixed set of keys -- Health, ConnectionStrings,
            OnboardTransport, OnboardSafetyProjection, JourneyRuntime.enabled and Serilog -- and
            nothing else. Everything the parallel instance needs beyond that is merged on top of
            the file it wrote, by the remote orchestration script, which is the same shape the MVP
            path already uses for the MesIngest bearer token.

            The merge matters more than it looks. .NET configuration merges per key, not per
            section: Install-ControlServerLocal.ps1 writing JourneyRuntime = { enabled = false }
            leaves every other JourneyRuntime key coming from the package's appsettings.json --
            including agvId and vehicleKey, which still name agv01 there.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][hashtable] $Definition)

    $journey = $Definition['journeyRuntime']
    $routeGraph = $Definition['routeGraph']

    $journeyOverlay = [ordered]@{}
    foreach ($key in $journey.Keys) {
        if ($key -cne 'fleet') { $journeyOverlay[$key] = $journey[$key] }
    }
    # control-server#571. The roster is always written, whole, so it replaces whatever an earlier install left in
    # appsettings.Production.json (Merge-ConfigurationTree replaces a list wholesale): a single-car definition writes
    # an empty one, so a roster from a two-car install does not survive a reinstall. The package's appsettings.json
    # has no roster, so the .NET by-index merge has nothing under it to leave behind; the read-back checks that.
    # Each row carries the product's keys only -- deviceKey and riotId are this deployment's checks, not options.
    # With a roster the Host still requires the primary pair to be one of its rows, and the package's appsettings.json
    # names agv01 there, so the primary is written too: the first row, never inherited.
    if ($journey.ContainsKey('fleet')) {
        $rows = @($journey['fleet'])
        $journeyOverlay['agvId'] = $rows[0]['agvId']
        $journeyOverlay['vehicleKey'] = $rows[0]['vehicleKey']
        $journeyOverlay['agvLifecycleGeneration'] = $rows[0]['agvLifecycleGeneration']
        $journeyOverlay['fleet'] = @(foreach ($row in $rows) {
                $written = [ordered]@{}
                foreach ($key in $script:FleetRowProductKeys) {
                    if (-not $row.Contains($key)) { continue }
                    $written[$key] = ($row[$key] -is [System.Collections.IList]) ? @($row[$key]) : $row[$key]
                }
                $written
            })
    } else {
        $journeyOverlay['fleet'] = @()
    }
    # control-server#638. Always written, false when the definition leaves it out, so a true from an earlier install does not
    # survive a reinstall in appsettings.Production.json; the read-back compares what the Host bound with this same value.
    $journeyOverlay['clearanceToWaitingPointEnabled'] = Get-ParallelClearanceToWaitingPoint -Definition $Definition
    # control-server#703 review L4. Always written too, 0 when the definition leaves it out, for the same reason: a value from an
    # earlier install (or a hand edit, or a v4 package's 2) must not survive a reinstall in appsettings.Production.json unseen.
    # 0 is the package default since control-server#751 and the only value the definition check accepts (REQ-0365).
    $journeyOverlay['stationTimeoutRedispatchLimit'] = $journey.Contains('stationTimeoutRedispatchLimit') ? $journey['stationTimeoutRedispatchLimit'] : 0
    # control-server#827. Always written too, 00:30:00 (REQ-0366's default) when the definition leaves it out, for the same
    # reason: a widened window from an earlier install must not survive a reinstall in appsettings.Production.json unseen.
    # control-server#828. The trial sublot rule's two keys always written too, off (0 and "") when the definition leaves the rule
    # out, for the same reason: a rule from an earlier install must not survive a reinstall in appsettings.Production.json unseen.
    # The defaults are $script:JourneyOverlayDefaults, which the read-back compares against as well.
    foreach ($key in $script:JourneyOverlayDefaults.Keys) {
        $journeyOverlay[$key] = Get-ParallelJourneyOverlayValue -Definition $Definition -Key $key
    }

    $routeGraphOverlay = [ordered]@{}
    foreach ($key in $routeGraph.Keys) { $routeGraphOverlay[$key] = $routeGraph[$key] }
    $catalogApproval = Get-ParallelMapStationCatalogApproval -Definition $Definition

    return [ordered]@{
        MesIngest = [ordered]@{
            baseUrl = $Definition['mesIngest']['baseUrl']
            sharedSecretEnvironmentVariable = 'CONTROL_SERVER_MES_INGEST_SHARED_SECRET'
        }
        JourneyRuntime = $journeyOverlay
        RouteGraph = $routeGraphOverlay
        RiotCreateDispatch = [ordered]@{ enabled = $Definition['riotCreateDispatch']['enabled'] }
        RiotForeignOrderCancel = [ordered]@{ enabled = $Definition['riotForeignOrderCancel']['enabled'] }
        # control-server#454. Written on every install, upgrade and rollback, so a first install that
        # rewrites the file cannot lose them. The variable is named here, never valued.
        VehicleFaultRecovery = [ordered]@{
            enabled = $Definition['vehicleFaultRecovery']['enabled']
            credentialEnvironmentVariable = $script:FaultRecoveryCredentialVariable
        }
        # control-server#617. Its own switch; the Host reads the credential through VehicleFaultRecovery's variable name above.
        ForeignRunningOrderRelease = [ordered]@{ enabled = $Definition['foreignRunningOrderRelease']['enabled'] }
        # workspace#49. Always written, false when the definition leaves the section out, so an earlier install's true does
        # not survive a reinstall; the two variables are named here, never valued.
        EmergencyStopRelease = [ordered]@{
            enabled = Test-ParallelEmergencyReleaseEnabled -Definition $Definition
            credentialEnvironmentVariable = $script:EmergencyReleaseCredentialVariable
            loadedCredentialEnvironmentVariable = $script:EmergencyLoadedReleaseCredentialVariable
        }
        # control-server#781. Always written, false when the definition leaves the section out, so an earlier install's (or a
        # hand edit's) true does not survive a reinstall; the read-back compares what the Host bound ($script:JourneyBindingFields).
        IdleReturn = [ordered]@{ enabled = Test-ParallelIdleReturnEnabled -Definition $Definition }
        # control-server#797. Always written -- the definition's, or vehicleFaultRecovery.enabled when it states none -- so an earlier
        # install's value does not survive a reinstall that says otherwise.
        RecoveryGrant = [ordered]@{ required = Test-ParallelRecoveryGrantRequired -Definition $Definition }
        # control-server#797. Always written, whole, like OnboardSafetyProjection.vehicleCredentials: a single-car definition writes an
        # empty list (the shared proof answers), so a list from a two-car install does not survive. Variables named, never valued.
        Recovery = [ordered]@{
            vehicleProofs = @(Get-ParallelRecoveryProofSlot -Definition $Definition | ForEach-Object {
                    [ordered]@{ vehicleKey = $_.VehicleKey; proofEnvironmentVariable = $_.Variable } })
        }
        FieldOperatorRoles = [ordered]@{
            path = $Definition['fieldOperatorRoles']['path']
            onboardClearanceEntryDeclared = $Definition['fieldOperatorRoles']['onboardClearanceEntryDeclared']
        }
        # control-server#518. Read only while the runtime is on, so written on every install whether it is or not.
        TaskTypeStations = [ordered]@{ settingsFile = $Definition['taskTypeStations']['settingsFile'] }
        # control-server#531. Always written, both keys: null when the definition leaves the section out, so values from an
        # earlier install -- or the 10-09..10-12 hand edit, in whatever casing (Merge-ConfigurationTree matches keys ignoring
        # case) -- do not survive a reinstall. A JSON null binds TimeSpan? as unset; the read-back checks what the Host bound.
        MapStationCatalog = [ordered]@{
            approvedSyncPeriod = $catalogApproval['approvedSyncPeriod']
            approvedMaxUnconfirmed = $catalogApproval['approvedMaxUnconfirmed']
        }
        # control-server#600. Always written, whole: a single-car definition writes an empty list, so a list from a two-car
        # install does not survive a reinstall. Variables are named here, never valued; enabled and the shared variable are
        # the product installer's and stay as it wrote them.
        OnboardSafetyProjection = [ordered]@{
            vehicleCredentials = @(Get-ParallelOnboardSafetyCredentialSlot -Definition $Definition | ForEach-Object {
                    [ordered]@{ vehicleKey = $_.VehicleKey; credentialEnvironmentVariable = $_.Variable } })
        }
        # control-server#686. Always written, derived from productionCutover.phase (Get-ParallelSlotConfigurationGateMode), so an
        # Enforcing from an earlier install does not survive a trial reinstall and no definition key can set it. The other
        # Governance keys are the product's and stay as the package and the product installer left them.
        Governance = [ordered]@{ slotConfigurationReadinessGate = (Get-ParallelSlotConfigurationGateMode -Definition $Definition) }
    }
}

function Get-ParallelSlotConfigurationGateMode {
    <#
        .SYNOPSIS
            control-server#686. The slot configuration readiness gate mode a definition deploys: Enforcing for
            productionCutover.phase 'production', Off for everything else. Pure.

        .DESCRIPTION
            REQ-0263 requires every slot of every vehicle verified one by one before it carries cargo in production; the trial
            runs on the slot simulator, where that verification means nothing and is not done by design (production-cutover-mode-plan.md
            section 5), so the gate there would stop every car. Derived rather than written: a key in the definition could be
            written Off for production, and a rule that requires it true is this function with one more thing to get wrong. The
            overlay writes it, the read-back compares the Host to it ($script:JourneyBindingFields), and an environment layer that
            sets it is refused (Get-ParallelJourneyRuntimeEnvironmentOverride).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    $cutover = $Definition['productionCutover']
    return ($cutover -is [System.Collections.IDictionary] -and $cutover['phase'] -ceq 'production') ? 'Enforcing' : 'Off'
}

function Get-ParallelOnboardSafetyCredentialSlot {
    <#
        .SYNOPSIS
            control-server#600. One row per roster vehicle: VehicleKey, RiotId and the service-Environment Variable that
            holds its vehicle-safety projection credential. One per row of journeyRuntime.fleet -- a roster of one row
            included; none for a definition without a roster (the single-vehicle keys), which keeps the shared credential.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    $journey = $Definition['journeyRuntime']
    if (-not $journey.Contains('fleet')) { return }
    foreach ($row in @($journey['fleet'])) {
        [pscustomobject]@{
            VehicleKey = [string] $row['vehicleKey']
            RiotId = [long] $row['riotId']
            Variable = "$($script:OnboardSafetyCredentialVariablePrefix)$([long] $row['riotId'])"
        }
    }
}

function Resolve-ParallelOnboardSafetyCredential {
    <#
        .SYNOPSIS
            control-server#600. The vehicle-safety projection credential of every roster vehicle, as an ordered map from
            service-Environment variable to value. Empty for a definition without a roster. Throws, naming vehicles and never a
            value, when one is missing or two would let one vehicle read another's projection.

        .DESCRIPTION
            Per vehicle, in this order:
              * -Supplied: deploy-config.json's onboardSafetyCredentials, vehicleKey -> value, which the control host fills
                from its DPAPI store (an install or upgrade; a rollback has none);
              * what the service's Environment already holds under that vehicle's variable -- read before the product
                script, because a first install rebuilds the Environment.
            The Host checks the same things at startup (OnboardSafetyProjectionOptionsValidator) and refuses to start; by
            then the old service is already down, so the installer refuses here first.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [AllowNull()] $Supplied,
        [AllowNull()][AllowEmptyCollection()][string[]] $ServiceEnvironment,
        [AllowNull()][AllowEmptyString()][string] $SharedCredential
    )
    $result = [ordered]@{}
    $missing = @()
    foreach ($slot in @(Get-ParallelOnboardSafetyCredentialSlot -Definition $Definition)) {
        $value = $null
        if ($Supplied -is [System.Collections.IDictionary]) {
            if ($Supplied.Contains($slot.VehicleKey)) { $value = [string] $Supplied[$slot.VehicleKey] }
        } elseif ($null -ne $Supplied -and $Supplied.PSObject.Properties.Name -ccontains $slot.VehicleKey) {
            $value = [string] $Supplied.($slot.VehicleKey)
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = Get-ParallelServiceEnvironmentEntry -Environment $ServiceEnvironment -Name $slot.Variable
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            $missing += "$($slot.VehicleKey) (riotId $($slot.RiotId), $($slot.Variable))"
            continue
        }
        $result[$slot.Variable] = $value
    }
    if ($missing.Count -gt 0) {
        throw ("ONBOARD_SAFETY_CREDENTIAL_MISSING: no vehicle-safety projection credential for $($missing -join '; '). " +
            'deploy-config.json carries none for it and the service''s Environment holds none. The Host would refuse to start ' +
            '(control-server#600). On the control host: control-server\Set-ControlServerSecret.ps1 -NewVehicleSafetyCredential ' +
            '<alias>, then deploy again and redeploy that vehicle''s onboard half. Nothing was stopped or changed.')
    }
    $slots = @(Get-ParallelOnboardSafetyCredentialSlot -Definition $Definition)
    $shared = @()
    for ($i = 0; $i -lt $slots.Count; $i++) {
        $value = $result[$slots[$i].Variable]
        if (-not [string]::IsNullOrWhiteSpace($SharedCredential) -and [string]::Equals($value, $SharedCredential, [StringComparison]::Ordinal)) {
            $shared += "$($slots[$i].VehicleKey) holds the shared onboard credential, which every vehicle has"
        }
        for ($j = 0; $j -lt $i; $j++) {
            if ([string]::Equals($value, $result[$slots[$j].Variable], [StringComparison]::Ordinal)) {
                $shared += "$($slots[$j].VehicleKey) and $($slots[$i].VehicleKey) hold one value"
            }
        }
    }
    if ($shared.Count -gt 0) {
        throw ("ONBOARD_SAFETY_CREDENTIAL_SHARED: $($shared -join '; '). Each vehicle needs a projection credential of its own, " +
            'or one vehicle can read another''s (control-server#600). Generate new ones with ' +
            'control-server\Set-ControlServerSecret.ps1 -NewVehicleSafetyCredential <alias>. Nothing was stopped or changed.')
    }
    return $result
}

function Get-ParallelRecoveryProofSlot {
    <#
        .SYNOPSIS
            control-server#797. One exception recovery proof variable per roster row, named by its RIoT id, as for the vehicle-safety
            projection credentials (Get-ParallelOnboardSafetyCredentialSlot). None for a definition without a roster: the shared
            proof answers for its one vehicle.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    foreach ($slot in @(Get-ParallelOnboardSafetyCredentialSlot -Definition $Definition)) {
        [pscustomobject]@{
            VehicleKey = $slot.VehicleKey
            RiotId = $slot.RiotId
            Variable = "$($script:RecoveryProofVariablePrefix)$($slot.RiotId)"
        }
    }
}

function Resolve-ParallelRecoveryProof {
    <#
        .SYNOPSIS
            control-server#797. Every roster vehicle's exception recovery proof, as an ordered map from service-Environment variable to
            value, and the vehicles that have none. Throws, naming vehicles and never a value, when two vehicles would share one.

        .DESCRIPTION
            Per vehicle, as for the projection credentials: -Supplied (deploy-config.json's recoveryProofs, vehicleKey -> value, from
            the control host's DPAPI store), else what the service's Environment already holds under that vehicle's variable. A
            vehicle with neither is not refused: the Host then refuses every session that vehicle opens (its listed variable is
            empty), which is closed, not open; the installer reports it as RECOVERY_PROOF_MISSING. Two vehicles with one value are
            refused: one vehicle's proof would open the other's sessions.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [AllowNull()] $Supplied,
        [AllowNull()][AllowEmptyCollection()][string[]] $ServiceEnvironment
    )
    $result = [ordered]@{}
    $missing = @()
    $slots = @(Get-ParallelRecoveryProofSlot -Definition $Definition)
    foreach ($slot in $slots) {
        $value = $null
        if ($Supplied -is [System.Collections.IDictionary]) {
            if ($Supplied.Contains($slot.VehicleKey)) { $value = [string] $Supplied[$slot.VehicleKey] }
        } elseif ($null -ne $Supplied -and $Supplied.PSObject.Properties.Name -ccontains $slot.VehicleKey) {
            $value = [string] $Supplied.($slot.VehicleKey)
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = Get-ParallelServiceEnvironmentEntry -Environment $ServiceEnvironment -Name $slot.Variable
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            $missing += "$($slot.VehicleKey) (riotId $($slot.RiotId), $($slot.Variable))"
            continue
        }
        $result[$slot.Variable] = $value
    }
    $shared = @()
    $present = @($slots | Where-Object { $result.Contains($_.Variable) })
    for ($i = 0; $i -lt $present.Count; $i++) {
        for ($j = 0; $j -lt $i; $j++) {
            if ([string]::Equals($result[$present[$i].Variable], $result[$present[$j].Variable], [StringComparison]::Ordinal)) {
                $shared += "$($present[$j].VehicleKey) and $($present[$i].VehicleKey) hold one value"
            }
        }
    }
    if ($shared.Count -gt 0) {
        throw ("RECOVERY_PROOF_SHARED: $($shared -join '; '). Each vehicle needs a recovery proof of its own, or one vehicle's proof " +
            'opens the other''s recovery sessions (control-server#797). Nothing was stopped or changed.')
    }
    return [pscustomobject]@{ Proofs = $result; Missing = $missing }
}

function Get-ParallelRollbackTargetRefusal {
    <#
        .SYNOPSIS
            control-server#652 R2. Why a -Rollback to the previous generation would fail on this definition, or $null. Pure.

        .DESCRIPTION
            -Rollback runs the Update-ControlServerLocal.ps1 inside the previous generation's package. On a definition with a
            vehicle roster (Get-ParallelOnboardSafetyCredentialSlot: per-vehicle projection credentials, control-server#600)
            the Host answers the shared onboard credential 401, and an upgrade script older than control-server#649 knows no
            other: its safety projection check fails every time, it rolls itself back, and the instance is left with its
            package directories swapped and JourneyRuntime off. So the installer refuses such a rollback before anything
            changes. The one fact read is the reason code control-server#649 introduced with the per-vehicle check,
            SAFETY_PROJECTION_VEHICLE_CREDENTIAL_MISSING, in that script's text -- one marker, not a list of features. A
            script that is not there refuses too. A definition without a roster keeps the shared credential and is not refused.

        .PARAMETER UpdateScriptText
            The previous generation's scripts\Update-ControlServerLocal.ps1, or $null when it is not there.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [Parameter(Mandatory = $true)][string] $UpdateScriptPath,
        [AllowNull()][AllowEmptyString()][string] $UpdateScriptText
    )
    $slots = @(Get-ParallelOnboardSafetyCredentialSlot -Definition $Definition)
    if ($slots.Count -eq 0) { return $null }
    $marker = 'SAFETY_PROJECTION_VEHICLE_CREDENTIAL_MISSING'
    if (-not [string]::IsNullOrEmpty($UpdateScriptText) -and $UpdateScriptText.Contains($marker, [StringComparison]::Ordinal)) { return $null }
    $found = [string]::IsNullOrEmpty($UpdateScriptText) ? 'does not exist' : "has no $marker"
    return ('ROLLBACK_TARGET_PREDATES_PER_VEHICLE_CHECK (nothing was stopped or changed; the package directories were not swapped): ' +
        "this definition lists $($slots.Count) roster vehicle(s) ($((@($slots | ForEach-Object VehicleKey)) -join ', ')), so the Host " +
        'answers the shared onboard credential 401 (control-server#600), and the previous generation''s upgrade script ' +
        "$UpdateScriptPath $found -- it predates the per-vehicle safety projection check (control-server#649). A -Rollback " +
        'would run it, fail that check every time, roll itself back and leave the package directories swapped with JourneyRuntime ' +
        'off. Fix forward instead: deploy a fixed commit. Do not uninstall and install a package older than control-server#649 ' +
        'either: its installer stops at the first readiness read of a retained data root. -Rollback becomes available again once ' +
        'a second deployment that contains control-server#649 has made the previous generation one that does.')
}

function Get-ParallelJourneyRuntimeEnvironmentOverride {
    <#
        .SYNOPSIS
            control-server#578 review item 2. The environment entries that would set a JourneyRuntime key above
            appsettings.Production.json, as "service NAME" / "machine NAME"; none when there are none.

        .DESCRIPTION
            .NET reads environment variables after the JSON files, so JourneyRuntime__Enabled=true in the service's
            Environment or the machine's environment turns the runtime on whatever the file says -- the held phase
            would read it on only after the Host had started. The Host builder also reads DOTNET_- and
            ASPNETCORE_-prefixed variables into its configuration, so those count too. A name is matched ignoring
            case, with __ or : after JourneyRuntime; JourneyRuntimeX or MyJourneyRuntime__ is not this section.
            Values are never returned.

            control-server#531 review L1: MapStationCatalog too, the same way. REQ-0302's two approved values come from the
            instance definition alone; MapStationCatalog__ApprovedMaxUnconfirmed in an environment layer (the L2 rig injects
            them so, Invoke-L2Scenario.ps1) would bind over the file, and the read-back would see it only after a restart.

            workspace#49 review L5: EmergencyStopRelease too. EmergencyStopRelease__LoadedCredentialEnvironmentVariable in an
            environment layer could point the loaded entry at the MVP's variable, and __Enabled could turn the entries off
            under a production definition; neither would show in the overlay.

            control-server#781: IdleReturn too. IdleReturn__Enabled=true in an environment layer would turn idle return on --
            vehicles driving themselves with no demand -- whatever the definition says, and only a restart would show it.

            control-server#797: RecoveryGrant and Recovery:VehicleProofs too. RecoveryGrant__Required=false would let any holder of
            the shared credential recover any vehicle under a definition that asks for grants, and a VehicleProofs entry could
            give one vehicle another's proof.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowEmptyCollection()][AllowNull()][string[]] $ServiceEnvironment,
        [AllowEmptyCollection()][AllowNull()][string[]] $MachineEnvironment
    )
    # control-server#686: Governance:slotConfigurationReadinessGate too -- that key alone, the other Governance keys are the
    # product's. The gate comes from the definition's phase, and an environment layer would bind over it.
    $pattern = '^(?i:(?:DOTNET_|ASPNETCORE_)?(?:(?:JourneyRuntime|MapStationCatalog|EmergencyStopRelease|IdleReturn|RecoveryGrant|Recovery(?:__|:)VehicleProofs)(?:__|:)|Governance(?:__|:)slotConfigurationReadinessGate$))'
    foreach ($pair in @(@{ Where = 'service'; Entries = $ServiceEnvironment }, @{ Where = 'machine'; Entries = $MachineEnvironment })) {
        foreach ($entry in @($pair.Entries | Where-Object { -not [string]::IsNullOrEmpty($_) })) {
            $name = ($entry -split '=', 2)[0]
            if ($name -match $pattern) { "$($pair.Where) $name" }
        }
    }
}

function Format-ParallelLogFileFacts {
    <#
        .SYNOPSIS
            control-server#578 review item 3. One line naming each log file a read-back read, its size and last write
            time (UTC), for the refusal: a Host that stopped writing (control-server#587, the 1 GiB limit) reads back
            nothing new, and the size and time say so at a glance.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowEmptyCollection()][object[]] $Files)
    $files = @($Files | Where-Object { $null -ne $_ })
    if ($files.Count -eq 0) { return 'Log files read: no log file matched.' }
    return 'Log files read: ' + (@($files | ForEach-Object {
                "$($_.FullName) ($([long] $_.Length) bytes, last written $(([datetime] $_.LastWriteTimeUtc).ToString('yyyy-MM-ddTHH:mm:ss', [cultureinfo]::InvariantCulture))Z)"
            }) -join '; ') + '.'
}

$script:ReadBackLogFileLimit = 16

function Get-ParallelLogFilesSince {
    <#
        .SYNOPSIS
            control-server#587. The Host's log files a read-back reads, newest first: every file the process may have
            written since -Since (its start).

        .DESCRIPTION
            The Host's log rolls at its size limit (ServerLogFileLimits), so the events logged as the process starts --
            EFFECTIVE_CONFIGURATION, 2001 -- can be any number of files back. Until #587 the read-back took the two
            newest files by last write time, and a log that rolled twice after the start read back as
            JOURNEY_RUNTIME_NOT_HELD and stopped the service.

            Newest first by creation time, the order Serilog created them in (the name breaks a tie, ordinally:
            "x-20261011.ndjson" before "x-20261011_001.ndjson"), down to and including the first file created at or
            before -Since -- the file the process started writing into; no older file can hold a line it wrote.
            At most $script:ReadBackLogFileLimit files: a read-back runs within minutes of the start, so more than that
            means something else is wrong, and the events it then misses are read back as missing, which refuses.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string] $Directory,
        [Parameter(Mandatory = $true)][string] $Filter,
        [Parameter(Mandatory = $true)][datetimeoffset] $Since
    )
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($file in @(Get-ChildItem -LiteralPath $Directory -Filter $Filter -File -ErrorAction SilentlyContinue)) { $files.Add($file) }
    $files.Sort([Comparison[IO.FileInfo]] {
            param($left, $right)
            $byCreation = $right.CreationTimeUtc.CompareTo($left.CreationTimeUtc)
            ($byCreation -ne 0) ? $byCreation : [string]::CompareOrdinal($right.Name, $left.Name)
        })
    $selected = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($file in $files) {
        if ($selected.Count -ge $script:ReadBackLogFileLimit) { break }
        $selected.Add($file)
        if ([datetimeoffset]::new([datetime]::SpecifyKind($file.CreationTimeUtc, [DateTimeKind]::Utc)) -le $Since) { break }
    }
    return @($selected)
}

function Read-ParallelLogLines {
    <#
        .SYNOPSIS
            control-server#587. Every line of the given log files, in the order given. The Host holds its current file
            open (shared = true), so each is read without asking for exclusive access. (Not with FileShare.Delete
            as well: this module's delete guard reads that as a delete, and Serilog's retention skips a file it cannot
            delete and tries again at its next roll.)
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([AllowEmptyCollection()][object[]] $Files)
    foreach ($file in @($Files | Where-Object { $null -ne $_ })) {
        $stream = [IO.File]::Open($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { ([IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)).ReadToEnd() -split "`r?`n" } finally { $stream.Dispose() }
    }
}

function Get-ParallelServiceEnvironmentEntry {
    <#
        .SYNOPSIS
            The value of one NAME=value entry in a service's Environment multi-string, or $null.

        .DESCRIPTION
            Names compare ignoring case, as Windows environment variable names do. The value is
            everything after the first '=', so a value may itself contain '='.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][AllowEmptyCollection()][string[]] $Environment,
        [Parameter(Mandatory = $true)][string] $Name
    )
    foreach ($entry in @($Environment)) {
        if ($null -ne $entry -and $entry.StartsWith("$Name=", [StringComparison]::OrdinalIgnoreCase)) {
            return $entry.Substring($Name.Length + 1)
        }
    }
    return $null
}

function Set-ParallelServiceEnvironmentEntry {
    <#
        .SYNOPSIS
            The Environment multi-string with NAME's entry replaced (or added), every other entry kept
            in its order.

        .DESCRIPTION
            Pure: the caller reads and writes the registry. Shared by the MesIngest bearer token and
            the fault recovery credential (control-server#454), which used to be one inline filter.
            A value with a line break or NUL is refused: REG_MULTI_SZ would split it into entries.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [AllowNull()][AllowEmptyCollection()][string[]] $Environment,
        [Parameter(Mandatory = $true)][string] $Name,
        [Parameter(Mandatory = $true)][string] $Value
    )
    if ($Value.IndexOfAny([char[]] "`r`n`0") -ge 0) {
        throw "The value for $Name contains a line break or NUL, which the service's Environment cannot hold."
    }
    [string[]] $kept = @(@($Environment) | Where-Object {
            $null -ne $_ -and -not $_.StartsWith("$Name=", [StringComparison]::OrdinalIgnoreCase) })
    return [string[]] (@($kept) + "$Name=$Value")
}

function Resolve-ParallelFaultRecoveryCredential {
    <#
        .SYNOPSIS
            The fault recovery credential this install writes into the service's Environment, or
            $null when there is none and none is needed. Throws when the entry is on and there is none.

        .DESCRIPTION
            control-server#454. Two sources, in this order:
              * -Supplied: from deploy-config.json, which the control host fills from its DPAPI
                store. An install or upgrade has it; a rollback has no deploy-config.json at all.
              * -Carried: what the service's Environment held before the product script ran. The
                caller reads it BEFORE that, because a first install -- and a rollback that lands in
                the product's first-install branch -- rebuilds the Environment from scratch.
            With the entry on and neither, the caller must stop before anything is stopped: the
            server itself would refuse to start (VehicleFaultRecoveryOptionsValidator, ValidateOnStart),
            but by then the old service is already down.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [AllowNull()][AllowEmptyString()][string] $Supplied,
        [AllowNull()][AllowEmptyString()][string] $Carried
    )
    if (-not [string]::IsNullOrWhiteSpace($Supplied)) { return $Supplied }
    if (-not [string]::IsNullOrWhiteSpace($Carried)) { return $Carried }
    # Not Get-Node: it only answers for [hashtable], and a section built with [ordered] is not one.
    # Here a section it failed to see would mean "entry off", the direction that lets a broken
    # deployment through.
    # control-server#617: the foreign running order release entry uses the same credential, so either switch on needs it.
    $entries = @(foreach ($section in 'vehicleFaultRecovery', 'foreignRunningOrderRelease') {
        $node = $Definition.Contains($section) ? $Definition[$section] : $null
        if ($node -is [System.Collections.IDictionary] -and $node['enabled'] -eq $true) { $section }
    })
    if ($entries.Count -gt 0) {
        throw ("$($entries[0]).enabled is true, but there is no value for $($script:FaultRecoveryCredentialVariable): " +
            "deploy-config.json carries no faultRecoveryCredential and the service's Environment holds none. " +
            'The service would refuse to start without it. Nothing was stopped or changed.')
    }
    return $null
}

function Test-ParallelIdleReturnEnabled {
    <#
        .SYNOPSIS
            control-server#781. Whether the definition turns idle return on (IdleReturnOptions.Enabled), as the overlay writes it
            and the read-back expects it: only a JSON true counts, absent is off (the product default).
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    $node = $Definition.Contains('idleReturn') ? $Definition['idleReturn'] : $null
    return ($node -is [System.Collections.IDictionary] -and $node.Contains('enabled') -and $node['enabled'] -is [bool] -and $node['enabled'])
}

function Test-ParallelRecoveryGrantRequired {
    <#
        .SYNOPSIS
            control-server#797. Whether the definition asks for recovery grants (RecoveryGrantOptions.Required), as the overlay writes
            it. recoveryGrant.required when stated (only a JSON true counts); absent, vehicleFaultRecovery.enabled: the entries a
            grant guards are on exactly then, and an instance that turns them on must not be left taking the shared credential alone.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    $node = $Definition.Contains('recoveryGrant') ? $Definition['recoveryGrant'] : $null
    if ($node -is [System.Collections.IDictionary] -and $node.Contains('required')) {
        return ($node['required'] -is [bool] -and $node['required'])
    }
    $recovery = $Definition.Contains('vehicleFaultRecovery') ? $Definition['vehicleFaultRecovery'] : $null
    return ($recovery -is [System.Collections.IDictionary] -and $recovery.Contains('enabled') -and $recovery['enabled'] -is [bool] -and
        $recovery['enabled'])
}

function Test-ParallelEmergencyReleaseEnabled {
    <#
        .SYNOPSIS
            workspace#49. Whether the definition turns the emergency stop release entries on: only a JSON true counts, absent
            is off (the product default). Not Get-Node: a section built with [ordered] is not a [hashtable].
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition)
    $node = $Definition.Contains('emergencyStopRelease') ? $Definition['emergencyStopRelease'] : $null
    return ($node -is [System.Collections.IDictionary] -and $node.Contains('enabled') -and $node['enabled'] -is [bool] -and $node['enabled'])
}

function Resolve-ParallelEmergencyReleaseCredential {
    <#
        .SYNOPSIS
            workspace#49. The two emergency stop release credentials this install writes into the service's Environment, as
            an ordered map from variable to value; empty when there are none and none are needed. Throws, naming variables and
            never a value, when the entries are on and one is missing, or when the two are one value.

        .DESCRIPTION
            Per credential, in this order:
              * -Supplied: deploy-config.json's emergencyReleaseCredential / emergencyLoadedReleaseCredential, which the
                control host fills from its DPAPI store (an install or upgrade; a rollback has none);
              * what the service's Environment already holds under that variable -- read before the product script,
                because a first install rebuilds the Environment.
            On, both are required: without the empty-vehicle one the Host refuses to start (EmergencyStopReleaseOptionsValidator),
            and without the loaded one its entry answers 503 -- found only when a loaded vehicle is stuck. The two equal would
            let the empty-vehicle credential release a loaded vehicle (CP-0011), so that is refused whether on or off. Off,
            whatever there is is written, so turning the entries on later needs no new secret on the server.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [AllowNull()] $Supplied,
        [AllowNull()][AllowEmptyCollection()][string[]] $ServiceEnvironment
    )
    $result = [ordered]@{}
    $missing = @()
    foreach ($slot in @(
            @{ Field = 'emergencyReleaseCredential'; Variable = $script:EmergencyReleaseCredentialVariable }
            @{ Field = 'emergencyLoadedReleaseCredential'; Variable = $script:EmergencyLoadedReleaseCredentialVariable })) {
        $value = $null
        if ($Supplied -is [System.Collections.IDictionary]) {
            if ($Supplied.Contains($slot.Field)) { $value = [string] $Supplied[$slot.Field] }
        } elseif ($null -ne $Supplied -and $Supplied.PSObject.Properties.Name -ccontains $slot.Field) {
            $value = [string] $Supplied.($slot.Field)
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = Get-ParallelServiceEnvironmentEntry -Environment $ServiceEnvironment -Name $slot.Variable
        }
        if ([string]::IsNullOrWhiteSpace($value)) { $missing += $slot.Variable; continue }
        $result[$slot.Variable] = $value
    }
    if ((Test-ParallelEmergencyReleaseEnabled -Definition $Definition) -and $missing.Count -gt 0) {
        throw ("EMERGENCY_RELEASE_CREDENTIAL_MISSING: emergencyStopRelease.enabled is true, but there is no value for $($missing -join ' or '): " +
            'deploy-config.json carries none and the service''s Environment holds none. Without the empty-vehicle credential the ' +
            'service refuses to start; without the loaded one its entry answers 503. On the control host: ' +
            'control-server\Set-ControlServerSecret.ps1 -NewEmergencyReleaseCredential Empty, Loaded, then deploy again. Nothing was stopped or changed.')
    }
    if ($result.Count -eq 2 -and [string]::Equals($result[$script:EmergencyReleaseCredentialVariable], $result[$script:EmergencyLoadedReleaseCredentialVariable], [StringComparison]::Ordinal)) {
        throw ("EMERGENCY_RELEASE_CREDENTIAL_SHARED: $($script:EmergencyReleaseCredentialVariable) and $($script:EmergencyLoadedReleaseCredentialVariable) " +
            'would hold one value, so the empty-vehicle credential would release a loaded vehicle (CP-0011). Generate a new one with ' +
            'control-server\Set-ControlServerSecret.ps1 -NewEmergencyReleaseCredential Loaded -Rotate. Nothing was stopped or changed.')
    }
    return $result
}

function Format-ParallelEmergencyReleaseLine {
    <#
        .SYNOPSIS
            workspace#49. The installer's EMERGENCY_STOP_RELEASE= result line, for 19-deploy-control-server-parallel.ps1:
            what the overlay turned on, and which of the two credential variables the service's Environment holds non-blank
            (-EnvironmentNames, read back after the write). Names only, never a value:
            EMERGENCY_STOP_RELEASE=enabled=true credential=set loadedCredential=set loadedConfirmers=2
            loadedConfirmers (review M2) is how many named people in the roster hold R-13, the only role the loaded entry
            accepts (LoadedEmergencyStopRelease.AllowedRoles); a number, never a name. -RosterText is the roster file's text,
            $null when it does not exist (0).
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Definition,
        [AllowNull()][AllowEmptyCollection()][string[]] $EnvironmentNames,
        [AllowNull()][AllowEmptyString()][string] $RosterText
    )
    $names = @($EnvironmentNames)
    return ('EMERGENCY_STOP_RELEASE=enabled={0} credential={1} loadedCredential={2} loadedConfirmers={3}' -f
        ((Test-ParallelEmergencyReleaseEnabled -Definition $Definition) ? 'true' : 'false'),
        (($names -contains $script:EmergencyReleaseCredentialVariable) ? 'set' : 'unset'),
        (($names -contains $script:EmergencyLoadedReleaseCredentialVariable) ? 'set' : 'unset'),
        (Get-ParallelRosterRoleHolderCount -RosterText $RosterText -Role 'R-13'))
}

function Get-ParallelRosterRoleHolderCount {
    <#
        .SYNOPSIS
            workspace#49 review M2. How many entries of the field operator roster have a non-blank operatorId and hold -Role,
            read the way FieldOperatorRoleRoster does (keys in any case, role codes ordinal). 0 for a missing, unreadable or
            empty roster: the server reads all three as "nobody holds it".
    #>
    [CmdletBinding()]
    [OutputType([int])]
    param(
        [AllowNull()][AllowEmptyString()][string] $RosterText,
        [Parameter(Mandatory = $true)][string] $Role
    )
    if ([string]::IsNullOrWhiteSpace($RosterText)) { return 0 }
    try { $document = ConvertFrom-Json -InputObject $RosterText -AsHashtable -Depth 12 -ErrorAction Stop } catch { return 0 }
    $count = 0
    foreach ($entry in @(Get-ConfigurationValue $document 'operators')) {
        $operatorId = Get-ConfigurationValue $entry 'operatorId'
        if ($operatorId -isnot [string] -or [string]::IsNullOrWhiteSpace($operatorId)) { continue }
        if (@(@(Get-ConfigurationValue $entry 'roles') | Where-Object { $_ -is [string] -and $_ -ceq $Role }).Count -gt 0) { $count++ }
    }
    return $count
}

function Get-ParallelGateClosingSteps {
    <#
        The operator's steps to close this instance's RIoT dispatch gate, spelled out with this
        instance's file and service and the MVP's beside them (control-server#454, incremental review
        item 1). The two install roots differ only by '.V2', and an operator who edits the MVP's file
        or restarts the MVP's service has changed production and left this instance's gate open.
    #>
    param([string] $ConfigurationPath, [string] $ServiceName)
    $mvpConfiguration = "$($script:ProductionPaths[0])\appsettings.Production.json"
    # control-server#472: the script does all of the hand steps below, with the checks; they stay as the
    # fallback for when it cannot run.
    return ("Close the gate with remote-ops/factory-server/scripts/20-set-control-server-parallel-dispatch-gate.ps1 -State Closed " +
        "from the control host, on the user's authorization: it refuses while a journey is in flight, writes the file, restarts " +
        "the V2 service and verifies it. Only if that script cannot run, close it by hand on THIS instance, in this order: (1) stop injecting new demand into its FakeMesIngest and wait " +
        "until agv02 and agv03 both report their last order Completed; (2) edit $ConfigurationPath -- the V2 file, NOT the " +
        "MVP's $mvpConfiguration -- and set RiotCreateDispatch.enabled to false, with an editor that keeps the file UTF-8 " +
        '(saving it from Notepad with "Save As" can re-encode it and corrupt the Chinese agvId); (3) restart the service ' +
        "'$ServiceName' -- NOT '$script:ProductionServiceName'. Then run this again.")
}

function Get-ParallelUpgradeRefusal {
    <#
        .SYNOPSIS
            Why an upgrade or rollback must not run on this installed configuration now, or $null.

        .DESCRIPTION
            control-server#454 review S3. An upgrade and a rollback stop the service, and with it the
            journey runtime -- including its fault supervision of a vehicle that is under way. While
            the installed configuration lets this instance place RIoT orders (RiotCreateDispatch
            enabled), a vehicle may be under way, so both are refused before anything is touched. The
            message carries the closing steps with this instance's own file and service named, and the
            MVP's beside them as the ones NOT to touch (Get-ParallelGateClosingSteps). Whether a
            vehicle IS under way cannot be read from here; the gate is the state this instance controls.

            Pure: takes the installed configuration as read. Keys match ignoring case, as .NET reads
            them; an absent section or flag is the product default, closed.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Configuration,
        [Parameter(Mandatory = $true)][string] $ConfigurationPath,
        [Parameter(Mandatory = $true)][string] $ServiceName
    )
    $dispatch = Get-ConfigurationValue $Configuration 'RiotCreateDispatch'
    if ((Get-ConfigurationValue $dispatch 'enabled') -eq $true) {
        return ("UPGRADE_REFUSED_DISPATCH_OPEN: $ConfigurationPath has RiotCreateDispatch.enabled=true, so a vehicle may be " +
            'under way, and stopping the service would stop the runtime''s fault supervision of it. ' +
            (Get-ParallelGateClosingSteps -ConfigurationPath $ConfigurationPath -ServiceName $ServiceName) +
            ' Nothing was stopped or changed.')
    }
    return $null
}

function Get-ParallelPreInstallRefusal {
    <#
        .SYNOPSIS
            The installer's first check, before it records a definition, swaps a rollback or unpacks
            anything: why this run must not go ahead, or $null.

        .DESCRIPTION
            control-server#454. Fail closed. With the service present, the installed
            appsettings.Production.json is what an upgrade and a rollback work on, and these refuse:
              * INSTALLED_CONFIGURATION_MISSING: the service exists and the file does not. Nobody can
                say what that instance is doing, and -Rollback used to swap the package directories
                before finding out (an earlier version of this check ran only when the file existed);
              * INSTALLED_CONFIGURATION_UNREADABLE: empty, not JSON, or not a JSON object;
              * SERVICE_NOT_SETTLED: the service is neither Running nor Stopped (StartPending, StopPending,
                ContinuePending, PausePending, Paused, unknown) -- mid-transition the process may not have
                read the file yet, so no time comparison is attempted;
              * SERVICE_START_TIME_UNKNOWN / CONFIGURATION_WRITE_TIME_UNKNOWN: the service is Running and
                one of the two times below cannot be had;
              * CONFIGURATION_CHANGED_SINCE_START: the file was written after the running process
                started (incremental review item 2). The process read the file when it started; what
                the file says now -- RiotCreateDispatch.enabled=false after a hand edit, say -- is not
                what the process is doing until it restarts, so reading the file would let through
                exactly the open gate S3 refuses;
              * whatever Get-ParallelUpgradeRefusal says (RIoT dispatch open).
            The time comparison runs only for a Running service; a Stopped one has no process, so the
            file is the truth. With no service there is nothing running to protect and nothing to upgrade:
            a first install goes ahead, whatever file may be lying around.

            Pure: the caller says whether the service exists and its status, passes the file's text
            ($null when the file does not exist), its last write time and the process start time, both
            UTC ($null when unknown).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][bool] $ServiceExists,
        [AllowNull()][AllowEmptyString()][string] $ServiceStatus,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][string] $ConfigurationPath,
        # Untyped on purpose: a [string] parameter turns $null into '', and $null (missing file) and ''
        # (empty file) are different refusals. The two times are untyped so $null means unknown.
        [AllowNull()][AllowEmptyString()] $ConfigurationText,
        [AllowNull()] $ConfigurationWriteTimeUtc,
        [AllowNull()] $ProcessStartTimeUtc
    )
    if (-not $ServiceExists) { return $null }
    $nothing = ' Nothing was stopped or changed.'
    if ($null -eq $ConfigurationText) {
        return ("INSTALLED_CONFIGURATION_MISSING: the service '$ServiceName' exists but $ConfigurationPath does not. That is " +
            'not a state any install or rollback leaves behind: find out why the file is gone (restored from the latest ' +
            'backup under the backup root? removed by hand?) before running this again.' + $nothing)
    }
    $configuration = $null
    try { $configuration = ConvertFrom-Json -InputObject ([string] $ConfigurationText) -AsHashtable -Depth 12 -ErrorAction Stop } catch { $configuration = $null }
    if ($configuration -isnot [System.Collections.IDictionary]) {
        return ("INSTALLED_CONFIGURATION_UNREADABLE: $ConfigurationPath is not a JSON object (empty, not JSON, or the wrong " +
            'shape). Find out what wrote it before running this again.' + $nothing)
    }
    # Only two states are settled enough to judge: Stopped (no process; the file is the truth) and
    # Running (compare the times below). StartPending, StopPending, ContinuePending, PausePending, Paused
    # or an unknown status refuse outright: mid-transition the process may not have read the file yet,
    # so a time comparison proves nothing (incremental review, item 2).
    if ($ServiceStatus -cne 'Stopped' -and $ServiceStatus -cne 'Running') {
        return ("SERVICE_NOT_SETTLED: the service '$ServiceName' is $(if ($ServiceStatus) { $ServiceStatus } else { 'in an unknown state' }) " +
            '-- starting, stopping, or a state this check cannot judge. Wait until it is Running or Stopped and try again later.' + $nothing)
    }
    if ($ServiceStatus -ceq 'Running') {
        if ($null -eq $ProcessStartTimeUtc) {
            return ("SERVICE_START_TIME_UNKNOWN: the service '$ServiceName' is Running and the start time of its process cannot " +
                "be read, so whether it runs on what $ConfigurationPath says now cannot be told. Find out why (is the process " +
                'there?) before running this again.' + $nothing)
        }
        if ($null -eq $ConfigurationWriteTimeUtc) {
            return ("CONFIGURATION_WRITE_TIME_UNKNOWN: the last write time of $ConfigurationPath cannot be read." + $nothing)
        }
        # Both to UTC here, whatever the caller did (third quick review, T4): [datetime] comparison ignores
        # Kind, so a start time handed over in local time on a UTC+8 machine would read 8 hours late and let
        # a file edited within those 8 hours through. Utc stays as it is; Local converts; Unspecified is
        # taken as local, as .NET does.
        $writtenUtc = ([datetime] $ConfigurationWriteTimeUtc).ToUniversalTime()
        $startedUtc = ([datetime] $ProcessStartTimeUtc).ToUniversalTime()
        if ($writtenUtc -gt $startedUtc) {
            return ("CONFIGURATION_CHANGED_SINCE_START: $ConfigurationPath was written at " +
                "$($writtenUtc.ToString('o')), after the process of '$ServiceName' started at " +
                "$($startedUtc.ToString('o')). The running process still works on what the file said " +
                "when it started -- an open RIoT dispatch gate, possibly. Restart '$ServiceName' (the V2 service, NOT " +
                "'$script:ProductionServiceName') so it reads the file, then run this again." + $nothing)
        }
    }
    $dispatchRefusal = Get-ParallelUpgradeRefusal -Configuration $configuration -ConfigurationPath $ConfigurationPath -ServiceName $ServiceName
    return $dispatchRefusal
}

function Resolve-ParallelInstanceDatabasePath {
    <#
        .SYNOPSIS
            The SQLite file this instance's service reads, from the installed configuration; throws
            unless it sits inside the layout's DataRoot.

        .DESCRIPTION
            control-server#472. Taken from ConnectionStrings:ControlServer -- the value the running
            service uses -- rather than rebuilt from DataRoot, so a file someone moved is read where
            the service reads it. Held to DataRoot and away from every production path, so that a
            configuration pointing at the MVP's database is refused instead of read. Pure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Configuration,
        [Parameter(Mandatory = $true)][string] $DataRoot,
        [Parameter(Mandatory = $true)][string] $ConfigurationPath
    )
    $connection = [string] (Get-ConfigurationValue (Get-ConfigurationValue $Configuration 'ConnectionStrings') 'ControlServer')
    $match = [regex]::Match($connection, '(?i)(?:^|;)\s*Data Source\s*=\s*([^;]+)')
    if (-not $match.Success) {
        throw "$ConfigurationPath has no Data Source in ConnectionStrings:ControlServer ('$connection'), so the journey state cannot be read."
    }
    $path = [Environment]::ExpandEnvironmentVariables($match.Groups[1].Value.Trim()).Replace('/', '\')
    $root = $DataRoot.TrimEnd('\') + '\'
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or $path.Contains('\..\') -or
        (Test-ParallelInstancePathIsProduction -Path $path)) {
        throw "The database in $ConfigurationPath ($path) is not inside this instance's data root $DataRoot; refusing to read it."
    }
    return $path
}

function Test-ParallelOrderIntentNeverSent {
    <#
        .SYNOPSIS
            True when an OrderIntents row has certainly never been sent to RIoT.

        .DESCRIPTION
            control-server#472. A port of WireToGateStore.IsNeverSentAsync and
            IsNeverSentAfterUnansweredReadsAsync (control-server#375), the single definition the
            runtime and the release service read: PENDING_RECONCILIATION with no create attempt and no
            order, or RESULT_UNKNOWN only because every read before the create answered nothing. Every
            other state may have a live order in RIoT. Test-ParallelInstance.ps1 pins the C# predicate
            literally, so this port cannot drift silently from it. Pure: the row and its audit events
            as read from the database (column names as there).
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Intent,
        [AllowEmptyCollection()][object[]] $AuditEvents = @()
    )
    $attempts = $Intent['CreateAttemptCount']
    $noAttempt = $null -ne $attempts -and [long] $attempts -eq 0 -and $null -eq $Intent['CreateAttemptId']
    if ([string] $Intent['Status'] -ceq 'PENDING_RECONCILIATION' -and $noAttempt -and $null -eq $Intent['OrderId']) {
        return $true
    }
    if ([string] $Intent['Status'] -cne 'RESULT_UNKNOWN' -or $null -eq $Intent['DispatchAuditVersion'] -or
        [long] $Intent['DispatchAuditVersion'] -ne 1 -or -not $noAttempt -or $null -ne $Intent['ExperimentalCreateAuthorizationId']) {
        return $false
    }
    $reads = @($AuditEvents | Where-Object { [string] $_['MovementLegId'] -ceq [string] $Intent['MovementLegId'] })
    if ($reads.Count -eq 0) { return $false }
    foreach ($read in $reads) {
        if ([string] $read['Phase'] -cne 'PRE_CREATE_RECONCILIATION' -or [string] $read['Outcome'] -cnotin @('UNKNOWN', 'NOT_FOUND') -or
            $null -ne $read['AttemptId'] -or $null -ne $read['ReturnedOrderId'] -or
            ($null -ne $read['ResultPresent'] -and [bool] $read['ResultPresent'])) {
            return $false
        }
    }
    return $true
}

function Test-ParallelJourneyAwaitsRecovery {
    <#
        .SYNOPSIS
            8005-workspace#61. Whether a journey stands Blocked on a slot operation result that needs an exception
            recovery session. Pure.

        .DESCRIPTION
            The same definition as Test-V2JourneyAwaitsRecovery in 8005-workspace's
            remote-ops/onboard-hmi/scripts/V2RecoveryWindow.psm1 (8005-workspace#58), read from this repository:
            Block() sets Stage Blocked and the reason (src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs),
            LOAD_RESULT_REQUIRES_RECOVERY when the load operation is RecoveryRequired, UNLOAD_RESULT_REQUIRES_RECOVERY
            for the unload, and a session result that did not reconcile keeps it Blocked as <messageType>_NOT_RECONCILED
            (OnboardRecoveryCoordinator.cs; BlockedJourneyRelease.NotReconciledSuffix). Not the unreleasable
            *_NOT_RECONCILED_ON_ENDED_DEMAND / *_BEFORE_UPGRADE (BlockedJourneyRelease.IsUnreleasable), nor any other reason.
    #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([AllowNull()][AllowEmptyString()][string] $Stage, [AllowNull()][AllowEmptyString()][string] $BlockReasonCode)
    if ($Stage -cne 'Blocked' -or [string]::IsNullOrEmpty($BlockReasonCode)) { return $false }
    return ($BlockReasonCode -cin @('LOAD_RESULT_REQUIRES_RECOVERY', 'UNLOAD_RESULT_REQUIRES_RECOVERY')) -or
        ($BlockReasonCode -cmatch '^[A-Za-z]+_NOT_RECONCILED$')
}

function ConvertTo-ParallelRecoveryJourneyArgument {
    <#
        .SYNOPSIS
            8005-workspace#61. The -RecoveryJourney argument of Set-ParallelDispatchGateLocal.ps1: base64 of the UTF-8 JSON
            array [{ journeyId, agvId, vehicleKey }, ...], one entry per journey named. Pure.

        .DESCRIPTION
            Encoded because it travels inside an ssh command line: a journey id carries '|' (journey:<demandId>|g<generation>)
            and an agvId is Chinese, and neither may be re-quoted or re-encoded on the way. Base64 has neither problem.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary[]] $Journeys)
    $list = @($Journeys | ForEach-Object { [ordered]@{ journeyId = [string] $_['journeyId']; agvId = [string] $_['agvId']; vehicleKey = [string] $_['vehicleKey'] } })
    $json = ConvertTo-Json -InputObject $list -Compress -Depth 3
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
}

function ConvertFrom-ParallelRecoveryJourneyArgument {
    <#
        .SYNOPSIS
            8005-workspace#61. Decodes ConvertTo-ParallelRecoveryJourneyArgument's text into @{ journeyId; agvId; vehicleKey }
            entries, written to the pipeline one by one (collect them with @(...)), at least one, no journey id twice. Throws GATE_RECOVERY_JOURNEY_MALFORMED on
            anything else. Pure.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Encoded)
    $malformed = ('GATE_RECOVERY_JOURNEY_MALFORMED: -RecoveryJourney must be the base64 of a JSON array of objects with journeyId, ' +
        'agvId and vehicleKey, at least one, no journey twice. Nothing was stopped or changed.')
    try {
        $value = ConvertFrom-Json -InputObject ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Encoded))) -AsHashtable -NoEnumerate
    } catch { throw $malformed }
    if ($value -isnot [array] -or $value.Count -eq 0) { throw $malformed }
    $result = foreach ($entry in $value) {
        if ($entry -isnot [System.Collections.IDictionary]) { throw $malformed }
        $one = @{}
        foreach ($key in 'journeyId', 'agvId', 'vehicleKey') {
            if (-not $entry.Contains($key) -or $entry[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($entry[$key])) { throw $malformed }
            $one[$key] = $entry[$key]
        }
        $one
    }
    $result = @($result)
    if (@($result | ForEach-Object { $_['journeyId'] } | Sort-Object -Unique -CaseSensitive).Count -ne $result.Count) { throw $malformed }
    return $result
}

function Get-ParallelRecoveryJourneyNotInFlight {
    <#
        .SYNOPSIS
            8005-workspace#61 review L2. The journey ids -RecoveryJourney names that are not among $State's journeys not
            Completed -- finished already, or mistyped -- so that a refusal and the dry run can say so instead of passing
            over them in silence. Pure; nothing on an unreadable state.
    #>
    [CmdletBinding()]
    param([AllowNull()] $State, [AllowNull()][AllowEmptyCollection()][object[]] $RecoveryJourney)
    if ($State -isnot [System.Collections.IDictionary] -or $State.Contains('Error')) { return }
    $active = @($State['Journeys'] | Where-Object { [string] $_['Stage'] -cne 'Completed' } | ForEach-Object { [string] $_['JourneyId'] })
    @($RecoveryJourney | Where-Object { $null -ne $_ } | ForEach-Object { [string] $_['journeyId'] } | Where-Object { $active -cnotcontains $_ })
}

function ConvertTo-ParallelGateDirection {
    <#
        .SYNOPSIS
            The gate change -State asks for: Closed is Close (RiotCreateDispatch.enabled false), Open is Open (true).

        .DESCRIPTION
            control-server#472 review S2. One place, pinned by Test-ParallelInstance.ps1: a swapped mapping makes
            "-State Closed" a no-op on an open gate that still reports success. Pure.

            8005-workspace#61: both spellings exact, and nothing else. Until then the ValidateSet ignored case while the
            mapping did not, so "-State closed" got past the one and fell into Open -- a close that opened the gate (present
            at aba28e30). The set is case-sensitive now, and a value that is neither throws instead of becoming Open.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][ValidateSet('Closed', 'Open', IgnoreCase = $false)][string] $State)
    if ($State -ceq 'Closed') { return 'Close' }
    if ($State -ceq 'Open') { return 'Open' }
    throw "GATE_STATE_INVALID: -State is 'Closed' or 'Open', exactly; '$State' is neither. Nothing was stopped or changed."
}

# 8005-workspace#68. The RIoT order states (RiotOrderState) a CONFIRMED intent's order may be read as ended in: CANCELED 2,
# FAILED 4, SUCCESS 5. Not DELETED 6, which the ticket did not name; an order in it still holds the gate.
$script:RiotEndedOrderStates = @(2, 4, 5)
# The non-final states the listing is filtered by, HttpRiotMovementGateway.NonFinalOrderStates: QUEUEING 1, EXECUTING 3,
# PAUSED 7, SUSPENDED 8, HANG 9, QUEUE_PRIORITY 10.
$script:RiotNonFinalOrderStates = @(1, 3, 7, 8, 9, 10)

function Get-ParallelRiotText {
    # One field of a RIoT answer as text, $null when absent or null.
    param([System.Collections.IDictionary] $Node, [string] $Name)
    return ($Node.Contains($Name) -and $null -ne $Node[$Name]) ? [string] $Node[$Name] : $null
}

function Get-ParallelRiotVehicleMotionReason {
    <#
        .SYNOPSIS
            8005-workspace#68. Why RIoT does not show a vehicle standing still: nothing when it does, else one reason per
            fact, each a code and what was read. Pure.

        .DESCRIPTION
            A port of HttpRiotMovementGateway.Safety() (src/ControlServer.Infrastructure/Adapters), the predicate every vehicle
            safety read of the server uses -- the onboard safety projection, the charging stop proof, idle return -- so that
            "standing still" here is what it is there, codes included. Over getVehicleInfo/{deviceKey} as RIoT answers it
            (vehicle: movementState, mode, speed, breakSwitchState, emergencyState, controlState, locationState;
            vehicleTaskInfo: key, procState, processingOrder, enable, integrationLevel) and one complete non-final order listing:
              * moving outright: speed not 0, or MT_RUNNING (RIOT_MOTION_ACTIVE);
              * otherwise every one of procState IDLE, processingOrder false, enable true, integrationLevel ON_LINE,
                emergencyState OK, breakSwitchState MOVABLE (the brake engaged; the name runs against intuition),
                MODE_AUTO, controlState CONTROL_STATE_OK, locationState LOCATION_STATE_RUNNING, speed exactly 0, and
                movementState MT_FINISHED -- or MT_NA with MODE_AUTO and the brake engaged (control-server#639) --
                and no non-final order appointed to or executed by the vehicle.
            sysState is not among them: Safety() does not read it, and VehicleFaultCoordinator lists it as a candidate whose
            meanings are not approved (agv03 on its charger reads CHARGING, agv01 under an emergency stop ERROR).
            Anything RIoT did not answer -- the facts not read, a read that failed, an answer for another key, a listing that
            did not add up -- is a reason too: "standing still" has to be something RIoT said (RIOT_NOT_READ,
            RIOT_VEHICLE_KEY_MISMATCH, RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN).

            -Riot is what Read-ParallelRiotGateFacts added to the state: Error, or Vehicles (deviceKey -> @{ Vehicle } or
            @{ Error }) and NonFinalOrders (@{ Records } or @{ Error }).
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $VehicleKey, [AllowNull()] $Riot)
    if ($Riot -isnot [System.Collections.IDictionary]) { return [string[]] @('RIOT_NOT_READ: RIoT was not read') }
    if ($Riot.Contains('Error')) { return [string[]] @("RIOT_NOT_READ: $($Riot['Error'])") }
    $entry = ($Riot['Vehicles'] -is [System.Collections.IDictionary] -and $Riot['Vehicles'].Contains($VehicleKey)) ? $Riot['Vehicles'][$VehicleKey] : $null
    if ($entry -isnot [System.Collections.IDictionary] -or $entry.Contains('Error') -or $entry['Vehicle'] -isnot [System.Collections.IDictionary]) {
        $why = ($entry -is [System.Collections.IDictionary] -and $entry.Contains('Error')) ? $entry['Error'] : 'no answer for this vehicle'
        return [string[]] @("RIOT_NOT_READ: getVehicleInfo/$VehicleKey ($why)")
    }
    $vehicle = $entry['Vehicle']['vehicle']
    $task = $entry['Vehicle']['vehicleTaskInfo']
    if ($vehicle -isnot [System.Collections.IDictionary] -or $task -isnot [System.Collections.IDictionary]) {
        return [string[]] @("RIOT_NOT_READ: getVehicleInfo/$VehicleKey has no vehicle or vehicleTaskInfo")
    }
    $listing = $Riot['NonFinalOrders']
    $reasons = [System.Collections.Generic.List[string]]::new()
    # The SDK refuses an answer whose vehicleTaskInfo.key is not the key asked for; so does this.
    if ([string] $task['key'] -cne $VehicleKey) { $reasons.Add("RIOT_VEHICLE_KEY_MISMATCH (vehicleTaskInfo.key '$($task['key'])')") }
    $movement = Get-ParallelRiotText $vehicle 'movementState'
    $mode = Get-ParallelRiotText $vehicle 'mode'
    $brake = Get-ParallelRiotText $vehicle 'breakSwitchState'
    $rawSpeed = $vehicle.Contains('speed') ? $vehicle['speed'] : $null
    [double] $speed = 0
    $speedKnown = $null -ne $rawSpeed -and $rawSpeed -isnot [bool] -and $rawSpeed -isnot [string] -and [double]::TryParse([string] $rawSpeed, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $speed)
    if (($speedKnown -and $speed -ne 0) -or $movement -ceq 'MT_RUNNING') {
        $reasons.Add("RIOT_MOTION_ACTIVE (movementState $($movement ?? 'none'), speed $($rawSpeed ?? 'none'))")
        return [string[]] $reasons.ToArray()
    }
    $auto = $mode -ceq 'MODE_AUTO'
    $braked = $brake -ceq 'MOVABLE'
    $bootAtRest = $movement -ceq 'MT_NA' -and $auto -and $braked
    if ((Get-ParallelRiotText $task 'procState') -cne 'IDLE') { $reasons.Add("RIOT_PROC_NOT_IDLE (procState $((Get-ParallelRiotText $task 'procState') ?? 'none'))") }
    if (-not ($task.Contains('processingOrder') -and $task['processingOrder'] -is [bool] -and -not $task['processingOrder'])) {
        $reasons.Add("RIOT_PROCESSING_ORDER_UNKNOWN_OR_ACTIVE (processingOrder $($task.Contains('processingOrder') ? ($task['processingOrder'] ?? 'null') : 'none'))")
    }
    if (-not ($task.Contains('enable') -and $task['enable'] -is [bool] -and $task['enable'])) { $reasons.Add("RIOT_VEHICLE_NOT_ENABLED (enable $((Get-ParallelRiotText $task 'enable') ?? 'none'))") }
    if ((Get-ParallelRiotText $task 'integrationLevel') -cne 'ON_LINE') { $reasons.Add("RIOT_VEHICLE_NOT_ONLINE (integrationLevel $((Get-ParallelRiotText $task 'integrationLevel') ?? 'none'))") }
    if ((Get-ParallelRiotText $vehicle 'emergencyState') -cne 'OK') { $reasons.Add("RIOT_EMERGENCY_NOT_OK (emergencyState $((Get-ParallelRiotText $vehicle 'emergencyState') ?? 'none'))") }
    if (-not $braked) { $reasons.Add("RIOT_BRAKE_NOT_MOVABLE (breakSwitchState $($brake ?? 'none'))") }
    if (-not $auto) { $reasons.Add("RIOT_MODE_NOT_AUTO (mode $($mode ?? 'none'))") }
    if ((Get-ParallelRiotText $vehicle 'controlState') -cne 'CONTROL_STATE_OK') { $reasons.Add("RIOT_CONTROL_NOT_OK (controlState $((Get-ParallelRiotText $vehicle 'controlState') ?? 'none'))") }
    if ((Get-ParallelRiotText $vehicle 'locationState') -cne 'LOCATION_STATE_RUNNING') { $reasons.Add("RIOT_LOCATION_NOT_RUNNING (locationState $((Get-ParallelRiotText $vehicle 'locationState') ?? 'none'))") }
    if (-not $speedKnown -or $speed -ne 0) { $reasons.Add("RIOT_SPEED_NOT_ZERO (speed $($rawSpeed ?? 'none'))") }
    if ($movement -cne 'MT_FINISHED' -and -not $bootAtRest) { $reasons.Add("RIOT_MOVEMENT_NOT_FINISHED (movementState $($movement ?? 'none'))") }
    if ($listing -isnot [System.Collections.IDictionary] -or $listing.Contains('Error') -or -not $listing.Contains('Records')) {
        $why = ($listing -is [System.Collections.IDictionary] -and $listing.Contains('Error')) ? $listing['Error'] : 'the non-final order listing was not read'
        $reasons.Add("RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN ($why)")
    } else {
        $orders = @($listing['Records'] | Where-Object { $_ -is [System.Collections.IDictionary] -and
                ([string] $_['appointVehicleKey'] -ceq $VehicleKey -or [string] $_['executeVehicleKey'] -ceq $VehicleKey) })
        if ($orders.Count -gt 0) {
            $reasons.Add("RIOT_NONFINAL_ORDER_PRESENT ($(@($orders | ForEach-Object { "$($_['orderId']) / $($_['upperId']) orderState $($_['orderState'])" }) -join ', '))")
        }
    }
    return [string[]] $reasons.ToArray()
}

function Get-ParallelRiotOrderEndReason {
    <#
        .SYNOPSIS
            8005-workspace#68. Nothing when RIoT, read now, shows the order of a CONFIRMED order intent ended (SUCCESS, FAILED
            or CANCELED); otherwise why it may still be running. Pure.

        .DESCRIPTION
            The order is read by the intent's UpperId (detailByUpperId, the read the runtime itself reconciles with) and must
            be the very order the intent recorded: the answer's upperId the intent's UpperId and its orderId the intent's
            OrderId, both ordinal -- an intent without an OrderId cannot be matched and is not let through. Only CONFIRMED:
            every other state that may have sent an order (CREATE_ATTEMPTED, RESULT_UNKNOWN, TERMINAL_RECONCILIATION_REQUIRED)
            keeps refusing as before, whatever RIoT says, since the server has not settled what was created.

            -Riot as Read-ParallelRiotGateFacts added it: Error, or Orders (upperId -> @{ Record }, the record $null when RIoT
            has none, or @{ Error }).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $Intent, [AllowNull()] $Riot)
    $upper = [string] $Intent['UpperId']
    if ([string] $Intent['Status'] -cne 'CONFIRMED') { return "the intent is $($Intent['Status']), not CONFIRMED, so RIoT's answer does not settle it" }
    if ($Riot -isnot [System.Collections.IDictionary]) { return 'RIOT_NOT_READ: RIoT was not read' }
    if ($Riot.Contains('Error')) { return "RIOT_NOT_READ: $($Riot['Error'])" }
    $entry = ($Riot['Orders'] -is [System.Collections.IDictionary] -and $Riot['Orders'].Contains($upper)) ? $Riot['Orders'][$upper] : $null
    if ($entry -isnot [System.Collections.IDictionary] -or $entry.Contains('Error') -or -not $entry.Contains('Record')) {
        $why = ($entry -is [System.Collections.IDictionary] -and $entry.Contains('Error')) ? $entry['Error'] : 'no answer for this upperId'
        return "RIOT_NOT_READ: detailByUpperId/$upper ($why)"
    }
    $record = $entry['Record']
    if ($null -eq $record) { return "RIOT_ORDER_ABSENT: RIoT has no order under upperId $upper" }
    if ($record -isnot [System.Collections.IDictionary]) { return "RIOT_NOT_READ: detailByUpperId/$upper answered something that is not an order" }
    if ([string] $record['upperId'] -cne $upper -or [string]::IsNullOrEmpty([string] $Intent['OrderId']) -or [string] $record['orderId'] -cne [string] $Intent['OrderId']) {
        return ("RIOT_ORDER_MISMATCH: RIoT answered order $($record['orderId']) / upperId $($record['upperId']) for the intent's order " +
            "$($Intent['OrderId'] ?? '(none recorded)') / $upper")
    }
    $state = $record['orderState']
    if ($null -ne $state -and $state -isnot [bool] -and $state -isnot [string] -and [string] $state -match '^\d+$' -and [int] $state -in $script:RiotEndedOrderStates) { return $null }
    return "RIoT shows order $($record['orderId']) in orderState $($state ?? 'none'), not ended (SUCCESS 5, FAILED 4, CANCELED 2)"
}

function Get-ParallelRiotSetting {
    # RIoT:<Key> from the first tree that has it (appsettings.Production.json before appsettings.json), ignoring case.
    param([object[]] $Trees, [string] $Key)
    foreach ($tree in $Trees) {
        $found = Get-ConfigurationValue (Get-ConfigurationValue $tree 'RIoT') $Key
        if ($null -ne $found) { return $found }
    }
    return $null
}

function Resolve-ParallelRiotGateConnection {
    <#
        .SYNOPSIS
            8005-workspace#68. Where the gate script reads RIoT from, as the installed service would: [ordered]@{ BaseUrl;
            ApiKey; KeyVariable; KeySource; TimeoutSeconds }, or @{ Error } when it cannot be told. Never carries the key into
            an error. Pure.

        .DESCRIPTION
            RiotSdkRegistration.AddRiotSdkIntegration reads RIoT:baseUrl (default http://127.0.0.1:58888, trailing '/'
            trimmed), RIoT:timeoutSeconds (default 30, positive) and the key from the environment variable
            RIoT:callApiKeyEnvironmentVariable names. Read here as .NET configuration reads them: appsettings.json
            (-BaseConfiguration) with appsettings.Production.json (-Configuration) over it, keys ignoring case. The key comes
            from the service's own registry Environment (-ServiceEnvironment, where the product installer writes it) and
            otherwise from the machine scope (-MachineVariable(name)), the two places the service process inherits it from.
    #>
    [CmdletBinding()]
    param(
        [AllowNull()][System.Collections.IDictionary] $BaseConfiguration,
        [AllowNull()][System.Collections.IDictionary] $Configuration,
        [AllowNull()][AllowEmptyCollection()][string[]] $ServiceEnvironment,
        [Parameter(Mandatory = $true)][scriptblock] $MachineVariable
    )

    $baseUrl = ([string] ((Get-ParallelRiotSetting -Trees @($Configuration, $BaseConfiguration) -Key 'baseUrl') ?? 'http://127.0.0.1:58888')).TrimEnd('/')
    [uri] $parsed = $null
    if (-not [uri]::TryCreate($baseUrl, [UriKind]::Absolute, [ref] $parsed) -or $parsed.Scheme -cnotin @('http', 'https')) {
        return @{ Error = "RIOT_CONNECTION_INVALID: RIoT:baseUrl '$baseUrl' is not an absolute http(s) URL." }
    }
    $timeout = Get-ParallelRiotSetting -Trees @($Configuration, $BaseConfiguration) -Key 'timeoutSeconds'
    [double] $seconds = 30
    if ($null -ne $timeout -and (-not [double]::TryParse([string] $timeout, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $seconds) -or $seconds -le 0)) {
        return @{ Error = "RIOT_CONNECTION_INVALID: RIoT:timeoutSeconds '$timeout' is not a positive number." }
    }
    $variable = [string] (Get-ParallelRiotSetting -Trees @($Configuration, $BaseConfiguration) -Key 'callApiKeyEnvironmentVariable')
    if ([string]::IsNullOrWhiteSpace($variable)) {
        return @{ Error = 'RIOT_KEY_MISSING: the installed configuration names no RIoT:callApiKeyEnvironmentVariable, so the RIoT call API key cannot be found.' }
    }
    $key = Get-ParallelServiceEnvironmentEntry -Environment $ServiceEnvironment -Name $variable
    $source = 'the service Environment'
    if ([string]::IsNullOrEmpty($key)) { $key = [string] (& $MachineVariable $variable); $source = 'the machine scope' }
    if ([string]::IsNullOrEmpty($key)) {
        return @{ Error = "RIOT_KEY_MISSING: $variable is neither in the service's Environment nor in the machine scope, so RIoT cannot be read." }
    }
    return [ordered]@{ BaseUrl = $baseUrl; ApiKey = $key; KeyVariable = $variable; KeySource = $source; TimeoutSeconds = $seconds }
}

function Get-ParallelGateSentIntent {
    <#
        .SYNOPSIS
            8005-workspace#68 (moved out of Get-ParallelDispatchGateRefusal). The order intents the open rule counts: for
            each journey not Completed, those that may have been sent. One @{ Journey; Intent; Problem } per pair; Problem is
            text (and Intent $null when nothing could be dated) for what cannot be read. Pure.

        .DESCRIPTION
            A journey's intents are those named on its row (PickupUpperId, GateUpperId) plus every intent for its vehicle
            created at or after the journey was: the engine keeps at most one active journey per vehicle, and later stops,
            rebuilds and charging legs get intents of their own that the row does not name. Sent is every intent not
            certainly never sent (Test-ParallelOrderIntentNeverSent). Anything that cannot be dated is a Problem.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $State)
    $intents = @($State['OrderIntents'])
    foreach ($journey in @($State['Journeys'] | Where-Object { [string] $_['Stage'] -cne 'Completed' })) {
        [DateTimeOffset] $since = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string] $journey['CreatedAt'], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $since)) {
            @{ Journey = $journey; Intent = $null; Problem = "its CreatedAt '$($journey['CreatedAt'])' cannot be read" }
            continue
        }
        $named = @([string] $journey['PickupUpperId'], [string] $journey['GateUpperId']) | Where-Object { $_ }
        foreach ($intent in $intents) {
            $mine = $named -ccontains [string] $intent['UpperId']
            if (-not $mine -and [string] $intent['VehicleKey'] -ceq [string] $journey['VehicleKey']) {
                [DateTimeOffset] $created = [DateTimeOffset]::MinValue
                if (-not [DateTimeOffset]::TryParse([string] $intent['CreatedAt'], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $created)) {
                    @{ Journey = $journey; Intent = $intent; Problem = "order intent $($intent['UpperId']) has an unreadable CreatedAt '$($intent['CreatedAt'])'" }
                    continue
                }
                $mine = $created -ge $since
            }
            if ($mine -and -not (Test-ParallelOrderIntentNeverSent -Intent $intent -AuditEvents @($State['AuditEvents']))) {
                @{ Journey = $journey; Intent = $intent; Problem = $null }
            }
        }
    }
}

function Get-ParallelGateVehicleMotion {
    <#
        .SYNOPSIS
            8005-workspace#68. For each vehicle of -Journeys, whether it may be under way: one [ordered]@{ VehicleKey; AgvId;
            Journeys (each "id (Stage)"); Reasons } per vehicle, Reasons empty when it stands still. Pure.

        .DESCRIPTION
            Standing still is Get-ParallelRiotVehicleMotionReason saying nothing, and on top of it the guard ws#61 put on a
            named recovery journey: its vehicle holds no fault (no VehicleFaultStates row of its AgvId with a Level other than
            None), because while a fault stands its stop proof lives in memory and a restart discards it (Program.cs:88-91).
            The faults not read is a reason too. A journey row without a vehicle key cannot be judged and is a reason.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][System.Collections.IDictionary] $State, [AllowEmptyCollection()][object[]] $Journeys)
    $riot = $State.Contains('Riot') ? $State['Riot'] : $null
    $byVehicle = [ordered]@{}
    foreach ($journey in @($Journeys)) {
        $key = [string] $journey['VehicleKey']
        if (-not $byVehicle.Contains($key)) { $byVehicle[$key] = [System.Collections.Generic.List[object]]::new() }
        $byVehicle[$key].Add($journey)
    }
    foreach ($key in @($byVehicle.Keys)) {
        $rows = @($byVehicle[$key])
        $agvIds = @($rows | ForEach-Object { [string] $_['AgvId'] } | Sort-Object -Unique -CaseSensitive)
        $reasons = [System.Collections.Generic.List[string]]::new()
        if ([string]::IsNullOrEmpty($key)) {
            $reasons.Add('NO_VEHICLE_KEY: the journey row names no vehicle key, so RIoT cannot be asked about it')
        } else {
            foreach ($reason in @(Get-ParallelRiotVehicleMotionReason -VehicleKey $key -Riot $riot)) { $reasons.Add($reason) }
        }
        if (-not $State.Contains('VehicleFaults')) {
            $reasons.Add('VEHICLE_FAULTS_NOT_READ: the vehicle faults were not read, so a stop proof a restart would drop cannot be ruled out')
        } else {
            foreach ($fault in @($State['VehicleFaults'] | Where-Object { [string] $_['AgvId'] -cin $agvIds -and [string] $_['Level'] -cne 'None' })) {
                $reasons.Add("VEHICLE_FAULT ($($fault['Level']) / $($fault['EvidenceCode'] ?? 'no evidence code')): a restart would drop its stop proof")
            }
        }
        [ordered]@{ VehicleKey = $key; AgvId = ($agvIds -join ', '); Journeys = @($rows | ForEach-Object { "$($_['JourneyId']) ($($_['Stage']))" }); Reasons = [string[]] $reasons.ToArray() }
    }
}

function Get-ParallelDispatchGateRefusal {
    <#
        .SYNOPSIS
            Why the RIoT dispatch gate must not be closed or opened now, judged from the instance's own
            journey state; $null when it may.

        .DESCRIPTION
            control-server#472. Both directions restart the V2 service, which stops the journey
            runtime's fault supervision for as long as the restart takes, so both refuse while a vehicle
            may be under one of this instance's RIoT orders. They differ in what counts:

              * Close: the journeys whose Stage is not Completed (the engine's own definition of active,
                JourneyRuntimeEngine; Blocked counts), judged by their vehicle (8005-workspace#68): a
                vehicle may be under way unless RIoT, read now, shows it standing still and it holds no
                fault (Get-ParallelGateVehicleMotion). Until #68 every such journey refused, so a vehicle
                parked on its charger -- its charging journey stays AwaitingPickupArrival until the
                battery reads full, long after its charge order ended -- held a planned restart for
                hours. No HTTP endpoint lists the journeys -- each /api/dashboard/* query returns a
                subset -- which is why this reads the database.
              * Open: a journey whose order has been, or may have been, sent. With the gate closed no
                order can be created (MovementDispatchService returns CreateDispatchDisabled and writes
                nothing), so the journeys that exist then are waiting for exactly this gate, and refusing
                them would be a deadlock only a database edit could break. Refused is any journey with an
                order intent that is not certainly never sent (Get-ParallelGateSentIntent), except a
                CONFIRMED one whose order RIoT, read now, shows ended (Get-ParallelRiotOrderEndReason;
                8005-workspace#68: until then CONFIRMED always counted, and a pickup order that had ended
                at 11:23 held the gate on 2026-10-21). Anything that cannot be dated or read refuses.

            $State is Get-ParallelJourneyDispatchState's output with Read-ParallelRiotGateFacts' Riot added,
            or $null / an Error string when it could not be read, which refuses in both directions (fail
            closed). Without Riot every vehicle and every CONFIRMED order counts as RIOT_NOT_READ, which is
            the rule from before #68. Pure.

            -RecoveryJourney (8005-workspace#61; ConvertFrom-ParallelRecoveryJourneyArgument's list of journeyId, agvId,
            vehicleKey), for closing only, names the journeys vehicles stuck on a slot operation result are recovered from.
            Closing needs no journey active, and a stuck journey is active, while 18's recovery window, 19 and the task-type
            station activation all need the gate closed first. With it, the journeys not Completed that are not named
            refuse as before; each named one that is active passes only when all of these hold, read by
            Get-ParallelJourneyDispatchState -RecoveryJourneyId:
              * its row is on the vehicle named with it (AgvId and VehicleKey, ordinal);
              * it awaits recovery (Test-ParallelJourneyAwaitsRecovery): Blocked, so the restart takes nothing from it --
                AdvanceAsync returns at once for a Blocked journey (JourneyRuntimeEngine.cs:2133-2135), and the fault model
                is observed only on the arrival-stage paths (faults.ObserveAsync at JourneyRuntimeEngine.cs:2283 and
                JourneyRuntimeEngine.InTransitDoors.cs:118, :208), so a Blocked journey has no supervision that rides on
                it. (Not that its vehicle is at a station: a *_NOT_RECONCILED can follow a handoff after a fault, with the
                vehicle wherever it stopped, VehicleFaultRecoveryService.StoppedRebuild.cs:260-345.)
              * its vehicle holds no fault (no VehicleFaultStates row of its AgvId with a Level other than None) -- a
                guard on top: while a fault stands, its stop proof (JourneyRuntimeEngine.cs:2245-2262) lives in memory and
                a restart discards it (Program.cs:88-91). It rests on the invariant that an emergency-stop episode not yet
                released keeps its vehicle's Level other than None (VehicleFaultCoordinator: NewDispatchBlocked is
                Level != None). Such a vehicle waits for its fault to clear -- the order resumed automatically
                (RESUME_HELD_ORDER) -- or for the system maintainer (control-server#771); the instance's
                vehicleFaultRecovery entry is off in production.
            A named journey that is not active waives nothing; a refusal names it (Get-ParallelRecoveryJourneyNotInFlight). Opening ignores it: it is not widened, because the journey
            ends Completed through control-server#771 and is then no longer among the journeys judged. Without
            -RecoveryJourney nothing here changes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Close', 'Open')][string] $Direction,
        [AllowNull()] $State,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][string] $DatabasePath,
        [AllowNull()][AllowEmptyCollection()][object[]] $RecoveryJourney
    )
    $nothing = ' Nothing was stopped or changed.'
    if ($null -eq $State -or $State -isnot [System.Collections.IDictionary] -or $State.Contains('Error')) {
        $why = ($State -is [System.Collections.IDictionary] -and $State.Contains('Error')) ? $State['Error'] : 'no state was returned'
        return ("GATE_STATE_UNREADABLE: the journey state of '$ServiceName' could not be read from $DatabasePath ($why), so " +
            'whether a vehicle is under way cannot be told.' + $nothing)
    }
    $journeys = @($State['Journeys'] | Where-Object { [string] $_['Stage'] -cne 'Completed' })
    if ($Direction -eq 'Close') {
        # 8005-workspace#61: each named journey that is among the active ones is judged on its own.
        foreach ($named in @($RecoveryJourney | Where-Object { $null -ne $_ })) {
            $recoveryId = [string] $named['journeyId']
            if (@($journeys | Where-Object { [string] $_['JourneyId'] -ceq $recoveryId }).Count -eq 0) { continue }
            $row = $State.Contains('RecoveryJourneys') ? @($State['RecoveryJourneys'] | Where-Object { [string] $_['JourneyId'] -ceq $recoveryId }) : @()
            if ($row.Count -ne 1 -or -not $State.Contains('VehicleFaults')) {
                return ("GATE_RECOVERY_JOURNEY_UNREADABLE: the recovery journey $recoveryId of '$ServiceName' is active in $DatabasePath, " +
                    'but its row or the vehicle faults were not read, so it cannot be judged.' + $nothing)
            }
            $row = $row[0]
            if ([string] $row['AgvId'] -cne [string] $named['agvId'] -or [string] $row['VehicleKey'] -cne [string] $named['vehicleKey']) {
                return ("GATE_RECOVERY_JOURNEY_VEHICLE_MISMATCH: in $DatabasePath, journey $recoveryId of '$ServiceName' is on agvId " +
                    "'$($row['AgvId'])' / vehicleKey '$($row['VehicleKey'])', not the named vehicle's '$($named['agvId'])' / " +
                    "'$($named['vehicleKey'])'." + $nothing)
            }
            if (-not (Test-ParallelJourneyAwaitsRecovery -Stage ([string] $row['Stage']) -BlockReasonCode ([string] $row['BlockReasonCode']))) {
                return ("GATE_CLOSE_RECOVERY_JOURNEY_NOT_AWAITING_RECOVERY: in $DatabasePath, journey $recoveryId of '$ServiceName' " +
                    "is $($row['Stage']) / $($row['BlockReasonCode'] ?? '(no block reason)'), not Blocked on a slot operation result " +
                    'that needs recovery, so it may be under way like any other active journey.' + $nothing)
            }
            $fault = @($State['VehicleFaults'] | Where-Object { [string] $_['AgvId'] -ceq [string] $row['AgvId'] -and [string] $_['Level'] -cne 'None' })
            if ($fault.Count -gt 0) {
                return ("GATE_CLOSE_RECOVERY_JOURNEY_VEHICLE_FAULT: in $DatabasePath, the vehicle '$($row['AgvId'])' of journey $recoveryId " +
                    "holds a fault ($($fault[0]['Level']) / $($fault[0]['EvidenceCode'] ?? 'no evidence code')), and a restart of " +
                    "'$ServiceName' would drop its stop proof. Wait until the fault clears (the held order resumed automatically, " +
                    'RESUME_HELD_ORDER) and run this again, or have the system maintainer settle the journey (control-server#771).' + $nothing)
            }
            $journeys = @($journeys | Where-Object { [string] $_['JourneyId'] -cne $recoveryId })
        }
        if ($journeys.Count -eq 0) { return $null }
        # 8005-workspace#68: the rest are judged by their vehicle, and only a vehicle that may be under way holds the gate.
        $moving = @(Get-ParallelGateVehicleMotion -State $State -Journeys $journeys | Where-Object { $_['Reasons'].Count -gt 0 })
        if ($moving.Count -eq 0) { return $null }
        $listed = ($moving | ForEach-Object { "vehicle $($_['AgvId']) / $($_['VehicleKey']) with $($_['Journeys'] -join ', '): $($_['Reasons'] -join '; ')" }) -join ' | '
        $notInFlight = @(Get-ParallelRecoveryJourneyNotInFlight -State $State -RecoveryJourney $RecoveryJourney)
        if ($notInFlight.Count -gt 0) { $listed += " (named but not in flight: $($notInFlight -join ', '))" }
        return ("GATE_CLOSE_REFUSED_IN_FLIGHT: $DatabasePath has journey(s) of '$ServiceName' not Completed on $($moving.Count) vehicle(s) " +
            "that may be under way, judged from RIoT read now: $listed. A vehicle stands still for this when RIoT lists no unfinished " +
            'order on it and reads it MODE_AUTO, MT_FINISHED (or MT_NA with the brake engaged), speed 0, procState IDLE, on line, ' +
            'emergency OK, and it holds no fault; a journey on such a vehicle -- one charging on its charger, say -- does not hold ' +
            'the gate. Stop new intake first -- fake source: stop injecting demand into its FakeMesIngest; production source (the ' +
            'cutover): import a header-only area assignment table, so that no AREA is in scope (docs/production-cutover-mode-plan.md ' +
            'section 6, step 16a) -- then wait until those vehicles stand still or their journeys finish, and run this again.' + $nothing)
    }

    # 8005-workspace#68: a CONFIRMED intent whose RIoT order reads ended now is not one that may be running.
    $sent = [System.Collections.Generic.List[string]]::new()
    foreach ($pair in @(Get-ParallelGateSentIntent -State $State)) {
        $journey = $pair['Journey']; $intent = $pair['Intent']
        if ($pair['Problem']) { $sent.Add("$($journey['JourneyId']): $($pair['Problem'])"); continue }
        $why = Get-ParallelRiotOrderEndReason -Intent $intent -Riot ($State.Contains('Riot') ? $State['Riot'] : $null)
        if ($null -eq $why) { continue }
        $sent.Add("$($journey['JourneyId']) ($($journey['AgvId']), $($journey['Stage'])): order intent $($intent['UpperId']) is $($intent['Status'])" +
            $(if ($intent['OrderId']) { " with RIoT order $($intent['OrderId'])" } else { '' }) + " -- $why")
    }
    if ($sent.Count -eq 0) { return $null }
    return ("GATE_OPEN_REFUSED_ORDER_SENT: in $DatabasePath, a journey of '$ServiceName' has an order that was or may have been sent " +
        "to RIoT and is not shown ended there now (SUCCESS, FAILED or CANCELED, read for a CONFIRMED intent only), so a vehicle may be " +
        "under way and restarting the service would stop its fault supervision: $($sent -join '; '). " +
        'With the gate closed through this script no such order can exist; it happens when the gate was closed by hand while ' +
        'a journey was under way, and then this refusal does not lift by itself -- the journey never becomes Completed while the ' +
        "gate stays closed. The way out, in this order: (1) in RIoT, confirm that no order is running on the vehicle(s) named " +
        "above; (2) only then open the gate by hand as section 10 of remote-ops/factory-server/docs/wire-to-gate-parallel-cd.md " +
        "says: set RiotCreateDispatch.enabled to true in THIS instance's appsettings.Production.json (the V2 file), keeping it " +
        "UTF-8; (3) restart '$ServiceName' only -- never the MVP's service." + $nothing)
}

function Get-ConfigurationValue {
    # One key, ignoring case, as .NET configuration reads it.
    param($Node, [string] $Key)
    if ($Node -isnot [System.Collections.IDictionary]) { return $null }
    foreach ($candidate in @($Node.Keys)) {
        if ([string]::Equals([string] $candidate, $Key, [StringComparison]::OrdinalIgnoreCase)) { return $Node[$candidate] }
    }
    return $null
}

function Get-ParallelClearanceExitReadiness {
    <#
        .SYNOPSIS
            What the service will read for the manual station clearance exit, judged the way the
            server judges it, and said in one line.

        .DESCRIPTION
            control-server#454. The server tells nobody but its own log: startup alarm 2272 and,
            per cycle, 2271. This reads the same three inputs from outside -- the effective
            appsettings.Production.json, the names in the service's Environment, the roster file --
            and applies StationClearanceExit's rule:
              * RosterEmpty when no named person holds R-11 or R-13 (missing, unreadable and empty
                all count, as in FieldOperatorRoleRoster.AnyoneHolds);
              * NoEntry when the Host entry is not offered (VehicleFaultRecovery.enabled and its
                credential variable populated) and the onboard entry is not declared either;
              * NoRecoveryEntry when the Host entry is not offered, so isolation and the charging
                recovery have no exit (IsolationUnavailable, VehicleRecoveryUnavailable).
            Reasons are a state, possibly intended (phase 1 offers no exit on purpose). Fatal is a
            broken deployment: a section absent from the effective file, or the entry on without its
            credential, which the server answers by refusing to start.

            Pure. -EnvironmentNames are names only, of entries whose value is not blank; the line
            never carries a value. -RosterText is the file's text, or $null when it does not exist.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.Collections.IDictionary] $Configuration,
        [AllowEmptyCollection()][string[]] $EnvironmentNames = @(),
        [AllowNull()][AllowEmptyString()][string] $RosterText
    )

    $fatal = [System.Collections.Generic.List[string]]::new()
    $reasons = [System.Collections.Generic.List[string]]::new()

    $recovery = Get-ConfigurationValue $Configuration 'VehicleFaultRecovery'
    $enabled = $false
    $variable = 'CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL'
    if ($recovery -isnot [System.Collections.IDictionary]) {
        $fatal.Add('VehicleFaultRecovery is missing from the effective appsettings.Production.json: the Host recovery and clearance entries are off by default, and nothing records that anyone decided so.')
    } else {
        $enabled = (Get-ConfigurationValue $recovery 'enabled') -eq $true
        $named = [string] (Get-ConfigurationValue $recovery 'credentialEnvironmentVariable')
        if (-not [string]::IsNullOrWhiteSpace($named)) { $variable = $named }
    }
    $credentialPresent = @($EnvironmentNames | Where-Object { [string]::Equals($_, $variable, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if ($enabled -and -not $credentialPresent) {
        $fatal.Add("VehicleFaultRecovery.enabled is true but the service's Environment has no $variable; the service refuses to start without it (VehicleFaultRecoveryOptionsValidator).")
    }

    $roles = Get-ConfigurationValue $Configuration 'FieldOperatorRoles'
    $declared = $false
    $rosterPath = ''
    if ($roles -isnot [System.Collections.IDictionary]) {
        $fatal.Add('FieldOperatorRoles is missing from the effective appsettings.Production.json: the server has no roster, so nobody can confirm a clearance.')
    } else {
        $declared = (Get-ConfigurationValue $roles 'onboardClearanceEntryDeclared') -eq $true
        $rosterPath = [string] (Get-ConfigurationValue $roles 'path')
    }

    # FieldOperatorRoleRoster.AnyoneHolds: a named operator with R-11 or R-13; the server reads the
    # file with System.Text.Json's web defaults, so property names match ignoring case.
    $rosterState = 'missing'
    if ($null -ne $RosterText) {
        $rosterState = 'empty'
        try {
            $document = ConvertFrom-Json -InputObject $RosterText -AsHashtable -Depth 12 -ErrorAction Stop
            foreach ($entry in @(Get-ConfigurationValue $document 'operators')) {
                $operatorId = Get-ConfigurationValue $entry 'operatorId'
                $entryRoles = @(Get-ConfigurationValue $entry 'roles')
                if ($operatorId -is [string] -and -not [string]::IsNullOrWhiteSpace($operatorId) -and
                    @($entryRoles | Where-Object { $_ -is [string] -and ($_ -ceq 'R-11' -or $_ -ceq 'R-13') }).Count -gt 0) {
                    $rosterState = 'named'
                    break
                }
            }
        } catch {
            $rosterState = 'unreadable'
        }
    }

    $hostEntry = $enabled -and $credentialPresent
    if ($rosterState -ne 'named') { $reasons.Add('FIELD_OPERATOR_ROSTER_EMPTY') }
    if (-not $hostEntry -and -not $declared) { $reasons.Add('STATION_CLEARANCE_ENTRY_NOT_OFFERED') }
    if (-not $hostEntry) { $reasons.Add('CHARGING_RECOVERY_ENTRY_NOT_OFFERED') }

    $line = ('CLEARANCE_EXIT_READINESS vehicleFaultRecovery={0} credential={1}:{2} fieldOperatorRoles={3} roster={4} onboardEntryDeclared={5} exit={6}' -f
        (($recovery -is [System.Collections.IDictionary]) ? ($enabled ? 'enabled' : 'disabled') : 'MISSING'),
        $variable, ($credentialPresent ? 'present' : 'absent'),
        (($roles -is [System.Collections.IDictionary]) ? 'present' : 'MISSING'),
        "$($rosterPath):$rosterState", $declared.ToString().ToLowerInvariant(),
        ($reasons.Count -eq 0 ? 'available' : "unavailable($($reasons -join ','))"))
    return [pscustomobject]@{
        Fatal = @($fatal)
        Reasons = @($reasons)
        Line = $line
    }
}

function Test-ParallelAreaAssignmentZone {
    <#
        .SYNOPSIS
            control-server#669. The dispatch zones an area assignment table names, against the definition's
            journeyRuntime.allowedDispatchZones. Returns failure strings; none means the sets agree.

        .DESCRIPTION
            Offline, before a table is imported: the import itself can only be dry-run against a database whose
            stored dispatch policy already holds the zones (AreaAssignmentImport, DISPATCH_ZONE_NOT_FOUND), and a
            table routing an AREA into a zone the definition does not allow keeps a dispatching Host from starting
            (AreaAssignmentDispatchZoneStartupCheck). The two directions are both refused here:

              1. a zone a table names that allowedDispatchZones omits (the import refuses it, or the Host does);
              2. an allowed zone no table names: no AREA routes into it, so the car that serves only it is never
                 given work -- silently, since an unmapped AREA is OUT_OF_SCOPE_AREA by design.

            ONE file, the one that will be imported (PR #676 review M1). An import is a whole table: each one
            writes a new version, the current version is the highest, and import-area-assignments takes one
            --input (AreaAssignmentStore.WriteVersionAsync, FieldOps). Importing an N table and then a T table
            leaves only the T areas in scope, and every N demand is OUT_OF_SCOPE_AREA, silently. Comparing the
            union of two files would pass exactly that, so more than one file is refused: merge them first.

            The file must carry the importer's controlled header exactly (AreaAssignmentImport.Header) and three
            fields per row; anything else is said and its zones are not counted. This reads the zone column
            and nothing more -- AREA format, slot position groups and duplicates are the importer's dry-run.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable] $Definition,
        [Parameter(Mandatory = $true)][string[]] $Path
    )

    $allowed = @($Definition['journeyRuntime']['allowedDispatchZones'])
    if (@($Path).Count -ne 1) {
        return @("$(@($Path).Count) area assignment tables given ($(@($Path) -join ', ')). An import replaces the whole table -- the last one imported is the only one in force -- so merge them into one file under one header, compare that file, dry-run it, and import it once.")
    }
    $failures = @()
    $named = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $Path) {
        $lines = @(([IO.File]::ReadAllText($file, [Text.UTF8Encoding]::new($false)) -replace "`r`n", "`n").TrimEnd("`n").Split("`n"))
        if ($lines[0] -cne 'area,dispatch_zone,slot_position') {
            $failures += "$file does not start with the importer's header 'area,dispatch_zone,slot_position' (line 1 is '$($lines[0])'); its zones are not counted."
            continue
        }
        $fileZones = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
        $malformed = @()
        for ($i = 1; $i -lt $lines.Count; $i++) {
            $fields = $lines[$i].Split(',')
            if ($fields.Count -ne 3) { $malformed += $i + 1; continue }
            $null = $fileZones.Add($fields[1])
        }
        if ($malformed.Count -gt 0) {
            $failures += "$file has rows that are not three fields (lines $($malformed -join ', ')); its zones are not counted."
            continue
        }
        foreach ($zone in $fileZones) {
            $null = $named.Add($zone)
            if (-not ($allowed -ccontains $zone)) {
                $failures += "$file routes AREAs into dispatch zone '$zone', which journeyRuntime.allowedDispatchZones [$($allowed -join ', ')] omits: the import refuses it (DISPATCH_ZONE_NOT_FOUND), or a dispatching Host refuses to start (AreaAssignmentDispatchZoneStartupCheck)."
            }
        }
    }
    foreach ($zone in $allowed) {
        if (-not $named.Contains([string] $zone)) {
            $failures += "journeyRuntime.allowedDispatchZones names '$zone', but no table routes an AREA into it: the vehicles that serve only that zone would never be given work."
        }
    }
    return $failures
}

function Merge-ConfigurationTree {
    <#
        .SYNOPSIS
            Recursive per-key merge of Overlay onto Base, Overlay winning.

        .DESCRIPTION
            Arrays are replaced wholesale rather than concatenated: allowedWorkTypes is a
            closed list, and an append would silently widen it on every redeployment. That holds for
            THIS file only. The Host reads appsettings.json under it, and .NET configuration merges
            arrays across files by index -- a one-item list here over six in the package used to leave
            five of them bound (control-server#535 review M1). JourneyRuntimeOptionsRegistration now
            takes allowedWorkTypes and allowedDispatchZones whole from the last file that names them;
            any other list this overlay ever writes needs the same, or the same length as the package's.

            Keys match ignoring case, as .NET configuration matches them, and that comes from one
            line: $result is an [ordered] literal, whose dictionary compares keys ignoring case. A
            base key that differs from an overlay key only in case -- 'vehicleFaultRecovery' or
            'Enabled' from a hand merge -- is therefore merged into, not written beside: a file
            repeating a key is refused by the JSON configuration provider. The upgrade case in
            Test-ParallelInstance.ps1 (control-server#454) pins this; swap the literal for a
            case-sensitive dictionary and it goes red.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Base,
        [Parameter(Mandatory = $true)] $Overlay
    )

    $result = [ordered]@{}
    if ($Base -is [hashtable] -or $Base -is [System.Collections.Specialized.OrderedDictionary]) {
        foreach ($key in $Base.Keys) { $result[$key] = $Base[$key] }
    }
    foreach ($key in $Overlay.Keys) {
        $incoming = $Overlay[$key]
        $existing = $result.Contains($key) ? $result[$key] : $null
        $bothMaps = ($incoming -is [hashtable] -or $incoming -is [System.Collections.Specialized.OrderedDictionary]) -and
            ($existing -is [hashtable] -or $existing -is [System.Collections.Specialized.OrderedDictionary])
        $result[$key] = $bothMaps ? (Merge-ConfigurationTree -Base $existing -Overlay $incoming) : $incoming
    }
    return $result
}

Export-ModuleMember -Function @(
    'Test-ParallelProductionCutoverDefinition'
    'Get-ParallelCutoverWorkType'
    'Get-ParallelCutoverInstallRefusal'
    'Get-ParallelCutoverMvpRefusal'
    'Get-ParallelCutoverContractVersion'
    'Get-ParallelInstanceAllowedKey'
    'Get-ParallelInstanceFleetRowKey'
    'Get-ParallelInstanceFootprint'
    'Get-ParallelInstanceLayout'
    'Test-ParallelInstanceOwnedPath'
    'Get-ParallelInstanceDeleteRefusal'
    'Test-ParallelInstanceReparsePoint'
    'Remove-ParallelInstanceDirectory'
    'Wait-ParallelInstanceProcessExit'
    'Test-ParallelInstanceDeploymentConfigPath'
    'Remove-ParallelInstanceDeploymentConfig'
    'Invoke-ParallelRemovalSequence'
    'Get-ParallelInstanceName'
    'Test-ParallelInstancePathIsProduction'
    'Get-ParallelInstanceAllowedVehicle'
    'Read-ParallelInstanceDefinition'
    'Test-ParallelInstanceDefinition'
    'Assert-ParallelInstanceDefinition'
    'New-ParallelInstanceConfigurationOverlay'
    'Format-ParallelMesIngestAudit'
    'Get-ParallelMesIngestSecretRefusal'
    'Get-ParallelRetiredFakeMesIngest'
    'Find-ParallelEffectiveConfiguration'
    'Get-ParallelEffectiveConfigurationRefusal'
    'Get-ParallelEffectiveConfigurationAction'
    'Get-ParallelMapStationCatalogApproval'
    'Format-ParallelMapStationCatalogReadBack'
    'Format-ParallelJourneyBindingLine'
    'Get-ParallelHeldRuntimeRefusal'
    'Get-ParallelReadBackAction'
    'Get-ParallelJourneyRuntimeEnvironmentOverride'
    'Format-ParallelLogFileFacts'
    'Get-ParallelLogFilesSince'
    'Read-ParallelLogLines'
    'Merge-ConfigurationTree'
    'Test-ParallelAreaAssignmentZone'
    'Get-ParallelServiceEnvironmentEntry'
    'Set-ParallelServiceEnvironmentEntry'
    'Resolve-ParallelFaultRecoveryCredential'
    'Test-ParallelEmergencyReleaseEnabled'
    'Test-ParallelIdleReturnEnabled'
    'Test-ParallelRecoveryGrantRequired'
    'Get-ParallelRecoveryProofSlot'
    'Resolve-ParallelRecoveryProof'
    'Resolve-ParallelEmergencyReleaseCredential'
    'Format-ParallelEmergencyReleaseLine'
    'Get-ParallelRosterRoleHolderCount'
    'Get-ParallelOnboardSafetyCredentialSlot'
    'Get-ParallelRollbackTargetRefusal'
    'Resolve-ParallelOnboardSafetyCredential'
    'Get-ParallelClearanceExitReadiness'
    'Get-ParallelUpgradeRefusal'
    'Get-ParallelPreInstallRefusal'
    'Resolve-ParallelInstanceDatabasePath'
    'Test-ParallelOrderIntentNeverSent'
    'Get-ParallelDispatchGateRefusal'
    'ConvertTo-ParallelGateDirection'
    'Test-ParallelJourneyAwaitsRecovery'
    'ConvertTo-ParallelRecoveryJourneyArgument'
    'ConvertFrom-ParallelRecoveryJourneyArgument'
    'Get-ParallelRecoveryJourneyNotInFlight'
    'Get-ParallelRiotVehicleMotionReason'
    'Get-ParallelRiotOrderEndReason'
    'Resolve-ParallelRiotGateConnection'
    'Get-ParallelGateSentIntent'
    'Get-ParallelGateVehicleMotion'
)
