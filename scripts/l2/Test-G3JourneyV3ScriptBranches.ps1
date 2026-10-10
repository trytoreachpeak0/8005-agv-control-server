#Requires -Version 7

<#
.SYNOPSIS
    Self-check for the two journey G3 script branches control-server#555 fixed: g3-slot-fault-declaration.ps1's G3-07-67
    and g3-forced-mechanical-recovery.ps1's G3-07-44.

.DESCRIPTION
    A few seconds, no rig, no window. Both lines sat on branches no run had reached before the batch-8 exit
    (control-server#393): the journey runner takes its scenarios from the bound ControlServer commit, so they only run for
    real once the exit moves the binding. This covers what can be said before:

      - Get-L2Inbound (L2RealStation.psm1) builds its rows with exactly MessageId, At, Payload, Response and
        ResponsePayload -- read from the function's own AST, not restated here. Under Set-StrictMode -Version Latest, a
        row of that shape throws on .PayloadJson, which is what stopped G3-07-67, and serialises through Payload.
      - against a real SQLite file, with the real Invoke-L2Query and Get-G3Scalar: a NULL ClosedReason reads back as "",
        so the old test ($null -eq $x -or $x -is [System.DBNull]) is false for NULL -- what kept G3-07-44 red whatever
        the server wrote -- while asking SQLite "ClosedReason IS NULL" says 1 for NULL and 0 for an empty string. A
        NULL-or-empty test would take the empty string too; this one does not.
      - the scenarios themselves, read from their AST: G3-07-67's actual-value argument no longer reads PayloadJson off
        $result, and G3-07-44's condition uses $closedReasonIsNull, which is computed from an "IS NULL" query.

    Exits 1 when any case comes out the other way, and prints every case either way. The SQLite cases need a built
    ControlServer.Host for Microsoft.Data.Sqlite (Open-L2Database borrows it, as every L2 run does); without one they
    are reported as skipped and the run exits 2, so a skip is never read as a pass.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-G3JourneyV3ScriptBranches.ps1
#>
[CmdletBinding()]
param(
    [string]$HostDirectory = (Join-Path $PSScriptRoot '..\..\src\ControlServer.Host\bin\Release\net8.0\win-x64'),
    [string]$SlotFaultScenario = (Join-Path $PSScriptRoot 'scenarios\g3-slot-fault-declaration.ps1'),
    [string]$ForcedScenario = (Join-Path $PSScriptRoot 'scenarios\g3-forced-mechanical-recovery.ps1')
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
. (Join-Path $PSScriptRoot 'scenarios\G3RecoveryCommon.ps1')

$wrong = 0
$skipped = 0
function Test-Case([string]$Name, [scriptblock]$Body) {
    $failure = $null
    try { $null = & $Body } catch { $failure = $_.Exception.Message }
    if ($null -ne $failure) { $script:wrong++ }
    Write-Host ("{0}  {1}{2}" -f $(if ($null -eq $failure) { 'ok  ' } else { 'BAD ' }), $Name,
        $(if ($failure) { " -> $failure" } else { '' }))
}
function Assert-That([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function Get-Ast([string]$Path) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $Path).Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "$Path does not parse: $($errors[0].Message)" }
    return $ast
}

# --- 1. The shape of Get-L2Inbound's rows ----------------------------------------------------------------------------

$moduleAst = Get-Ast (Join-Path $PSScriptRoot 'L2RealStation.psm1')
$inbound = $moduleAst.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-L2Inbound' }, $true)
$rowKeys = @()
if ($inbound) {
    $hash = $inbound.Body.Find({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)
    if ($hash) { $rowKeys = @($hash.KeyValuePairs | ForEach-Object { [string]$_.Item1.Value }) }
}

Test-Case 'Get-L2Inbound builds rows of exactly MessageId, At, Payload, Response, ResponsePayload (read from its AST)' {
    Assert-That ($null -ne $inbound) 'no Get-L2Inbound in L2RealStation.psm1'
    Assert-That ((($rowKeys | Sort-Object) -join ',') -eq 'At,MessageId,Payload,Response,ResponsePayload') "keys: $($rowKeys -join ',')"
}

$row = [ordered]@{}
foreach ($key in $rowKeys) { $row[$key] = $null }
$row['Payload'] = [pscustomobject]@{ overallOutcome = 'UNKNOWN'; slotResults = @([pscustomobject]@{ slotNo = 1; outcome = 'UNKNOWN' }) }
$result = [pscustomobject]$row

Test-Case 'a row of that shape throws on .PayloadJson under StrictMode (the line G3-07-67 stopped on)' {
    $threw = $false
    try { & { Set-StrictMode -Version Latest; $null = $result.PayloadJson } } catch { $threw = $true }
    Assert-That $threw 'reading .PayloadJson did not throw'
}
Test-Case 'the same row serialises through Payload under StrictMode' {
    $json = & { Set-StrictMode -Version Latest; $result.Payload | ConvertTo-Json -Depth 20 -Compress }
    Assert-That ($json -like '*"overallOutcome":"UNKNOWN"*') "serialised as $json"
}

# --- 2. NULL through Invoke-L2Query and Get-G3Scalar, against a real SQLite file --------------------------------------

$sqlite = Join-Path $HostDirectory 'Microsoft.Data.Sqlite.dll'
if (-not (Test-Path -LiteralPath $sqlite)) {
    $skipped++
    Write-Host "skip  SQLite cases: no Microsoft.Data.Sqlite.dll under $HostDirectory (build src/ControlServer.Host -c Release)"
} else {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("cs555-" + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $dir
    $db = Join-Path $dir 'probe.db'
    try {
        # Writable connection only to build the file; the checks read it through Open-L2Database like a scenario does.
        foreach ($assembly in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
            $path = Join-Path $HostDirectory $assembly
            if (Test-Path -LiteralPath $path) { Add-Type -LiteralPath $path -ErrorAction SilentlyContinue }
        }
        try { [SQLitePCL.Batteries_V2]::Init() } catch { }
        $writer = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$db;Pooling=False")
        $writer.Open()
        $command = $writer.CreateCommand()
        $command.CommandText = "CREATE TABLE ExceptionRecoverySessions (ExceptionRecoverySessionId TEXT PRIMARY KEY, ClosedReason TEXT NULL);" +
            "INSERT INTO ExceptionRecoverySessions VALUES ('null-row', NULL), ('empty-row', ''), ('coded-row', 'ADMINISTRATOR_CLOSED');"
        $null = $command.ExecuteNonQuery()
        $writer.Close()

        $connection = Open-L2Database -HostDirectory $HostDirectory -DatabasePath $db
        try {
            function Read-Reason([string]$Id) { Get-G3Scalar $connection "SELECT ClosedReason AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$Id'" }
            function Read-IsNull([string]$Id) { (Get-G3Scalar $connection "SELECT ClosedReason IS NULL AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$Id'") -eq '1' }
            function Test-Old($Value) { $null -eq $Value -or $Value -is [System.DBNull] }

            Test-Case 'a NULL ClosedReason reads back through Get-G3Scalar as "" (not $null, not DBNull)' {
                $value = Read-Reason 'null-row'
                Assert-That ($value -is [string] -and $value -eq '') "read back as [$($value.GetType().Name)] '$value'"
            }
            Test-Case 'so the old G3-07-44 conjunct is false for a NULL ClosedReason -- the red it produced was the script''s' {
                Assert-That (-not (Test-Old (Read-Reason 'null-row'))) 'old test was true for NULL'
            }
            Test-Case 'ClosedReason IS NULL: true for NULL' { Assert-That (Read-IsNull 'null-row') 'false for NULL' }
            Test-Case 'ClosedReason IS NULL: false for an empty string (a NULL-or-empty test would pass it)' {
                Assert-That (-not (Read-IsNull 'empty-row')) 'true for an empty string'
                Assert-That ([string]::IsNullOrEmpty((Read-Reason 'empty-row'))) 'the NULL-or-empty test did not take the empty string, so this case shows nothing'
            }
            Test-Case 'ClosedReason IS NULL: false for a reason code' { Assert-That (-not (Read-IsNull 'coded-row')) 'true for a code' }
        } finally {
            $connection.Close()
            $connection.Dispose()
        }
    } finally {
        [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- 3. The scenarios as committed -------------------------------------------------------------------------------------

function Get-AssertionCall([System.Management.Automation.Language.Ast]$Ast, [string]$Id) {
    $Ast.Find({
            param($n)
            $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and [string]$n.Member.Value -eq 'Add' -and
            $n.Arguments.Count -ge 1 -and $n.Arguments[0] -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            $n.Arguments[0].Value -eq $Id
        }, $true)
}

$slotAst = Get-Ast $SlotFaultScenario
$g367 = Get-AssertionCall $slotAst 'G3-07-67'
Test-Case 'g3-slot-fault-declaration.ps1: G3-07-67 reads no PayloadJson off $result' {
    Assert-That ($null -ne $g367) 'no G3-07-67 assertion'
    $reads = @($g367.FindAll({
                param($n) $n -is [System.Management.Automation.Language.MemberExpressionAst] -and [string]$n.Member.Value -eq 'PayloadJson' -and
                [string]$n.Expression.Extent.Text -eq '$result' }, $true))
    Assert-That ($reads.Count -eq 0) "still reads $(@($reads | ForEach-Object { $_.Extent.Text }) -join ', ')"
}

$forcedAst = Get-Ast $ForcedScenario
$g344 = Get-AssertionCall $forcedAst 'G3-07-44'
Test-Case 'g3-forced-mechanical-recovery.ps1: G3-07-44''s condition uses $closedReasonIsNull, not a $null or DBNull test of $closedReason' {
    Assert-That ($null -ne $g344) 'no G3-07-44 assertion'
    $condition = $g344.Arguments[2].Extent.Text
    Assert-That ($condition -match '\$closedReasonIsNull\b') 'condition does not use $closedReasonIsNull'
    Assert-That ($condition -notmatch 'DBNull' -and $condition -notmatch '\$null\s+-eq\s+\$closedReason\b') "condition still tests `$closedReason against null: $condition"
}
Test-Case 'g3-forced-mechanical-recovery.ps1: $closedReasonIsNull is computed from an "IS NULL" query' {
    $assign = $forcedAst.Find({
            param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -eq '$closedReasonIsNull' }, $true)
    Assert-That ($null -ne $assign) 'no assignment to $closedReasonIsNull'
    Assert-That ($assign.Right.Extent.Text -match 'ClosedReason IS NULL AS Value') "assigned from: $($assign.Right.Extent.Text)"
}

Write-Host ''
if ($wrong -gt 0) { Write-Host "FAIL: $wrong case(s) came out the other way."; exit 1 }
if ($skipped -gt 0) { Write-Host 'INCOMPLETE: the SQLite cases were skipped.'; exit 2 }
Write-Host 'PASS'
exit 0
