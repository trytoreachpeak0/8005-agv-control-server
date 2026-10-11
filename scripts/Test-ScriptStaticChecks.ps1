#Requires -Version 7
<#
.SYNOPSIS
    Offline static checks over the repository's PowerShell, and their self-check (control-server#583).

.DESCRIPTION
    Pure parsing, no rig, no window, no database, seconds. Each check runs twice: once against small fixtures written to
    %TEMP% here, which it must flag (and, for the counterexamples, must leave alone), and once against scripts/ itself,
    which it must leave clean AND must have read at least a known number of things -- a scan that broke would otherwise
    report zero findings for ever, which is also what success looks like.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-ScriptStaticChecks.ps1

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Test-ScriptStaticChecks.ps1 -SkipSelfTest -Path <file>, <file>
    Scans only the given files (the red evidence on bc22cfd25^ was taken this way).
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot,

    # Scan these files instead of every script under ScriptRoot. The lower bounds on what was scanned apply only to the
    # full scan.
    [string[]]$Path,

    [switch]$SkipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ScriptStaticChecks.psm1') -Force

$cases = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $cases.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}
function Format-Finding([object[]]$Findings) {
    return (@($Findings | ForEach-Object { "$([IO.Path]::GetFileName($_.File)):$($_.Line) $($_.Message)" }) -join ' | ')
}

