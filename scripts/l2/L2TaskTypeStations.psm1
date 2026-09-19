#Requires -Version 7

<#
.SYNOPSIS
    The L2 rig's task type station preset (control-server#159), and the "server is expected to refuse to start"
    scenario shape.

.DESCRIPTION
    Since control-server#159 a server whose journey runtime is on loads a controlled preset of task type rules and
    per-map bindings at startup, and refuses to start when it is misconfigured. Every L2 rig runs with the journey
    runtime on, so the orchestrator installs one preset per run: by default the rig's own map with WIRE_TO_GATE bound
    to its gate station, which is exactly what the shipped preset and the rig's JourneyRuntime scalars say. It is
    written into the run's stage directory and named to the server through TaskTypeStations__settingsFile, so the
    run never reads whatever preset happens to sit next to the built host.

    A scenario replaces the preset whole with the setup key `TaskTypeStations`, or installs none with `$false`
    (the server then loads nothing and refuses nothing). A scenario that declares
    `ExpectServerStartupRefusal = '<reason code>'` gets the other shape: the orchestrator waits for the server
    process to exit instead of for it to listen, collects its log, starts no peer, and hands the result to the
    scenario as Context.ServerRefusal.

    Both keys are checked before any process starts: a typo in either would otherwise surface as a server that
    refused, or did not refuse, for a reason nobody asked about.
#>

Set-StrictMode -Version Latest

$script:SixRules = @(
    [ordered]@{ taskType = 'DIE_TO_WIRE_STAGING'; fixedEnd = 'DESTINATION' }
    [ordered]@{ taskType = 'DIE_TO_OVEN'; fixedEnd = 'DESTINATION' }
    [ordered]@{ taskType = 'WIRE_TO_GATE'; fixedEnd = 'DESTINATION' }
    [ordered]@{ taskType = 'WIRE_TO_OPTICAL'; fixedEnd = 'DESTINATION' }
    [ordered]@{ taskType = 'STAGING_TO_WIRE'; fixedEnd = 'ORIGIN' }
    [ordered]@{ taskType = 'WIRE_TO_NITROGEN'; fixedEnd = 'DESTINATION' }
)

# Not a real site verification record: nothing on this rig was verified on site, and the evidence says so.
$script:SyntheticSiteVerification = 'L2-SYNTHETIC-SITE-CHECK'

$script:SettingKeys = @('Rules', 'MapId', 'RequiredTaskTypes', 'Bindings')
$script:RuleKeys = @('TaskType', 'FixedEnd')
$script:BindingKeys = @('TaskType', 'StationRiotId', 'StationName', 'SiteVerificationRef')

<#
Checks the setup file's TaskTypeStations value and returns what the run installs: $null for the default preset,
$false for none, or the scenario's own table. Keys are matched case-sensitively and an unknown one is refused, so
`Binding` or `stationRiotID` fails here rather than quietly installing something else.
#>
function Resolve-L2TaskTypeStationsSetting {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where
    )

    if (-not $Setup.ContainsKey('TaskTypeStations')) { return $null }
    $value = $Setup.TaskTypeStations
    if ($value -is [bool]) {
        if ($value) {
            throw "TaskTypeStations = `$true in $Where means nothing: leave the key out for the default preset, give `$false for none, or give the preset."
        }
        return $false
    }
    if ($value -isnot [hashtable]) {
        throw "TaskTypeStations in $Where must be `$false or a table with RequiredTaskTypes and Bindings (and optionally Rules and MapId)."
    }
    $unknown = @($value.Keys | Where-Object { $_ -cnotin $script:SettingKeys })
    if ($unknown.Count -gt 0) {
        throw "TaskTypeStations in $Where has unknown key(s) $($unknown -join ', '); the keys are $($script:SettingKeys -join ', ')."
    }
    foreach ($required in @('RequiredTaskTypes', 'Bindings')) {
        if (-not $value.ContainsKey($required)) {
            throw "TaskTypeStations in $Where needs $required (give @() for an empty one)."
        }
    }
    if ($value.ContainsKey('MapId') -and $value.MapId -isnot [int]) {
        throw "TaskTypeStations.MapId in $Where must be an integer, not '$($value.MapId)'."
    }
    foreach ($taskType in @($value.RequiredTaskTypes)) {
        if ($taskType -isnot [string] -or [string]::IsNullOrWhiteSpace($taskType)) {
            throw "TaskTypeStations.RequiredTaskTypes in $Where must list task type names."
        }
    }
    if ($value.ContainsKey('Rules')) {
        foreach ($rule in @($value.Rules)) {
            Assert-L2TaskTypeStationEntry -Entry $rule -Keys $script:RuleKeys -What 'Rules' -Where $Where
        }
    }
    foreach ($binding in @($value.Bindings)) {
        Assert-L2TaskTypeStationEntry -Entry $binding -Keys $script:BindingKeys -What 'Bindings' -Where $Where
        if ($binding.StationRiotId -isnot [int]) {
            throw "TaskTypeStations.Bindings in ${Where}: StationRiotId must be an integer, not '$($binding.StationRiotId)'."
        }
    }
    return $value
}

