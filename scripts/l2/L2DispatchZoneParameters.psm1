#Requires -Version 7
Set-StrictMode -Version Latest

# Batch 7's two scenario setup keys (control-server#206): CargoHoldingTimeout and DispatchZoneParameters.
#
# The default precondition is "per-zone dispatch parameters unconfigured": with no DispatchZoneParameters key the
# orchestrator writes no version at all. That is today's behaviour -- no en-route addition, no cargo holding wait, ageing
# without escalation -- and it has to stay the default, or every existing single-demand scenario would load, then hold
# its cargo waiting for a second demand until the cargo holding timeout ran out.
#
# A scenario that needs parameters (control-server#211 to #215) names them per zone. The orchestrator writes them into
# the server's database directly as one version with Source = L2_PRESET, after the server is live and before the
# scenario publishes its first demand. That is not the formal import: it takes no governed snapshot and writes no audit,
# and the evidence for the formal import belongs to control-server#216's scenarios.

$script:ZoneParameterFields = @('EnRouteAdditionMaxPathCostIncrease', 'StarvationThresholdSeconds')
$script:Batch7SetupKeys = @('CargoHoldingTimeout', 'DispatchZoneParameters')

<#
Refuses a setup key that is a near miss of one of batch 7's two keys -- the same letters but another case, or within three
edits of one, ignoring everything but letters and digits: a misspelt key would otherwise be ignored, and the scenario would
run on the default precondition while claiming the one it asked for. A key that only shares a word with them
(CargoHoldingYieldWindow, a later ticket's) is not a near miss and passes.
#>
function Assert-L2Batch7SetupKeysSpelled {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where
    )

    foreach ($key in @($Setup.Keys)) {
        $name = [string]$key
        if ($name -cin $script:Batch7SetupKeys) { continue }
        $normalized = ($name -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
        foreach ($known in $script:Batch7SetupKeys) {
            if ((Get-L2EditDistance -Left $normalized -Right $known.ToLowerInvariant()) -le 3) {
                throw "Unknown setup key '$name' in ${Where}: did you mean ${known}?"
            }
        }
    }
}

# Levenshtein distance, for Assert-L2Batch7SetupKeysSpelled.
function Get-L2EditDistance {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Left, [Parameter(Mandatory)][AllowEmptyString()][string]$Right)

    $previous = [int[]](0..$Right.Length)
    for ($i = 1; $i -le $Left.Length; $i++) {
        $current = [int[]]::new($Right.Length + 1)
        $current[0] = $i
        for ($j = 1; $j -le $Right.Length; $j++) {
            $cost = $Left[$i - 1] -ceq $Right[$j - 1] ? 0 : 1
            $current[$j] = [Math]::Min([Math]::Min($previous[$j] + 1, $current[$j - 1] + 1), $previous[$j - 1] + $cost)
        }
        $previous = $current
    }
    return $previous[$Right.Length]
}

<#
The CargoHoldingTimeout setup value as the server's configuration binds it ("hh:mm:ss"), or $null when the key is
absent -- then nothing is passed and the server keeps its own 30-minute default. Must be a positive time span.
#>
function Resolve-L2CargoHoldingTimeout {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where
    )

    Assert-L2Batch7SetupKeysSpelled -Setup $Setup -Where $Where
    if (-not $Setup.ContainsKey('CargoHoldingTimeout')) { return $null }
    $value = $Setup.CargoHoldingTimeout
    $parsed = [TimeSpan]::Zero
    if ($value -isnot [string] -or
        -not [TimeSpan]::TryParseExact($value, 'c', [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or
        $parsed -le [TimeSpan]::Zero) {
        throw "CargoHoldingTimeout in $Where must be a positive time span written as 'hh:mm:ss', not '$value'."
    }
    return $parsed.ToString('c', [Globalization.CultureInfo]::InvariantCulture)
}

<#
The DispatchZoneParameters setup value as an array of zones, ordered by zone name, or $null when the key is absent.
The value is a table of dispatch zone to a table holding EnRouteAdditionMaxPathCostIncrease (planned path cost, today
millimetres) and StarvationThresholdSeconds, each $null (unconfigured) or a non-negative whole number. 0 and $null are
kept apart: both forbid en-route addition, and both must be storable.
#>
function Resolve-L2DispatchZoneParameters {
    param(
        [Parameter(Mandatory)][hashtable]$Setup,
        [Parameter(Mandatory)][string]$Where
    )

    Assert-L2Batch7SetupKeysSpelled -Setup $Setup -Where $Where
    if (-not $Setup.ContainsKey('DispatchZoneParameters')) { return $null }
    $table = $Setup.DispatchZoneParameters
    if ($table -isnot [hashtable] -or $table.Count -eq 0) {
        throw "DispatchZoneParameters in $Where must be a non-empty table of dispatch zone to parameters; leave the key out for none."
    }

    $zones = @()
    foreach ($zone in @($table.Keys | Sort-Object -CaseSensitive)) {
        $name = [string]$zone
        if ([string]::IsNullOrWhiteSpace($name)) { throw "DispatchZoneParameters in $Where names an empty dispatch zone." }
        $entry = $table[$zone]
        if ($entry -isnot [hashtable]) {
            throw "DispatchZoneParameters in $Where gives zone '$name' no table; give @{ $($script:ZoneParameterFields -join ' = ...; ') = ... }."
        }
        $unknown = @($entry.Keys | Where-Object { [string]$_ -cnotin $script:ZoneParameterFields })
        if ($unknown.Count -gt 0) {
            throw "DispatchZoneParameters in $Where sets '$($unknown -join ', ')' for zone '$name'; only $($script:ZoneParameterFields -join ' and ')."
        }
        $values = [ordered]@{ DispatchZone = $name }
        foreach ($field in $script:ZoneParameterFields) {
            $value = $entry.ContainsKey($field) ? $entry[$field] : $null
            if ($null -ne $value -and (($value -isnot [int] -and $value -isnot [long]) -or $value -lt 0)) {
                throw "DispatchZoneParameters in $Where sets $field of zone '$name' to '$value'; give a non-negative whole number or `$null."
            }
            $values[$field] = $null -eq $value ? $null : [long]$value
        }
        $zones += [pscustomobject]$values
    }
    return , $zones
}

<#
The content the version stands for, in the shape DispatchZoneParameterStore freezes (zones ordered by name), and its
SHA-256. For an L2 preset this digest is an identity, not a governed snapshot's.
#>
function Get-L2DispatchZoneParameterContent {
    param([Parameter(Mandatory)][object[]]$Zones)

    $json = [ordered]@{
        zones = @($Zones | ForEach-Object {
                [ordered]@{
                    dispatchZone                       = $_.DispatchZone
                    enRouteAdditionMaxPathCostIncrease = $_.EnRouteAdditionMaxPathCostIncrease
                    starvationThresholdSeconds         = $_.StarvationThresholdSeconds
                }
            })
    } | ConvertTo-Json -Compress -Depth 4
    $hash = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json))).ToLowerInvariant()
    return [pscustomobject]@{ Json = $json; Sha256 = $hash }
}

