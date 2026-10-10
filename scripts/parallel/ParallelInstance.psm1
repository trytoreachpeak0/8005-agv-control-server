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
    pins allowedWorkTypes to exactly the one type, and the shipped six -- WIRE_TO_GATE among them --
    are refused by name. Hard-coded on purpose, like the vehicle whitelist: a rule read out of the
    file being checked is edited in the same keystroke as the file.
#>
$script:MesIngestSources = @('fake', 'production')
$script:ProductionMesIngestBaseUrl = 'http://127.0.0.1:5088'
$script:ProductionSourceWorkTypes = @('STAGING_TO_WIRE')
$script:MvpWorkType = 'WIRE_TO_GATE'

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
        'journeyRuntime', 'vehicleFaultRecovery', 'fieldOperatorRoles', 'taskTypeStations')
    # control-server#535. source is this deployment's, not a product option: the overlay does not write it.
    'mesIngest' = @('baseUrl', 'source')
    'fakeMesIngest' = @('installRoot', 'port', 'taskName', 'seedPath')
    'routeGraph' = @('enabled', 'mapId', 'designStateTtl', 'runtimeRefreshPeriod', 'runtimeStateMaxAge')
    'riotCreateDispatch' = @('enabled')
    'riotForeignOrderCancel' = @('enabled')
    'journeyRuntime' = @('enabled', 'pollInterval', 'agvId', 'vehicleKey', 'agvLifecycleGeneration', 'fleet',
        'mapId', 'mapIdentity', 'dispatchZone', 'dispatchGeneration',
        'maximumEvidenceAge', 'departureSafetyResultWait', 'stationDepartureWaitTimeout',
        'cargoHoldingTimeout', 'sublotBoxCountPath', 'allowedWorkTypes', 'allowedDispatchZones',
        'admissionPolicyVersion', 'admissionPolicyDeploymentId', 'checkpointWaitBudget',
        'areaEndAdmissionRevokedTimeout')
    # control-server#454. credentialEnvironmentVariable is deliberately absent: the overlay writes
    # this instance's own variable name, so no definition can point the service at the MVP's.
    'vehicleFaultRecovery' = @('enabled')
    'fieldOperatorRoles' = @('path', 'onboardClearanceEntryDeclared')
    # control-server#518. TaskTypeStationPreset.SettingsFileKey; there is no options class to mirror.
    'taskTypeStations' = @('settingsFile')
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

