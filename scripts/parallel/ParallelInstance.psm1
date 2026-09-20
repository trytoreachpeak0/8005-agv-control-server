#Requires -Version 7

<#
    The v2 parallel instance definition and the checks that refuse a bad one.

    control-server#262. From roughly 2026-10-08 a second ControlServer runs on factory01
    beside the MVP service, driving agv02/agv03 while the MVP keeps driving agv01. Four
    resources are shared between the two (MES demand, ports and database, RIoT, the map),
    and every check below exists because one of them can be taken by accident.

    Pure functions only: no file system, no network, no registry. Everything here is
    decidable from the definition text alone, which is what lets Test-ParallelInstance.ps1
    assert that a given corruption produces exactly one named failure. The scripts that do
    touch a machine call Assert-ParallelInstanceDefinition first and then stop reasoning
    about identity.
#>

Set-StrictMode -Version 3.0

# The production installation, named here so a parallel definition can be refused for
# colliding with it. These are the defaults of Install-ControlServerLocal.ps1 and the values
# remote-ops/factory-server/docs/wire-to-gate-cd.md section 5 records.
$script:ProductionServiceName = '8005 AGV ControlServer'
$script:ProductionPaths = @(
    'C:\Program Files\8005 AGV\ControlServer'
    'C:\Program Files\8005 AGV\ControlServer.Dashboard'
    'C:\ProgramData\8005\ControlServer'
    'C:\ProgramData\8005\ControlServer-backups'
    'D:\zhengyushao\ControlServer'
    'D:\zhengyushao\control-server-ops'
    'D:\zhengyushao\control-server-staging'
)
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

function Test-NormalizedPath {
    param([string] $Path)
    return $Path.TrimEnd('\', '/').ToLowerInvariant()
}

function Test-PathCollision {
    <#
        Equal, or nested either way. A parallel data root placed *inside* the production data
        root shares the production directory's fate on an uninstall, and one placed *around*
        it hands the parallel instance's ACL tightening the production files.
    #>
    param([string] $Candidate, [string] $Production)
    $a = Test-NormalizedPath $Candidate
    $b = Test-NormalizedPath $Production
    if ($a -eq $b) { return $true }
    return $a.StartsWith("$b\", [StringComparison]::Ordinal) -or $b.StartsWith("$a\", [StringComparison]::Ordinal)
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
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)] $Definition,
        [switch] $AllowRiotCreateDispatch
    )

    [string[]] $failures = @()
    if ($Definition -isnot [hashtable]) {
        return @('The instance definition is not a JSON object.')
    }

    # ---------------------------------------------------------------- identity ---

    foreach ($key in @('instanceId', 'serviceName')) {
        if (-not (Test-KeyPresent -Node $Definition -Key $key) -or
            [string]::IsNullOrWhiteSpace([string] $Definition[$key])) {
            $failures += "$key must be a non-empty string."
        }
    }

    if ((Test-KeyPresent -Node $Definition -Key 'serviceName') -and
        ([string] $Definition['serviceName']).Trim() -eq $script:ProductionServiceName) {
        $failures += "serviceName is the production service '$script:ProductionServiceName'; the parallel instance must not install over the MVP service."
    }

    # ------------------------------------------------------------------- paths ---

    $pathKeys = @('installRoot', 'dataRoot', 'backupRoot', 'packageRoot', 'opsRoot', 'stagingRoot')
    foreach ($key in $pathKeys) {
        if (-not (Test-KeyPresent -Node $Definition -Key $key) -or
            [string]::IsNullOrWhiteSpace([string] $Definition[$key])) {
            $failures += "$key must be a non-empty path."
            continue
        }
        $candidate = [string] $Definition[$key]
        foreach ($production in $script:ProductionPaths) {
            if (Test-PathCollision -Candidate $candidate -Production $production) {
                $failures += "$key ('$candidate') collides with the production path '$production'."
            }
        }
    }

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
        foreach ($key in @('installRoot', 'taskName', 'seedPath')) {
            if (-not (Test-KeyPresent -Node $fake -Key $key) -or
                [string]::IsNullOrWhiteSpace([string] $fake[$key])) {
                $failures += "fakeMesIngest.$key must be a non-empty string."
            }
        }
        if ((Test-KeyPresent -Node $fake -Key 'installRoot')) {
            foreach ($production in $script:ProductionPaths) {
                if (Test-PathCollision -Candidate ([string] $fake['installRoot']) -Production $production) {
                    $failures += "fakeMesIngest.installRoot ('$($fake['installRoot'])') collides with the production path '$production'."
                }
            }
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

    $match = $script:AllowedVehicles | Where-Object { $_.VehicleKey -eq $vehicleKey } | Select-Object -First 1
    if ($null -eq $match) {
        $allowed = ($script:AllowedVehicles | ForEach-Object { "$($_.Alias)=$($_.VehicleKey)" }) -join ', '
        return @("journeyRuntime.vehicleKey '$vehicleKey' is not a spare vehicle. Allowed: $allowed.")
    }
    if ($agvId -cne $match.AgvId) {
        return @("journeyRuntime.agvId '$agvId' does not match the RIoT deviceName of $($match.Alias), which is '$($match.AgvId)'. The name and the key must describe the same car.")
    }

    return @()
}

function Assert-ParallelInstanceDefinition {
    <#
        .SYNOPSIS
            Throws with every failure listed, or returns the definition unchanged.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] $Definition,
        [switch] $AllowRiotCreateDispatch
    )

    # @() around the call, not just the [string[]] cast: PowerShell unwraps an empty array to
    # $null on return, and [string[]] $null is $null rather than an empty array -- so under
    # StrictMode the .Count below threw on exactly the input this function is supposed to
    # accept. The self-test only exercised Test-, which its own callers already wrapped.
    [string[]] $failures = @(Test-ParallelInstanceDefinition -Definition $Definition -AllowRiotCreateDispatch:$AllowRiotCreateDispatch)
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
    'Get-ParallelInstanceAllowedVehicle'
    'Read-ParallelInstanceDefinition'
    'Test-ParallelInstanceDefinition'
    'Assert-ParallelInstanceDefinition'
    'New-ParallelInstanceConfigurationOverlay'
    'Merge-ConfigurationTree'
)
