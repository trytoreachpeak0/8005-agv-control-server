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
    The two spare vehicles, as pairs. Hard-coded on purpose: a whitelist read out of the file
    being checked is not a whitelist, because whoever edits the vehicle edits the list in the
    same keystroke.

    Source: remote-ops/fleet.md, itself read from RIoT GET /api/device/v1/devices on
    2026-09-03. deviceKey is the durable coordinate -- deviceName is human-editable and RIoT
    does not enforce uniqueness on it -- so the key is what anchors each row, and the name
    must agree with the key rather than being trusted on its own.
#>
$script:AllowedVehicles = @(
    [pscustomobject]@{ Alias = 'agv02'; AgvId = '老厂前线新多仓位2'; VehicleKey = 'BROKERX-f38975561adf46ccb1d2f23833c7d0e4' }
    [pscustomobject]@{ Alias = 'agv03'; AgvId = '老厂前线新多仓位3'; VehicleKey = 'BROKERX-7daca4ee91da498d8026c68b7b941127' }
)

# Named separately from "not in the allowed list" so that pointing the parallel instance at
# the production vehicle produces its own message rather than a generic one. agv01 is what
# the MVP service is driving right now; the shipped appsettings.json still carries its
# identity, so inheriting the default is exactly how this goes wrong.
$script:ProductionVehicle = [pscustomobject]@{
    Alias = 'agv01'
    AgvId = '老厂前线新多仓位1'
    VehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
}

<#
    The keys each section may carry, spelled exactly. Anything else is refused.

    Why a whitelist rather than reading the keys this module knows about: ConvertFrom-Json
    -AsHashtable is case-sensitive, and .NET configuration is not. A definition carrying both
    "agvId" and "AgvId" -- or a "Fleet" roster -- passes every check that reads keys by name,
    is copied verbatim into the overlay, and is then bound by a configuration system that
    treats the two spellings as one setting. Checking by name can only see the keys it thought
    of; refusing the ones it did not is what closes the rest.

    journeyRuntime mirrors JourneyRuntimeOptions minus Fleet. Fleet is excluded on purpose: it
    is a second vehicle list, the options validator only requires it to *contain* the primary
    pair, and the pair whitelist below would never look at it. Test-ParallelInstance.ps1
    asserts these names against the C# properties, so a rename there fails here instead of
    binding to nothing.
#>
$script:AllowedKeys = [ordered]@{
    '' = @('instanceId', 'serviceName', 'installRoot', 'dataRoot', 'backupRoot', 'packageRoot',
        'opsRoot', 'stagingRoot', 'listenAddress', 'healthBindAddress', 'onboardPort', 'healthPort',
        'dashboardPort', 'mesIngest', 'fakeMesIngest', 'routeGraph', 'riotCreateDispatch', 'riotForeignOrderCancel',
        'journeyRuntime')
    'mesIngest' = @('baseUrl')
    'fakeMesIngest' = @('installRoot', 'port', 'taskName', 'seedPath')
    'routeGraph' = @('enabled', 'mapId', 'designStateTtl', 'runtimeRefreshPeriod', 'runtimeStateMaxAge')
    'riotCreateDispatch' = @('enabled')
    'riotForeignOrderCancel' = @('enabled')
    'journeyRuntime' = @('enabled', 'pollInterval', 'agvId', 'vehicleKey', 'agvLifecycleGeneration',
        'mapId', 'mapIdentity', 'dispatchZone', 'dispatchGeneration', 'minimumBatteryPercent',
        'maximumEvidenceAge', 'departureSafetyResultWait', 'stationDepartureWaitTimeout',
        'cargoHoldingTimeout', 'sublotBoxCountPath', 'allowedWorkTypes', 'allowedDispatchZones',
        'admissionPolicyVersion', 'admissionPolicyDeploymentId', 'checkpointWaitBudget',
        'areaEndAdmissionRevokedTimeout')
}

# Names only this instance uses, shared by the installer and the uninstaller so the two cannot
# drift apart. The certificate variable matters most: Update-ControlServerLocal.ps1 deletes the
# machine-scope variable it is pointed at, and the default name is the MVP's.
$script:CertificatePasswordVariable = 'CONTROL_SERVER_V2_ONBOARD_CERTIFICATE_PASSWORD'
$script:ProductionCertificatePasswordVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
$script:FirewallRuleFormat = '8005 AGV ControlServer V2 {0}'