# Names only this instance uses, shared by the installer and the uninstaller so the two cannot
# drift apart. The certificate variable matters most: Update-ControlServerLocal.ps1 deletes the
# machine-scope variable it is pointed at, and the default name is the MVP's.
$script:CertificatePasswordVariable = 'CONTROL_SERVER_V2_ONBOARD_CERTIFICATE_PASSWORD'
$script:ProductionCertificatePasswordVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
$script:FirewallRuleFormat = '8005 AGV ControlServer V2 {0}'
# control-server#454. Its own name, not the product default CONTROL_SERVER_FAULT_RECOVERY_CREDENTIAL,
# as for every variable the two instances could otherwise share (coordinator, 2026-10-03).
$script:FaultRecoveryCredentialVariable = 'CONTROL_SERVER_V2_FAULT_RECOVERY_CREDENTIAL'

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
    #>
    param([hashtable] $Definition)

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
        if (@($listed | Where-Object { $_ -is [string] -and $_.Trim() -ieq $script:MvpWorkType }).Count -gt 0) {
            $failures += "journeyRuntime.allowedWorkTypes $shown includes $script:MvpWorkType, which the MVP takes from the same catalog: with mesIngest.source 'production' both instances read one MesIngest with no claim, so this instance would compete with the MVP for the same material. It must be exactly [""STAGING_TO_WIRE""] (user decision 2026-10-09)."
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
            and nothing else: a taskTypeStations that is written is checked as always. Never for
            installing, rolling back or opening the gate (PR #523 review, item 1): those start
            something, and a definition without the preset must not.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)] $Definition,
        [switch] $AllowRiotCreateDispatch,
        [switch] $AllowRiotForeignOrderCancel,
        [switch] $ForStopDirection
    )

    [string[]] $failures = @()
    if ($Definition -isnot [System.Collections.IDictionary]) {
        return @('The instance definition is not a JSON object.')
    }

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
    if ($null -ne $mesIngest -and $null -eq $mesSource) {
        $failures += "mesIngest.source must be 'fake' or 'production' (exactly, lower case), got '$($mesIngest['source'])'; leave it out for 'fake'."
    } elseif ($null -ne $mesIngest -and $mesSource -ceq 'production') {
        $failures += @(Test-ProductionMesIngestSource -Definition $Definition)
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

        # @() for the same reason as in Assert-: a function returning an empty array returns
        # nothing, and += against an unwrapped result is where a stray $null element comes from.
        $failures += @(Test-VehicleIdentity -Journey $journey)
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
    param([Parameter(Mandatory = $true)][hashtable] $Journey)

    # control-server#571: a roster stands instead of the single-vehicle keys. Ordinal 'fleet' only: a case
    # variant ('Fleet') is refused by the key whitelist, and so is the whole definition.
    if ($Journey.ContainsKey('fleet')) {
        return @(Test-FleetIdentity -Journey $Journey)
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
        return @("journeyRuntime names agv01 ('$agvId' / '$vehicleKey'), the vehicle the MVP service is driving in production. The parallel instance may only drive agv02 or agv03.")
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
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory = $true)][hashtable] $Journey)

    $fleet = $Journey['fleet']

    # ---- 1. agv01 ----
    $hits = [ordered]@{}
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
    if ($hits.Count -gt 0) {
        return @($hits.Keys | ForEach-Object {
                "$_ names agv01 ($($hits[$_] -join ', ')), the vehicle the MVP service is driving in production. The parallel instance may only drive agv02 or agv03; the whole definition is refused."
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
        $match = $script:AllowedVehicles | Where-Object { [string]::Equals($_.VehicleKey, $vehicleKey, [StringComparison]::Ordinal) } | Select-Object -First 1
        if ($null -eq $match) {
            $allowed = ($script:AllowedVehicles | ForEach-Object { "$($_.Alias)=$($_.VehicleKey)" }) -join ', '
            $failures += "$prefix.vehicleKey '$vehicleKey' is not a spare vehicle. Allowed: $allowed."
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
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string] $Path)
    # The link check comes first only so the self-test can reach it: a test can make a junction in
    # a temporary directory, but not at a path the allowlist accepts, so with the string checks
    # first this branch would be unreachable from any test and its removal would go unnoticed.
    # Both refuse; the order changes which reason is given, not whether the delete happens.
    if (Test-ParallelInstanceReparsePoint -Path $Path) {
        throw "Refusing to delete '$Path': it is a junction or symbolic link, not a directory this instance created."
    }
    $refusal = Get-ParallelInstanceDeleteRefusal -Path $Path
    if ($refusal) { throw "Refusing to delete '$Path': $refusal." }
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
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
        [switch] $ForStopDirection
    )

    # @() around the call, not just the [string[]] cast: PowerShell unwraps an empty array to
    # $null on return, and [string[]] $null is $null rather than an empty array -- so under
    # StrictMode the .Count below threw on exactly the input this function is supposed to
    # accept. The self-test only exercised Test-, which its own callers already wrapped.
    [string[]] $failures = @(Test-ParallelInstanceDefinition -Definition $Definition -AllowRiotCreateDispatch:$AllowRiotCreateDispatch `
            -AllowRiotForeignOrderCancel:$AllowRiotForeignOrderCancel -ForStopDirection:$ForStopDirection)
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
    #>
    [CmdletBinding()]
    param(
        [AllowNull()] $Previous,
        [Parameter(Mandatory = $true)][hashtable] $Current
    )
    if ($null -eq $Previous) { return $null }
    if ((Get-MesIngestSource -Definition $Current) -cne 'production') { return $null }
    if ($Previous -isnot [hashtable]) { throw 'The previously installed definition is not a JSON object; its FakeMesIngest cannot be retired.' }
    [string[]] $failures = @(Test-ParallelInstanceDefinition -Definition $Previous -AllowRiotCreateDispatch -AllowRiotForeignOrderCancel -ForStopDirection)
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
            the same as an empty roster. That -- not the definition, not the
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
    if ((Get-MesIngestSource -Definition $Definition) -ceq 'production' -and
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
    if ($problems.Count -eq 0) { return $null }
    return "EFFECTIVE_CONFIGURATION_MISMATCH: $($problems -join '; ')."
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
            to warn about, so that, too, stops the service.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable] $Definition,
        [AllowNull()] $Effective
    )
    $message = Get-ParallelEffectiveConfigurationRefusal -Definition $Definition -Effective $Effective
    $action = if ($null -eq $message) { 'Pass' }
        elseif ($null -eq $Effective -and (Get-MesIngestSource -Definition $Definition) -cne 'production' -and
            -not $Definition['journeyRuntime'].ContainsKey('fleet')) { 'Warn' }
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

    $routeGraphOverlay = [ordered]@{}
    foreach ($key in $routeGraph.Keys) { $routeGraphOverlay[$key] = $routeGraph[$key] }

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
        FieldOperatorRoles = [ordered]@{
            path = $Definition['fieldOperatorRoles']['path']
            onboardClearanceEntryDeclared = $Definition['fieldOperatorRoles']['onboardClearanceEntryDeclared']
        }
        # control-server#518. Read only while the runtime is on, so written on every install whether it is or not.
        TaskTypeStations = [ordered]@{ settingsFile = $Definition['taskTypeStations']['settingsFile'] }
    }
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
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowEmptyCollection()][AllowNull()][string[]] $ServiceEnvironment,
        [AllowEmptyCollection()][AllowNull()][string[]] $MachineEnvironment
    )
    $pattern = '^(?i:(?:DOTNET_|ASPNETCORE_)?JourneyRuntime(?:__|:))'
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
    $recovery = $Definition.Contains('vehicleFaultRecovery') ? $Definition['vehicleFaultRecovery'] : $null
    if ($recovery -is [System.Collections.IDictionary] -and $recovery['enabled'] -eq $true) {
        throw ("vehicleFaultRecovery.enabled is true, but there is no value for $($script:FaultRecoveryCredentialVariable): " +
            "deploy-config.json carries no faultRecoveryCredential and the service's Environment holds none. " +
            'The service would refuse to start without it. Nothing was stopped or changed.')
    }
    return $null
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

function ConvertTo-ParallelGateDirection {
    <#
        .SYNOPSIS
            The gate change -State asks for: Closed is Close (RiotCreateDispatch.enabled false), Open is Open (true).

        .DESCRIPTION
            control-server#472 review S2. One place, pinned by Test-ParallelInstance.ps1: a swapped mapping makes
            "-State Closed" a no-op on an open gate that still reports success. Pure.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory = $true)][ValidateSet('Closed', 'Open')][string] $State)
    return $State -ceq 'Closed' ? 'Close' : 'Open'
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

              * Close: any journey whose Stage is not Completed (the engine's own definition of active,
                JourneyRuntimeEngine; Blocked counts). No HTTP endpoint lists them all -- each
                /api/dashboard/* query returns a subset -- which is why this reads the database.
              * Open: a journey whose order has been, or may have been, sent. With the gate closed no
                order can be created (MovementDispatchService returns CreateDispatchDisabled and writes
                nothing), so the journeys that exist then are waiting for exactly this gate, and refusing
                them would be a deadlock only a database edit could break. Refused is any journey with an
                order intent that is not certainly never sent (Test-ParallelOrderIntentNeverSent). A
                journey's intents are those named on its row (PickupUpperId, GateUpperId) plus every
                intent for its vehicle created at or after the journey was: the engine keeps at most one
                active journey per vehicle, and later stops, rebuilds and charging legs get intents of
                their own that the row does not name. Anything that cannot be dated or read refuses.

            $State is Get-ParallelJourneyDispatchState's output, or $null / an Error string when it could
            not be read, which refuses in both directions (fail closed). Pure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][ValidateSet('Close', 'Open')][string] $Direction,
        [AllowNull()] $State,
        [Parameter(Mandatory = $true)][string] $ServiceName,
        [Parameter(Mandatory = $true)][string] $DatabasePath
    )
    $nothing = ' Nothing was stopped or changed.'
    if ($null -eq $State -or $State -isnot [System.Collections.IDictionary] -or $State.Contains('Error')) {
        $why = ($State -is [System.Collections.IDictionary] -and $State.Contains('Error')) ? $State['Error'] : 'no state was returned'
        return ("GATE_STATE_UNREADABLE: the journey state of '$ServiceName' could not be read from $DatabasePath ($why), so " +
            'whether a vehicle is under way cannot be told.' + $nothing)
    }
    $journeys = @($State['Journeys'] | Where-Object { [string] $_['Stage'] -cne 'Completed' })
    if ($Direction -eq 'Close') {
        if ($journeys.Count -eq 0) { return $null }
        $listed = ($journeys | ForEach-Object { "$($_['JourneyId']) ($($_['AgvId']), $($_['Stage']))" }) -join '; '
        return ("GATE_CLOSE_REFUSED_IN_FLIGHT: $DatabasePath has $($journeys.Count) journey(s) of '$ServiceName' not Completed: " +
            "$listed. Stop injecting demand into its FakeMesIngest, wait until agv02 and agv03 have finished, and run this again." + $nothing)
    }

    $intents = @($State['OrderIntents'])
    $sent = [System.Collections.Generic.List[string]]::new()
    foreach ($journey in $journeys) {
        [DateTimeOffset] $since = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string] $journey['CreatedAt'], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $since)) {
            $sent.Add("$($journey['JourneyId']): its CreatedAt '$($journey['CreatedAt'])' cannot be read")
            continue
        }
        $named = @([string] $journey['PickupUpperId'], [string] $journey['GateUpperId']) | Where-Object { $_ }
        foreach ($intent in $intents) {
            $mine = $named -ccontains [string] $intent['UpperId']
            if (-not $mine -and [string] $intent['VehicleKey'] -ceq [string] $journey['VehicleKey']) {
                [DateTimeOffset] $created = [DateTimeOffset]::MinValue
                if (-not [DateTimeOffset]::TryParse([string] $intent['CreatedAt'], [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref] $created)) {
                    $sent.Add("$($journey['JourneyId']): order intent $($intent['UpperId']) has an unreadable CreatedAt '$($intent['CreatedAt'])'")
                    continue
                }
                $mine = $created -ge $since
            }
            if ($mine -and -not (Test-ParallelOrderIntentNeverSent -Intent $intent -AuditEvents @($State['AuditEvents']))) {
                $sent.Add("$($journey['JourneyId']) ($($journey['AgvId']), $($journey['Stage'])): order intent $($intent['UpperId']) is $($intent['Status'])" +
                    $(if ($intent['OrderId']) { " with RIoT order $($intent['OrderId'])" } else { '' }))
            }
        }
    }
    if ($sent.Count -eq 0) { return $null }
    return ("GATE_OPEN_REFUSED_ORDER_SENT: in $DatabasePath, a journey of '$ServiceName' has an order that was or may have been sent " +
        "to RIoT, so a vehicle may be under way and restarting the service would stop its fault supervision: $($sent -join '; '). " +
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
    'Get-ParallelInstanceAllowedKey'
    'Get-ParallelInstanceFleetRowKey'
    'Get-ParallelInstanceFootprint'
    'Get-ParallelInstanceLayout'
    'Test-ParallelInstanceOwnedPath'
    'Get-ParallelInstanceDeleteRefusal'
    'Test-ParallelInstanceReparsePoint'
    'Remove-ParallelInstanceDirectory'
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
    'Get-ParallelHeldRuntimeRefusal'
    'Get-ParallelReadBackAction'
    'Get-ParallelJourneyRuntimeEnvironmentOverride'
    'Format-ParallelLogFileFacts'
    'Merge-ConfigurationTree'
    'Get-ParallelServiceEnvironmentEntry'
    'Set-ParallelServiceEnvironmentEntry'
    'Resolve-ParallelFaultRecoveryCredential'
    'Get-ParallelClearanceExitReadiness'
    'Get-ParallelUpgradeRefusal'
    'Get-ParallelPreInstallRefusal'
    'Resolve-ParallelInstanceDatabasePath'
    'Test-ParallelOrderIntentNeverSent'
    'Get-ParallelDispatchGateRefusal'
    'ConvertTo-ParallelGateDirection'
)