if (-not $SkipSelfTest) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "script-static-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $scratch -Force
    try {
        function New-Fixture([string]$Name, [string]$Body) {
            $file = Join-Path $scratch $Name
            Set-Content -LiteralPath $file -Encoding utf8NoBOM -Value $Body
            return $file
        }

        # --- 1. every script parses -------------------------------------------------------------------------------------
        # The cs#472 shape: "$name:" is a drive-qualified variable, and the whole file fails to parse.
        $broken = New-Fixture 'parse-broken.ps1' "`$x = 1`n`$y = 2`nWrite-Host `"value `$InstanceDefinitionPath: done`"`n"
        $clean = New-Fixture 'parse-clean.ps1' "`$x = 1`nWrite-Host `"value `${InstanceDefinitionPath}: done`"`n"
        $parse = Test-ScriptParse -Path $broken, $clean
        $hit = @($parse.Findings | Where-Object { $_.File -eq $broken })
        Add-Case 'parse: a syntax error is reported with its file and line' ($hit.Count -ge 1 -and $hit[0].Line -eq 3) (Format-Finding $parse.Findings)
        Add-Case 'parse: a clean file is not reported' (@($parse.Findings | Where-Object { $_.File -eq $clean }).Count -eq 0) (Format-Finding $parse.Findings)
        Add-Case 'parse: both files were read' ($parse.Files -eq 2) "$($parse.Files)"

        # --- 2. properties read off a helper's literal object -----------------------------------------------------------
        $catalog = Get-HelperShapeCatalog -ScriptRoot $ScriptRoot
        $shape = @{}; foreach ($entry in $catalog) { $shape[$entry.Name] = $entry.Properties }
        Add-Case 'catalog: every listed helper resolved to a literal object' (@($catalog | Where-Object { -not $_.Relay -and @($_.Properties).Count -eq 0 }).Count -eq 0 -and @($catalog | Where-Object { -not $_.Relay }).Count -ge 8) "$(@($catalog).Count) entries"
        # The two premises the bc22cfd25 counterexample rests on (L2RealStation.psm1, L2RealOnboard.psm1).
        Add-Case 'catalog: Get-L2OperationResults has the shape of Get-L2Inbound, without PayloadJson' (
            $shape.ContainsKey('Get-L2OperationResults') -and ($shape['Get-L2OperationResults'] -join ',') -eq ($shape['Get-L2Inbound'] -join ',') -and
            $shape['Get-L2Inbound'] -notcontains 'PayloadJson' -and $shape['Get-L2Inbound'] -contains 'Payload') "$($shape['Get-L2OperationResults'] -join ',')"
        Add-Case 'catalog: Get-L2RealInbound carries PayloadJson' ($shape.ContainsKey('Get-L2RealInbound') -and $shape['Get-L2RealInbound'] -contains 'PayloadJson') "$($shape['Get-L2RealInbound'] -join ',')"

        # A listed function that does not return one literal object must stop the catalog, not shrink it.
        $notLiteral = New-Fixture 'NotLiteral.psm1' "function Get-Rows { param(`$c) return Invoke-L2Query -Connection `$c -Sql 'SELECT 1' }`n"
        $refused = try { $null = Get-HelperShapeCatalog -ScriptRoot $scratch -Entries @(@{ Name = 'Get-Rows'; File = 'NotLiteral.psm1' }); '' } catch { "$_" }
        Add-Case 'catalog: a listed function without a literal object is refused' ($refused -like '*Get-Rows*') $refused

        # g3-slot-fault-declaration.ps1 at bc22cfd25^, lines 269-293 and 201-222, reduced to the reads that matter.
        $slotFault = New-Fixture 'slot-fault.ps1' @'
$refusal = Wait-L2RealOrLast -Description 'the onboard answered the replayed declaration' `
    -Journal $journal -Criterion 'declaration-not-applicable' -TimeoutSeconds 90 `
    -Probe { @((Get-L2RealInbound $connection 'SlotFaultDeclarationResult') | Where-Object { [string]$_.Payload.declarationId -eq $lostId }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$refusalId = if ($refusal) { ([string]$refusal.MessageId).ToLowerInvariant() } else { '' }
$x = $(if ($refusal) { $refusal.PayloadJson } else { '(no result)' })
$result = Wait-L2RealOrLast -Description 'the onboard reported the unload' `
    -Journal $journal -Criterion 'unload-result' -TimeoutSeconds 30 `
    -Probe { (Get-L2OperationResults -Connection $connection -AttemptId $unloadAttempt) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$resultId = if ($result) { ([string]$result.MessageId).ToLowerInvariant() } else { '' }
$y = $(if ($result) { $result.PayloadJson } else { '(no OperationResult)' })
'@
        $audit = Find-HelperPropertyMisuse -Path $slotFault -Catalog $catalog
        Add-Case 'property: $result.PayloadJson off Get-L2OperationResults through Wait-L2RealOrLast is reported at line 12' (
            @($audit.Findings | Where-Object { $_.Line -eq 12 -and $_.Message -like '*PayloadJson*Get-L2OperationResults*' }).Count -eq 1) (Format-Finding $audit.Findings)
        Add-Case 'property: $refusal.PayloadJson off Get-L2RealInbound is not reported (bc22cfd25^ line 222)' (
            @($audit.Findings | Where-Object { $_.Line -ne 12 }).Count -eq 0) (Format-Finding $audit.Findings)
        Add-Case 'property: both variables were recognised' ($audit.Variables -eq 2) "$($audit.Variables)"

        $shapes = New-Fixture 'shapes.ps1' @'
$rows = Get-L2Inbound -Connection $c -MessageType 'X'
$n = $rows.Count
$bad = $rows.Bogus
foreach ($m in (Get-L2Inbound -Connection $c -MessageType 'Y')) { $m.Payload; $m.Nope }
$first = @(Get-L2OperationProgress -Connection $c -AttemptId 'a')[0]
$first.Phase; $first.PhaseName
$twice = Get-L2Inbound -Connection $c -MessageType 'X'
$twice = Get-Something
$twice.Whatever
'@
        $audit = Find-HelperPropertyMisuse -Path $shapes -Catalog $catalog
        $lines = @($audit.Findings | ForEach-Object Line | Sort-Object)
        Add-Case 'property: direct assignment, foreach variable and an index into @() are each checked' (($lines -join ',') -eq '3,4,6') (Format-Finding $audit.Findings)
        Add-Case 'property: a variable also assigned from something unknown is skipped and counted' ($audit.Unresolved -eq 1) "$($audit.Unresolved)"

        $shadow = New-Fixture 'shadow.ps1' @'
function Get-L2Inbound { [pscustomobject]@{ Mine = 1 } }
$rows = Get-L2Inbound
$rows.Mine
'@
        $audit = Find-HelperPropertyMisuse -Path $shadow -Catalog $catalog
        Add-Case 'property: a file that defines its own function of a listed name is not judged by the listed shape' (@($audit.Findings).Count -eq 0 -and $audit.Shadowed -eq 1) "$(Format-Finding $audit.Findings); shadowed $($audit.Shadowed)"

        # --- 3. a [string] helper's result compared with $null ----------------------------------------------------------
        $helpers = New-Fixture 'StringHelpers.psm1' @'
function Get-Text([object]$c, [string]$Sql) {
    $rows = Invoke-L2Query -Connection $c -Sql $Sql
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Value
}
function Get-Number([object]$c, [string]$Sql) { return [int](Invoke-L2Query -Connection $c -Sql $Sql)[0].Total }
function Get-Mixed([object]$c) { if ($c) { return [string]$c } return $c }
function Get-Guarded([object]$c, [string]$Sql) {
    $rows = Invoke-L2Query -Connection $c -Sql $Sql
    if ($rows.Count -eq 0) { return $null }
    if (-not (Test-L2RealPresent $rows[0].Value)) { return $null }
    return [string]$rows[0].Value
}
function Get-Column([object]$c, [string]$Id) {
    $row = Read-L2SingleRow (Invoke-L2Query -Connection $c -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$Id'")
    if ($null -eq $row) { return $null }
    return [string]$row.Stage
}
'@
        $stringHelpers = Get-StringHelperCatalog -Path $helpers
        # Only a reader that takes any SQL and casts the value without first testing it for NULL folds NULL into ''. A guarded
        # reader (Get-L2RealScalar's shape) answers $null for NULL too; a fixed-column reader returns $null for "no row" on
        # purpose, and whether its column can be NULL is the schema's business, not this scan's.
        Add-Case 'string catalog: only an unguarded any-SQL reader casting to [string] is a NULL-folding helper' (
            ($stringHelpers -join ',') -eq 'Get-Text') ($stringHelpers -join ',')
        $repoStringHelpers = Get-StringHelperCatalog -Path @(Get-ChildItem -LiteralPath $ScriptRoot -Recurse -File -Include '*.ps1', '*.psm1' | ForEach-Object FullName)
        Add-Case 'string catalog: Get-G3Scalar is derived from G3RecoveryCommon.ps1, Get-G3Count is not' (
            $repoStringHelpers -contains 'Get-G3Scalar' -and $repoStringHelpers -notcontains 'Get-G3Count') ($repoStringHelpers -join ',')
        Add-Case 'string catalog: Get-L2RealScalar (guarded) and Get-L2JourneyStage (fixed column) are not NULL-folding' (
            $repoStringHelpers -notcontains 'Get-L2RealScalar' -and $repoStringHelpers -notcontains 'Get-L2JourneyStage') ($repoStringHelpers -join ',')

        # g3-forced-mechanical-recovery.ps1 at bc22cfd25^, lines 153 and 170, reduced to the comparison that matters.
        $forced = New-Fixture 'forced.ps1' @'
$closedReason = Get-G3Scalar $connection "SELECT ClosedReason AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = 'x'"
$ok = ($recoverySession -eq 'CLOSED' -and
    ($null -eq $closedReason -or $closedReason -is [System.DBNull]))
$closedReasonIsNull = (Get-G3Scalar $connection "SELECT ClosedReason IS NULL AS Value FROM ExceptionRecoverySessions") -eq '1'
$a = $closedReason -eq $null
$b = $null -ne (Get-G3Scalar $connection 'SELECT 1 AS Value')
$c = $closedReason -eq ''
$n = Get-G3Count $connection 'SELECT COUNT(*) AS Total FROM AcceptedDemands'
$d = $null -eq $n
'@
        $nulls = Find-StringHelperNullComparison -Path $forced -Helpers $repoStringHelpers
        $lines = @($nulls.Findings | ForEach-Object Line | Sort-Object)
        Add-Case 'string-null: $null -eq $closedReason (bc22cfd25^ line 170), $x -eq $null and $null -ne (Get-G3Scalar ...) are reported' (
            ($lines -join ',') -eq '3,5,6') (Format-Finding $nulls.Findings)
        Add-Case 'string-null: the comparisons that were checked include the three' ($nulls.Comparisons -ge 3) "$($nulls.Comparisons)"

        $shadowString = New-Fixture 'shadow-string.ps1' @'
function Get-G3Scalar([object]$c, [string]$Sql) { return (Invoke-L2Query -Connection $c -Sql $Sql)[0] }
$v = Get-G3Scalar $connection 'SELECT 1 AS Value'
$e = $null -eq $v
'@
        $nulls = Find-StringHelperNullComparison -Path $shadowString -Helpers $repoStringHelpers
        Add-Case 'string-null: a file whose own Get-G3Scalar returns rows is not judged as the shared one' (@($nulls.Findings).Count -eq 0) (Format-Finding $nulls.Findings)

        # An exemption names file, function and variable, and must still match: one that matches nothing is reported stale.
        $exempt = New-Fixture 'exempt.ps1' @'
function Get-FleetGeneration {
    $value = Get-G3Scalar $connection "SELECT ForcedRecoveryGeneration AS Value FROM VehicleRecoveryGenerations"
    if ($null -eq $value) { return 0 }
    return [long]$value
}
$other = Get-G3Scalar $connection 'SELECT 1 AS Value'
$f = $null -eq $other
'@
        $exemptions = @(
            @{ File = 'exempt.ps1'; Function = 'Get-FleetGeneration'; Variable = 'value'; Reason = 'fixture' }
            @{ File = 'exempt.ps1'; Function = 'Get-Nothing'; Variable = 'value'; Reason = 'fixture, stale' }
        )
        $nulls = Find-StringHelperNullComparison -Path $exempt -Helpers $repoStringHelpers -Exemptions $exemptions
        Add-Case 'string-null: an exempted comparison is not reported, the one beside it is' (
            (@($nulls.Findings | ForEach-Object Line) -join ',') -eq '7' -and $nulls.Exempted -eq 1) "$(Format-Finding $nulls.Findings); exempted $($nulls.Exempted)"
        Add-Case 'string-null: an exemption that matches nothing is reported stale' (
            (@($nulls.StaleExemptions | ForEach-Object { $_.Function }) -join ',') -eq 'Get-Nothing') "$(@($nulls.StaleExemptions | ForEach-Object { $_.Function }) -join ',')"

        # An exemption that rests on a fact about the schema carries that fact, and the check re-reads it from the EF model
        # snapshot every run: if the column ever becomes nullable, the exemption is void and the comparison is reported.
        $repositoryRoot = Split-Path -Parent $ScriptRoot
        $real = @(Get-StringNullExemption)[0]
        $premise = Test-StringNullExemptionPremise -RepositoryRoot $repositoryRoot -Exemption $real
        Add-Case 'string-null premise: ForcedRecoveryGeneration is a non-null long in the repository''s model snapshot' ($premise.Holds) $premise.Detail
        $null = New-Fixture 'Snapshot.cs' @'
            modelBuilder.Entity("ControlServer.Infrastructure.Persistence.OtherRow", b =>
                {
                    b.Property<long>("ForcedRecoveryGeneration")
                        .HasColumnType("INTEGER");
                });

            modelBuilder.Entity("ControlServer.Infrastructure.Persistence.VehicleRecoveryGenerationRow", b =>
                {
                    b.Property<long?>("ForcedRecoveryGeneration")
                        .HasColumnType("INTEGER");

                    b.ToTable("VehicleRecoveryGenerations");
                });
'@
        $voided = @{ File = 'exempt.ps1'; Function = 'Get-FleetGeneration'; Variable = 'value'; Reason = 'fixture'
            Premise = @{ Snapshot = 'Snapshot.cs'; Entity = $real.Premise.Entity; Property = $real.Premise.Property; ClrType = $real.Premise.ClrType } }
        $premise = Test-StringNullExemptionPremise -RepositoryRoot $scratch -Exemption $voided
        Add-Case 'string-null premise: the same column as long? in that entity voids it (a long in another entity does not save it)' (-not $premise.Holds) $premise.Detail
        $nulls = Find-StringHelperNullComparison -Path $exempt -Helpers $repoStringHelpers -Exemptions @($voided) -RepositoryRoot $scratch
        Add-Case 'string-null premise: a voided exemption lets the comparison be reported, saying why' (
            @($nulls.Findings | Where-Object { $_.Line -eq 3 -and $_.Message -like '*premise*' }).Count -eq 1 -and $nulls.Exempted -eq 0) (Format-Finding $nulls.Findings)

        # The premise is about the column the exempted function reads, so the function must still read that column: a
        # rewrite to SELECT another column voids the exemption even while the snapshot is unchanged (review, optional).
        $otherColumn = New-Fixture 'exempt-other-column.ps1' @'
function Get-FleetGeneration {
    $value = Get-G3Scalar $connection "SELECT ForcedRecoveryPending AS Value FROM VehicleRecoveryGenerations"
    if ($null -eq $value) { return 0 }
    return [long]$value
}
'@
        $moved = @{ File = 'exempt-other-column.ps1'; Function = 'Get-FleetGeneration'; Variable = 'value'; Reason = 'fixture'; Premise = $real.Premise }
        $nulls = Find-StringHelperNullComparison -Path $otherColumn -Helpers $repoStringHelpers -Exemptions @($moved) -RepositoryRoot $repositoryRoot
        Add-Case 'string-null premise: an exempted function that no longer reads the premised column is reported' (
            @($nulls.Findings | Where-Object { $_.Line -eq 3 -and $_.Message -like '*ForcedRecoveryGeneration*' }).Count -eq 1) (Format-Finding $nulls.Findings)
    } finally {
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- the repository ---------------------------------------------------------------------------------------------------
$full = -not $Path
# @() around the if: one -Path file would otherwise come out of it as a bare string.
$targets = @(if ($full) {
    Get-ChildItem -LiteralPath $ScriptRoot -Recurse -File -Include '*.ps1', '*.psm1' | ForEach-Object FullName
} else {
    $Path | ForEach-Object { (Resolve-Path -LiteralPath $_).ProviderPath }
})

$parse = Test-ScriptParse -Path $targets
Write-Host "parse: $($parse.Files) files read, $(@($parse.Findings).Count) with errors"
foreach ($f in $parse.Findings) { Write-Host "  PARSE  $($f.File):$($f.Line):$($f.Column) $($f.Message)" }
Add-Case 'repository: every script parses' (@($parse.Findings).Count -eq 0) "$(@($parse.Findings).Count) errors"
# 231 .ps1/.psm1 under scripts/ on fp/v2-impl@da6e40e2; the bound leaves room to delete some, not to lose the glob.
if ($full) { Add-Case 'repository: parse read at least 200 scripts' ($parse.Files -ge 200) "$($parse.Files)" }

$audit = Find-HelperPropertyMisuse -Path $targets -Catalog (Get-HelperShapeCatalog -ScriptRoot $ScriptRoot)
Write-Host ("property: {0} variables recognised, {1} reads checked; skipped: {2} variables also assigned from something unrecognised, {3} calls in files that shadow a listed name" -f
    $audit.Variables, $audit.Reads, $audit.Unresolved, $audit.Shadowed)
foreach ($f in $audit.Findings) { Write-Host "  PROPERTY  $($f.File):$($f.Line) $($f.Message)" }
Add-Case 'repository: no property read outside its helper''s object' (@($audit.Findings).Count -eq 0) "$(@($audit.Findings).Count) findings"
if ($full) {
    Add-Case 'repository: property audit recognised at least 100 variables and 250 reads' ($audit.Variables -ge 100 -and $audit.Reads -ge 250) "$($audit.Variables) / $($audit.Reads)"
}

# The string helpers are always derived from the whole of ScriptRoot, so a -Path scan of an old copy knows Get-G3Scalar.
$stringHelpers = Get-StringHelperCatalog -Path @(Get-ChildItem -LiteralPath $ScriptRoot -Recurse -File -Include '*.ps1', '*.psm1' | ForEach-Object FullName)
$nulls = Find-StringHelperNullComparison -Path $targets -Helpers $stringHelpers -Exemptions (Get-StringNullExemption)
Write-Host ("string-null: {0} NULL-folding readers ({1}); {2} comparisons with `$null read, {3} variables held a reader's result, {4} exempted" -f
    @($stringHelpers).Count, ($stringHelpers -join ', '), $nulls.Comparisons, $nulls.Variables, $nulls.Exempted)
foreach ($f in $nulls.Findings) { Write-Host "  STRING-NULL  $($f.File):$($f.Line) $($f.Message)" }
if ($full) { foreach ($e in $nulls.StaleExemptions) { Write-Host "  STRING-NULL  stale exemption: $($e.File) $($e.Function) `$$($e.Variable)" } }
Add-Case 'repository: no NULL-folding reader result compared with $null' (@($nulls.Findings).Count -eq 0) "$(@($nulls.Findings).Count) findings"
if ($full) {
    Add-Case 'repository: every string-null exemption still matches' (@($nulls.StaleExemptions).Count -eq 0) "$(@($nulls.StaleExemptions).Count) stale"
    # Get-G3Scalar plus the scenario-local Get-Scalar copies on fp/v2-impl@da6e40e2; 1800+ $null comparisons read.
    # 38 variables held a reader's result on fp/v2-impl@15d96bea.
    Add-Case 'repository: string-null derived Get-G3Scalar, read at least 1000 $null comparisons and 30 reader variables' (
        $stringHelpers -contains 'Get-G3Scalar' -and $nulls.Comparisons -ge 1000 -and $nulls.Variables -ge 30) "$($nulls.Comparisons) / $($nulls.Variables)"
}

$failed = @($cases | Where-Object { -not $_.Ok })
foreach ($c in $cases) { Write-Host ("{0}  {1}{2}" -f ($c.Ok ? 'ok  ' : 'FAIL'), $c.Name, ($c.Ok ? '' : " -- $($c.Actual)")) }
Write-Host "$($cases.Count - $failed.Count)/$($cases.Count) passed"
exit ($failed.Count -eq 0 ? 0 : 1)
