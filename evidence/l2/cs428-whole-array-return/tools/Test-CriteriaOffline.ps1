#Requires -Version 7
<#
.SYNOPSIS
    Replays every read control-server#428 changed, in its old and its new form, against a real database, under three
    conditions: as recorded, with the rows doubled, and with no rows.

.DESCRIPTION
    Nothing here is a copy of a scenario. For each case the read is cut out of the scenario file at an anchor line --
    once from the file as it was on the base commit (-OldRoot) and once from the working tree -- and the criterion is
    a string that must occur verbatim in that file, or the case is refused. What the case adds is only the values the
    scenario would have had in its variables at that point, read from the same database.

    The three conditions are made at the one place every read goes through: Invoke-L2Query is wrapped so that the
    case's own query becomes
        doubled   SELECT * FROM (<sql>) UNION ALL SELECT * FROM (<sql>)      -- "a second row appeared"
        empty     SELECT * FROM (<sql>) WHERE 0                              -- "the row is not there"
    and handed to the real Invoke-L2Query of scripts/l2/L2.psm1, so the return shape under test is the shipped one.

    What each kind of case must show:
      count      an "exactly N rows" criterion.  new: as-is True, doubled False, empty False (a verdict, not a throw).
                                                 old: printed; doubled True means the criterion was idling.
      single-row a read through Read-L2SingleRow. new: the row (or its value) as-is, "(2 rows, expected 1)" doubled,
                                                 $null when empty.
      single-row-required  the same with -Required: a verdict in all three, the two-row and zero-row ones unlike
                                                 the one-row one.
      criterion  a criterion that is not a row count.  new: True as-is, False when empty, never a throw.
      first-row  `if ($rows.Count -eq 0) { return $null }; return $rows[0]` in a function or a probe.
                                                 new: a row (or its value) as-is and doubled, $null when empty.
      index      `(...)[0]` followed by a property read.   new: the same value as-is and doubled.
      text       a diagnostic string only.       new: never throws.
    The old form's results are printed beside them and not judged; the summary counts the count criteria whose old
    form stayed True with the rows doubled.

    Databases: a stage root kept from a passing run (Run-SyntheticL2Local.ps1 -KeepStage), found through the run's
    assertions.json; or, for the real-rig and G3 scenarios this machine cannot run, a database rebuilt from the
    db-<Table>.json snapshots of an earlier real run's evidence. A rebuilt one has only the snapshotted tables.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    # The changed files as they were on the base commit, same relative paths.
    [Parameter(Mandatory)][string]$OldRoot,
    # Evidence root of the -KeepStage run: <scenario>-01/assertions.json names each stage root.
    [Parameter(Mandatory)][string]$KeptEvidenceRoot,
    [Parameter(Mandatory)][string]$CasesPath,
    [string]$ScratchRoot = (Join-Path ([IO.Path]::GetTempPath()) "cs428-offline-$([guid]::NewGuid().ToString('N'))"),
    # Case ids separated by '|'. One string, not an array: `pwsh -File` hands a comma list over as a single element.
    [string]$Only = '',
    # The report, as UTF-8. The console is not: Chinese text printed through it comes out garbled.
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$hostDirectory = Join-Path $Repository 'src/ControlServer.Host/bin/Release/net8.0/win-x64'
Import-Module (Join-Path $Repository 'scripts/l2/L2.psm1') -Force
Import-Module (Join-Path $Repository 'scripts/l2/L2SingleRow.psm1') -Force
$null = New-Item -ItemType Directory -Path $ScratchRoot -Force

# ------------------------------------------------------------------------------------------------ databases

function New-DatabaseFromSnapshots([string]$SnapshotRoot, [string]$Path) {
    # Loads the SQLite assemblies the way the orchestrator does.
    (Open-L2Database -HostDirectory $hostDirectory -DatabasePath ':memory:').Dispose()
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$Path;Pooling=False")
    $connection.Open()
    foreach ($file in Get-ChildItem -LiteralPath $SnapshotRoot -Filter 'db-*.json') {
        $table = $file.BaseName.Substring(3)
        $text = (Get-Content -Raw -LiteralPath $file.FullName)
        if ([string]::IsNullOrWhiteSpace($text)) { continue }   # an empty table is snapshotted as an empty file
        $rows = @($text | ConvertFrom-Json -AsHashtable -NoEnumerate -DateKind String)
        if ($rows.Count -eq 1 -and $rows[0] -is [System.Collections.IList]) { $rows = @($rows[0]) }
        $columns = @($rows | ForEach-Object { $_.Keys } | Select-Object -Unique)
        $command = $connection.CreateCommand()
        $command.CommandText = "CREATE TABLE [$table] ($(($columns | ForEach-Object { "[$_]" }) -join ', '))"
        $null = $command.ExecuteNonQuery()
        foreach ($row in $rows) {
            $insert = $connection.CreateCommand()
            $insert.CommandText = "INSERT INTO [$table] VALUES ($((0..($columns.Count - 1) | ForEach-Object { "`$p$_" }) -join ', '))"
            for ($i = 0; $i -lt $columns.Count; $i++) {
                $value = $row.ContainsKey($columns[$i]) ? $row[$columns[$i]] : $null
                $null = $insert.Parameters.AddWithValue("`$p$i", ($null -eq $value ? [DBNull]::Value : $value))
            }
            $null = $insert.ExecuteNonQuery()
        }
    }
    $connection.Dispose()
}