# Exactly the given keys, no more and no fewer. A deliberately bad value (an empty name, an unknown task type) is
# allowed through: refusing it is the server's job, and a scenario may be about exactly that.
function Assert-L2TaskTypeStationEntry {
    param($Entry, [string[]]$Keys, [string]$What, [string]$Where)

    if ($Entry -isnot [hashtable]) {
        throw "Every TaskTypeStations.$What entry in $Where must be a table with $($Keys -join ', ')."
    }
    $unknown = @($Entry.Keys | Where-Object { $_ -cnotin $Keys })
    $missing = @($Keys | Where-Object { -not $Entry.ContainsKey($_) })
    if ($unknown.Count -gt 0 -or $missing.Count -gt 0) {
        throw "A TaskTypeStations.$What entry in $Where has " +
            "$(if ($unknown.Count -gt 0) { "unknown key(s) $($unknown -join ', ')" } else { 'no unknown key' }) and " +
            "$(if ($missing.Count -gt 0) { "misses $($missing -join ', ')" } else { 'misses nothing' }); it needs exactly $($Keys -join ', ')."
    }
}

<#
Checks ExpectServerStartupRefusal and returns the reason code, or $null when the scenario expects the server to
start. Only the synthetic rig: a refusing server has no session for the shipped onboard to open, and the real rig's
desktop lock is not worth taking for a server that never listens.
#>
function Resolve-L2ExpectedStartupRefusal {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where,
        [bool]$RealOnboard
    )

    if (-not $Setup.ContainsKey('ExpectServerStartupRefusal')) { return $null }
    $code = $Setup.ExpectServerStartupRefusal
    if ($code -isnot [string] -or $code -cnotmatch '^[A-Z][A-Z0-9_]*$') {
        throw "ExpectServerStartupRefusal in $Where must be a reason code such as TASK_TYPE_STATION_REUSED, not '$code'."
    }
    if ($RealOnboard) {
        throw "ExpectServerStartupRefusal in $Where needs the synthetic rig: a server that refuses to start has no session for the real onboard to open."
    }
    foreach ($key in @('Dashboard', 'OnboardPeers', 'Fleet', 'ClockSkewMs', 'ProtocolFaultProxy')) {
        if ($Setup.ContainsKey($key)) {
            throw "ExpectServerStartupRefusal in $Where cannot be combined with ${key}: nothing that needs a running server is started."
        }
    }
    return $code
}