<#
Writes the zones into the server's database as the next version (current maximum plus one) with Source = L2_PRESET and
no snapshot, in one write transaction. Returns the version and its digest. Uses the ControlServer build's own
Microsoft.Data.Sqlite, loaded by Open-L2Database.
#>
function Write-L2DispatchZoneParameters {
    param(
        [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][object[]]$Zones,
        [Parameter(Mandatory)][DateTimeOffset]$LoadedAt
    )

    $content = Get-L2DispatchZoneParameterContent -Zones $Zones
    # The text EF Core's SQLite provider stores a DateTimeOffset as, so the server reads it back as its own.
    $loadedAtText = $LoadedAt.ToString('yyyy-MM-dd HH:mm:ss.FFFFFFFzzz', [Globalization.CultureInfo]::InvariantCulture)
    $writer = [Microsoft.Data.Sqlite.SqliteConnection]::new(
        "Data Source=$DatabasePath;Mode=ReadWrite;Cache=Private;Pooling=False;Default Timeout=30")
    $writer.Open()
    try {
        $transaction = $writer.BeginTransaction()
        $next = $writer.CreateCommand()
        $next.Transaction = $transaction
        $next.CommandText = 'SELECT COALESCE(MAX(Version), 0) + 1 FROM DispatchZoneParameterVersions'
        $version = [long]$next.ExecuteScalar()

        $header = $writer.CreateCommand()
        $header.Transaction = $transaction
        $header.CommandText = 'INSERT INTO DispatchZoneParameterVersions (Version, ContentSha256, SnapshotId, LoadedAt, Source) ' +
            "VALUES (`$version, `$sha, NULL, `$loadedAt, 'L2_PRESET')"
        $null = $header.Parameters.AddWithValue('$version', $version)
        $null = $header.Parameters.AddWithValue('$sha', $content.Sha256)
        $null = $header.Parameters.AddWithValue('$loadedAt', $loadedAtText)
        $null = $header.ExecuteNonQuery()

        foreach ($zone in $Zones) {
            $row = $writer.CreateCommand()
            $row.Transaction = $transaction
            $row.CommandText = 'INSERT INTO DispatchZoneParameters ' +
                '(Version, DispatchZone, EnRouteAdditionMaxPathCostIncrease, StarvationThresholdSeconds) ' +
                'VALUES ($version, $zone, $increase, $threshold)'
            $null = $row.Parameters.AddWithValue('$version', $version)
            $null = $row.Parameters.AddWithValue('$zone', $zone.DispatchZone)
            $null = $row.Parameters.AddWithValue('$increase', $zone.EnRouteAdditionMaxPathCostIncrease ?? [DBNull]::Value)
            $null = $row.Parameters.AddWithValue('$threshold', $zone.StarvationThresholdSeconds ?? [DBNull]::Value)
            $null = $row.ExecuteNonQuery()
        }
        $transaction.Commit()
        return [pscustomobject]@{ Version = $version; ContentSha256 = $content.Sha256; Content = $content.Json }
    } finally {
        $writer.Dispose()
    }
}

Export-ModuleMember -Function Assert-L2Batch7SetupKeysSpelled, Resolve-L2CargoHoldingTimeout,
    Resolve-L2DispatchZoneParameters, Get-L2DispatchZoneParameterContent, Write-L2DispatchZoneParameters