$script:databases = @{}
function Get-CaseDatabase([hashtable]$Case) {
    $key = $Case.ContainsKey('Snapshot') ? "snapshot:$($Case.Snapshot)" : "stage:$($Case.Stage)"
    if ($script:databases.ContainsKey($key)) { return $script:databases[$key] }
    if ($Case.ContainsKey('Snapshot')) {
        $path = Join-Path $ScratchRoot "$(($Case.Snapshot -replace '[\\/:]', '_')).db"
        New-DatabaseFromSnapshots (Join-Path $Repository $Case.Snapshot) $path
        $source = "rebuilt from $($Case.Snapshot)"
    } else {
        $assertions = Get-Content -Raw -LiteralPath (Join-Path $KeptEvidenceRoot "$($Case.Stage)-01/assertions.json") | ConvertFrom-Json
        if ($assertions.outcome -ne 'PASS') { throw "$($Case.Stage): the kept run is $($assertions.outcome), not PASS" }
        $path = Join-Path $assertions.identity.stageRoot 'controlserver.db'
        if (-not (Test-Path -LiteralPath $path)) { throw "$($Case.Stage): no kept database at $path" }
        $source = "kept stage of $($Case.Stage) run $($assertions.runId) at $($assertions.identity.controlServerCommit.Substring(0, 8))"
    }
    $script:databases[$key] = [pscustomobject]@{ Path = $path; Source = $source }
    return $script:databases[$key]
}

# ------------------------------------------------------------------------------------------------ the replay

function Get-Normalized([string]$Text) { return ($Text -replace '\s+', ' ').Trim() }

function Format-Seen($Value) {
    if ($null -eq $Value) { return '$null' }
    if ($Value -is [bool]) { return "$Value" }
    if ($Value -is [array]) { return "array[$($Value.Count)]" }
    if ($Value -is [string]) { return "'$Value'" }
    if ($Value -is [ValueType]) { return "$Value" }
    # Read-L2SingleRow's stand-in: every column reads "(N rows, expected 1)".
    $texts = @($Value.PSObject.Properties | ForEach-Object { [string]$_.Value } | Select-Object -Unique)
    if ($texts.Count -eq 1 -and $texts[0] -match '^\(\d+ rows, expected 1\)$') { return "stand-in $($texts[0])" }
    return 'row'
}

# The text of a case in one form: the functions it needs and the read itself, cut out of the scenario file.
function Get-CaseSource([hashtable]$Case, [string]$Form) {
    $path = Join-Path ($Form -eq 'old' ? $OldRoot : $Repository) $Case.File
    $text = Get-Content -Raw -LiteralPath $path
    $lines = $text -split "`r?`n"
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$null)
    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($name in @($Case['Functions'])) {
        if (-not $name) { continue }
        $definition = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
        if ($null -eq $definition) { throw "$($Case.Id): no function $name in $($Case.File)" }
        $parts.Add($definition.Extent.Text)
    }
    # Cut by ANCHOR, not by line number: the two forms of a file no longer have the same line numbers. An anchor is
    # a regex that must match exactly one line of the file; Cut is a list of (anchor, number of lines from there).
    # CutOld and ProbeOld are for a read whose first line reads differently in the old form.
    $findLine = {
        param([string]$anchor)
        $hits = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -match $anchor })
        if ($hits.Count -ne 1) { throw "$($Case.Id): anchor /$anchor/ matches $($hits.Count) lines of $($Case.File) ($Form)" }
        return $hits[0]
    }
    $cuts = ($Form -eq 'old' -and $Case.ContainsKey('CutOld')) ? $Case.CutOld : $Case['Cut']
    foreach ($cut in @($cuts)) {
        if ($null -eq $cut) { continue }
        $start = & $findLine $cut[0]
        $parts.Add(($lines[$start..($start + $cut[1] - 1)]) -join "`n")
    }
    $probe = ($Form -eq 'old' -and $Case.ContainsKey('ProbeOld')) ? $Case.ProbeOld : $Case['Probe']
    if ($probe) {
        # The innermost script block around the anchored line: the probe handed to Wait-L2Condition.
        $probeLine = (& $findLine $probe) + 1
        $block = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ScriptBlockExpressionAst] -and
                $n.Extent.StartLineNumber -le $probeLine -and $n.Extent.EndLineNumber -ge $probeLine }, $true) |
            Sort-Object { $_.Extent.EndOffset - $_.Extent.StartOffset } | Select-Object -First 1
        if ($null -eq $block) { throw "$($Case.Id): no script block around line $probeLine" }
        $parts.Add("`$probeUnderTest = $($block.Extent.Text)")
    }
    $body = $parts -join "`n"
    $observe = ($Form -eq 'old' -and $Case.ContainsKey('ObserveOld')) ? $Case.ObserveOld : $Case['Observe']
    $wrapped = "$body`n$observe" -match '@\(\s*(Invoke-L2Query|Get-PlanLegs|Get-AcknowledgedWorklists|Wait-L2RealOrLast)'
    if ($Form -eq 'old' -and -not $wrapped) { throw "$($Case.Id): the old text does not wrap the helper -- wrong lines?" }
    if ($Form -eq 'new' -and $wrapped) { throw "$($Case.Id): the new text still wraps the helper" }
    if ($observe -and -not $Case['ObserveIsCall']) {
        if (-not (Get-Normalized $text).Contains((Get-Normalized $observe))) {
            throw "$($Case.Id): the criterion text is not in $($Case.File) ($Form) verbatim: $observe"
        }
    }
    return $body
}