<#
Writes the preset this run installs and returns its path, or writes a file with no TaskTypeStations section when
the setting is $false -- so the server named to it loads nothing, rather than falling back to the preset next to
the built host.
#>
function New-L2TaskTypeStationPreset {
    param(
        [AllowNull()][object]$Setting,
        [Parameter(Mandatory)][int]$MapId,
        [Parameter(Mandatory)][string]$GateStationId,
        [Parameter(Mandatory)][int]$GateStationRiotId,
        [Parameter(Mandatory)][string]$Path
    )

    if ($Setting -is [bool]) {
        $document = [ordered]@{}
    } else {
        # Each list is built into a variable with a plain loop rather than out of an if-expression: an expression's
        # output goes through the pipeline, which turns an empty array into $null and a one-element one into its
        # element -- and a scenario's @() is exactly the empty case.
        $rules = [System.Collections.Generic.List[object]]::new()
        $required = [System.Collections.Generic.List[string]]::new()
        $bindings = [System.Collections.Generic.List[object]]::new()
        if ($null -eq $Setting) {
            foreach ($rule in $script:SixRules) { $rules.Add($rule) }
            $required.Add('WIRE_TO_GATE')
            $bindings.Add([ordered]@{
                    taskType            = 'WIRE_TO_GATE'
                    stationRiotId       = $GateStationRiotId
                    stationName         = $GateStationId
                    siteVerificationRef = $script:SyntheticSiteVerification
                })
        } else {
            if ($Setting.ContainsKey('Rules')) {
                foreach ($rule in @($Setting.Rules)) { $rules.Add([ordered]@{ taskType = $rule.TaskType; fixedEnd = $rule.FixedEnd }) }
            } else {
                foreach ($rule in $script:SixRules) { $rules.Add($rule) }
            }
            foreach ($taskType in @($Setting.RequiredTaskTypes)) { $required.Add($taskType) }
            foreach ($binding in @($Setting.Bindings)) {
                $bindings.Add([ordered]@{
                        taskType            = $binding.TaskType
                        stationRiotId       = $binding.StationRiotId
                        stationName         = $binding.StationName
                        siteVerificationRef = $binding.SiteVerificationRef
                    })
            }
        }
        $document = [ordered]@{
            TaskTypeStations = [ordered]@{
                rules             = $rules.ToArray()
                mapId             = ($null -ne $Setting -and $Setting.ContainsKey('MapId')) ? $Setting.MapId : $MapId
                requiredTaskTypes = $required.ToArray()
                bindings          = $bindings.ToArray()
            }
        }
    }
    [IO.File]::WriteAllText($Path, ($document | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    return $Path
}

<#
Waits for a server that is expected to refuse to start: until its process exits, polling /health/live the whole
time. Returns the exit code, whether /health/live ever answered, and the server's stdout and stderr together.

Throws when the server is still running at the deadline -- a server that did not refuse is a failed scenario, and
the orchestrator's catch records it as such.
#>
function Wait-L2ServerStartupRefusal {
    param(
        [Parameter(Mandatory)][object]$Server,
        [Parameter(Mandatory)][int]$HealthPort,
        [int]$TimeoutSeconds = 120
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $everLive = $false
    while (-not $Server.Process.HasExited) {
        try {
            $status = (Invoke-RestMethod -Uri "http://127.0.0.1:$HealthPort/health/live" -TimeoutSec 2).status
            if ($status -eq 'live') { $everLive = $true }
        } catch {
            # Not listening, which is what a refusing server looks like.
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "The server was expected to refuse to start but was still running after ${TimeoutSeconds}s " +
                "(/health/live answered: $everLive)."
        }
        Start-Sleep -Milliseconds 250
    }
    $Server.Process.WaitForExit()
    $decoded = @($Server.OutLog, $Server.ErrLog | Where-Object { Test-Path -LiteralPath $_ } |
            ForEach-Object { Read-L2ProcessLog -Path $_ })
    return [pscustomobject]@{
        ExitCode = $Server.Process.ExitCode
        EverLive = $everLive
        Log      = ($decoded.Text -join "`n")
        Encoding = ($decoded.Encoding | Sort-Object -Unique) -join ', '
    }
}

<#
A redirected .NET console writes in the console's code page, not UTF-8: on a zh-CN Windows -- this machine and
win11-01 both -- the server's 关卡 lands in its log as GBK bytes. Strict UTF-8 first, so a log that is UTF-8 is never
misread; the OEM code page when that fails, which is what the console used.
#>
function Read-L2ProcessLog {
    param([Parameter(Mandatory)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
        return [pscustomobject]@{ Text = $text; Encoding = 'utf-8' }
    } catch [Text.DecoderFallbackException] {
        [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
        # The system's OEM code page, not the current culture's: pwsh may run under en-US on a zh-CN system, and it is
        # the system locale a console process falls back to (936 on both machines this runs on).
        $codePage = [int](Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage' -Name OEMCP)
        return [pscustomobject]@{
            Text     = [Text.Encoding]::GetEncoding($codePage).GetString($bytes)
            Encoding = "code page $codePage"
        }
    }
}

<#
The protocol release identity a server would have answered on /version, read from the ProtocolCandidate block of
the appsettings.json next to the host that ran. A server that refuses to start never serves /version, and the
evidence still has to say which protocol generation it is about. The block mirrors the host's own
ProtocolCandidateIdentity constants field for field (ProtocolIdentityArchitectureTests.
TheShippedSettingsMirrorTheIdentityConstants holds the two together), and the keys below are those of /version.
#>
function Read-L2ProtocolCandidateIdentity {
    param([Parameter(Mandatory)][string]$HostDirectory)

    $candidate = (Get-Content -LiteralPath (Join-Path $HostDirectory 'appsettings.json') -Raw -Encoding utf8 |
        ConvertFrom-Json).ProtocolCandidate
    if ($null -eq $candidate) {
        throw "The host's appsettings.json has no ProtocolCandidate block to read the protocol identity from."
    }
    return [ordered]@{
        repository         = '8005-agv-protocol'
        releaseVersion     = $candidate.releaseVersion
        tag                = $candidate.tag
        commit             = $candidate.repositoryCommit
        protocolVersion    = $candidate.protocolVersion
        profileId          = $candidate.profileId
        manifestSha256     = $candidate.manifestSha256
        schemaBundleSha256 = $candidate.schemaBundleSha256
        vectorsSha256      = $candidate.vectorsSha256
        approvalStatus     = $candidate.approvalStatus
        source             = 'appsettings.json:ProtocolCandidate'
    }
}

Export-ModuleMember -Function Resolve-L2TaskTypeStationsSetting, Resolve-L2ExpectedStartupRefusal,
    New-L2TaskTypeStationPreset, Wait-L2ServerStartupRefusal, Read-L2ProtocolCandidateIdentity