function Get-ParallelInstanceAllowedKey {
    <#
        .SYNOPSIS
            The per-section key whitelist; '' is the top level.
    #>
    [CmdletBinding()]
    param()
    return $script:AllowedKeys
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
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)] $Definition,
        [switch] $AllowRiotCreateDispatch,
        [switch] $AllowRiotForeignOrderCancel
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
    # 127.0.0.1:5088; v2 must read an injected catalog for the whole parallel period, because
    # a demand claimed by one instance is gone from the other's point of view.
    $mesIngest = Get-Node -Root $Definition -Key 'mesIngest'
    if ($null -eq $mesIngest) {
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

    $fake = Get-Node -Root $Definition -Key 'fakeMesIngest'
    if ($null -eq $fake) {
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
        # as "fill me in" is refused, not guessed. The shipped definition carries these for the
        # two map-26 values that have no source yet (see scripts/parallel/README.md).
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
        Every key in the section that is not on the whitelist, spelled exactly. Three messages,
        because the three ways to get here need three different fixes: a roster (remove it --
        the parallel instance drives one vehicle), a case variant of a known key (delete the
        duplicate), and anything else (a typo, or a new option this module has not been taught).
    #>
    param([System.Collections.IDictionary] $Node, [string] $Section)

    [string[]] $failures = @()
    $allowed = $script:AllowedKeys[$Section]
    $prefix = $Section -eq '' ? '' : "$Section."
    foreach ($key in @($Node.Keys)) {
        # Ordinal: -ccontains is a culture comparison that skips zero-width characters, and would
        # accept 'enabled<U+200B>' as 'enabled' while .NET configuration binds it as a different key
        # (see the allowlist note on ConvertTo-MapComparisonKey; evidence review3-string-equality.txt).
        if (@($allowed | Where-Object { [string]::Equals($_, $key, [StringComparison]::Ordinal) }).Count -gt 0) { continue }
        if ($Section -eq 'journeyRuntime' -and $key -ieq 'fleet') {
            $failures += "journeyRuntime.$key is a vehicle roster. The parallel instance drives one vehicle, and the agv02/agv03 pair check never looks inside a roster -- an agv01 entry there would pass."
            continue
        }
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

            deploy-config.json carries the RIoT call API key and the MesIngest shared secret in
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
    $fake = $Definition['fakeMesIngest']
    $packageLeaf = Split-Path -Leaf $packageRoot
    # String concatenation, not Join-Path: Join-Path resolves the drive and fails on a machine
    # without one (the control host has no D:), and this function must stay free of the file system.
    return [pscustomobject]@{
        ServiceName = [string] $Definition['serviceName']
        TaskName = [string] $fake['taskName']
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
        FakeLogPath = "$opsRoot\logs\fake-mes-ingest.log"
        InstalledDefinitionPath = "$opsRoot\installed-instance.json"
        # Where the control host copies the secrets file (19-deploy-control-server-parallel.ps1
        # writes "$opsRoot\deploy-config.json"). The installer accepts no other path and deletes
        # only this one.
        DeploymentConfigPath = "$opsRoot\deploy-config.json"
        FakeInstallRoot = [string] $fake['installRoot']
        SeedPath = [string] $fake['seedPath']
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
    $items = @(
        [pscustomobject]@{ Kind = 'Service'; Name = $layout.ServiceName; Data = $false }
        [pscustomobject]@{ Kind = 'ScheduledTask'; Name = $layout.TaskName; Data = $false }
    )
    foreach ($rule in $layout.FirewallRules) {
        $items += [pscustomobject]@{ Kind = 'FirewallRule'; Name = $rule; Data = $false }
    }
    $items += @(
        [pscustomobject]@{ Kind = 'MachineEnvironment'; Name = $layout.CertificatePasswordVariable; Data = $false }
        [pscustomobject]@{ Kind = 'Directory'; Name = $layout.FakeInstallRoot; Data = $false }
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
        [switch] $AllowRiotForeignOrderCancel
    )

    # @() around the call, not just the [string[]] cast: PowerShell unwraps an empty array to
    # $null on return, and [string[]] $null is $null rather than an empty array -- so under
    # StrictMode the .Count below threw on exactly the input this function is supposed to
    # accept. The self-test only exercised Test-, which its own callers already wrapped.
    [string[]] $failures = @(Test-ParallelInstanceDefinition -Definition $Definition -AllowRiotCreateDispatch:$AllowRiotCreateDispatch `
            -AllowRiotForeignOrderCancel:$AllowRiotForeignOrderCancel)
    if ($failures.Count -gt 0) {
        $listed = ($failures | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
        throw ("The parallel instance definition was refused ($($failures.Count) reason(s)):" +
            [Environment]::NewLine + $listed)
    }
    return $Definition
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
    foreach ($key in $journey.Keys) { $journeyOverlay[$key] = $journey[$key] }

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
    }
}

function Merge-ConfigurationTree {
    <#
        .SYNOPSIS
            Recursive per-key merge of Overlay onto Base, Overlay winning.

        .DESCRIPTION
            Arrays are replaced wholesale rather than concatenated: allowedWorkTypes is a
            closed list, and an append would silently widen it on every redeployment.
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
    'Merge-ConfigurationTree'
)