$script:injectedCondition = 'as-is'
$script:injectPattern = '.'
function Invoke-L2Query {
    param($Connection, $Sql)
    $text = $Sql
    if ($Sql -match $script:injectPattern) {
        $text = switch ($script:injectedCondition) {
            'doubled' { "SELECT * FROM ($Sql) UNION ALL SELECT * FROM ($Sql)" }
            'empty'   { "SELECT * FROM ($Sql) WHERE 0" }
            default   { $Sql }
        }
    }
    return L2\Invoke-L2Query -Connection $Connection -Sql $text
}
function Read-L2SingleRow {
    param($Connection, $Sql, [switch]$Required)
    $text = $Sql
    if ($Sql -match $script:injectPattern) {
        $text = switch ($script:injectedCondition) {
            'doubled' { "SELECT * FROM ($Sql) UNION ALL SELECT * FROM ($Sql)" }
            'empty'   { "SELECT * FROM ($Sql) WHERE 0" }
            default   { $Sql }
        }
    }
    return L2SingleRow\Read-L2SingleRow -Connection $Connection -Sql $text -Required:$Required
}
# For a case's own setup: never injected.
function Query([string]$Sql) { return L2\Invoke-L2Query -Connection $connection -Sql $Sql }

function Invoke-Case([hashtable]$Case, [string]$Form, [string]$Condition) {
    $database = Get-CaseDatabase $Case
    $connection = Open-L2Database -HostDirectory $hostDirectory -DatabasePath $database.Path
    try {
        $observe = if ($Case.ContainsKey('Probe')) { '& $probeUnderTest' }
            elseif ($Form -eq 'old' -and $Case.ContainsKey('ObserveOld')) { $Case.ObserveOld } else { $Case.Observe }
        $text = @(
            'Set-StrictMode -Version Latest'
            $Case['Setup']
            '$script:injectedCondition = $Condition'
            (Get-CaseSource $Case $Form)
            "`$observed = $observe"
            '$script:injectedCondition = ''as-is'''
            # In a property, not on the pipeline: a probe that returned nothing must read as $null here, and an
            # array must arrive as the array it was.
            '[pscustomobject]@{ Observed = $observed }'
        ) -join "`n"
        $script:injectPattern = $Case['Inject'] ?? '.'
        try {
            $emitted = @(& ([scriptblock]::Create($text)))
            return Format-Seen $emitted[-1].Observed
        } catch {
            $script:injectedCondition = 'as-is'
            return "throws ($(($_.Exception.Message -split "`r?`n")[0] -replace '\s+', ' ' | ForEach-Object { $_.Substring(0, [Math]::Min(70, $_.Length)) }))"
        }
    } finally {
        $connection.Dispose()
    }
}

# ------------------------------------------------------------------------------------------------ the cases

$cases = & $CasesPath
$onlyIds = @($Only -split '\|' | ForEach-Object Trim | Where-Object { $_ })
$unknown = @($onlyIds | Where-Object { $_ -notin @($cases | ForEach-Object Id) })
if ($unknown.Count -gt 0) { throw "No such case: $($unknown -join ' | ')" }
$rows = [System.Collections.Generic.List[object]]::new()
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($case in $cases) {
    if ($onlyIds.Count -gt 0 -and $case.Id -notin $onlyIds) { continue }
    $seen = @{}
    foreach ($form in 'old', 'new') {
        foreach ($condition in 'as-is', 'doubled', 'empty') {
            $seen["$form/$condition"] = Invoke-Case $case $form $condition
        }
    }
    $new = @($seen['new/as-is'], $seen['new/doubled'], $seen['new/empty'])
    $verdict = switch ($case.Kind) {
        'count' { $new[0] -eq 'True' -and $new[1] -eq 'False' -and $new[2] -eq 'False' }
        'first-row' { $new[0] -notlike 'throws*' -and $new[0] -ne '$null' -and $new[0] -notlike 'array*' -and $new[1] -eq $new[0] -and $new[2] -eq '$null' }
        'single-row' {
            $new[0] -notlike 'throws*' -and $new[0] -ne '$null' -and $new[0] -notlike 'array*' -and $new[0] -notlike '*rows, expected 1*' -and
            $new[1] -like '*(2 rows, expected 1)*' -and $new[2] -eq '$null'
        }
        # A read that is not polled: not one row must be a verdict that differs from the one-row verdict, never a throw.
        'single-row-required' {
            @($new | Where-Object { $_ -like 'throws*' }).Count -eq 0 -and $new[1] -ne $new[0] -and $new[2] -ne $new[0] -and
            ($new[1] -in 'True', 'False' -or ($new[1] -like '*(2 rows, expected 1)*' -and $new[2] -like '*(0 rows, expected 1)*'))
        }
        'criterion' { $new[0] -eq 'True' -and $new[2] -eq 'False' -and $new[1] -notlike 'throws*' }
        'index'     { $new[0] -notlike 'throws*' -and $new[1] -eq $new[0] }
        'text'      { @($new | Where-Object { $_ -like 'throws*' }).Count -eq 0 -and (-not $case['ExpectNew'] -or (($new -join ' | ') -ceq ($case.ExpectNew -join ' | '))) }
        default     { throw "$($case.Id): unknown kind $($case.Kind)" }
    }
    if (-not $verdict) { $problems.Add("$($case.Id) ($($case.Kind)) new=$($new -join ' | ') old/doubled=$($seen['old/doubled'])") }
    $rows.Add([pscustomobject]@{
            Idle = ($case.Kind -eq 'count' -and $seen['old/doubled'] -eq 'True')
            Id = $case.Id; Kind = $case.Kind; Site = "$(Split-Path -Leaf $case.File)  $($case['Site'] ?? $case['Probe'] ?? @($case['Cut'])[0][0])"
            Database = (Get-CaseDatabase $case).Source; Ok = $verdict; Seen = $seen; Observe = $case['Observe']; Note = $case['Note']
        })
}

if ($rows.Count -eq 0) { throw 'No case ran.' }
$report = [System.Collections.Generic.List[string]]::new()
$report.Add("HEAD $((git -C $Repository rev-parse HEAD).Trim()); old form from $OldRoot")
foreach ($row in $rows) {
    $report.Add('')
    $report.Add("$($row.Ok ? 'ok  ' : 'FAIL') $($row.Id)  [$($row.Kind)]  $($row.Site)")
    $report.Add("     db: $($row.Database)")
    if ($row.Note) { $report.Add("     note: $($row.Note)") }
    if ($row.Observe) { $report.Add("     criterion: $(Get-Normalized $row.Observe)") }
    foreach ($form in 'old', 'new') {
        $report.Add(("     {0}: as-is   {1}" -f $form, $row.Seen["$form/as-is"]))
        $report.Add(("          doubled {0}" -f $row.Seen["$form/doubled"]))
        $report.Add(("          empty   {0}" -f $row.Seen["$form/empty"]))
    }
}
$report.Add('')
$report.Add("$($rows.Count) cases, $(@($rows | Where-Object Ok).Count) as required, $($problems.Count) not.")
$counts = @($rows | Where-Object Kind -eq 'count')
$report.Add("count criteria: $($counts.Count). Old form with the rows doubled: " +
    "$(@($counts | Where-Object { $_.Seen['old/doubled'] -eq 'True' }).Count) stayed True (idle), " +
    "$(@($counts | Where-Object { $_.Seen['old/doubled'] -eq 'False' }).Count) went False (another conjunct read the joined rows), " +
    "$(@($counts | Where-Object { $_.Seen['old/doubled'] -like 'throws*' }).Count) threw.")
$report.Add("Old form with no rows: $(@($rows | Where-Object { $_.Seen['old/empty'] -like 'throws*' }).Count) of $($rows.Count) threw; new form: $(@($rows | Where-Object { $_.Seen['new/empty'] -like 'throws*' }).Count).")
foreach ($problem in $problems) { $report.Add("NOT AS REQUIRED: $problem") }
$report | ForEach-Object { Write-Host $_ }
if ($ReportPath) { $report | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM }
Remove-Item -LiteralPath $ScratchRoot -Recurse -Force -ErrorAction SilentlyContinue
if ($problems.Count -gt 0) { exit 1 }
