#Requires -Version 7

<#
Self-check for L2MultiStopJourney.psm1's plan-leg row parsing (control-server#218). No rig, no UI Automation: it feeds
ConvertFrom-L2PlanLegRowReading the shapes a real rig produced and checks what comes back.

Why it exists: the first Name fallback carried a regex word boundary that an editing tool decoded into a literal
backspace. It parsed, it matched nothing, and a whole real-rig session read every plan row as '?' before anyone knew.
Case 5 is the direct guard for that: no control character may sit in the module at all.

Exit 0 when every case holds; throws naming the failures otherwise.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2MultiStopJourney.psm1') -Force

# What UI Automation named a JourneyPlanLegRow DataItem on the rig: the record's ToString.
$recordName = 'JourneyPlanLegRow { Sequence = 2, StationId = C15-13, StopPurposeCategory = BUSINESS, StopPurposeText = 业务站, ' +
    'LegTypeText = 取货, State = PLANNED, StateText = 待行驶, SequenceText = 2, ItemStatus = 2|BUSINESS|PLANNED }'

$cases = @(
    @{ Name = 'the sequence TextBlock is read when present'
       Texts = @('2', 'C15-13   业务站   取货', '待行驶'); RowName = $recordName; Sequence = 2; Source = 'TEXT'; Status = '2|BUSINESS|PLANNED' }
    @{ Name = 'a whitespace text first does not hide the sequence text (g3-multi-stop-plan-001)'
       Texts = @('  ', '3', '关卡'); RowName = ''; Sequence = 3; Source = 'TEXT'; Status = $null }
    @{ Name = 'no texts at all falls back to the Name (g3-multi-stop-plan-002, red-hmi-reorder-2)'
       Texts = @(); RowName = $recordName; Sequence = 2; Source = 'NAME'; Status = '2|BUSINESS|PLANNED' }
    @{ Name = 'SequenceText alone is not taken for Sequence'
       Texts = @(); RowName = 'Row { SequenceText = 3, ItemStatus = 3|BUSINESS|PLANNED }'; Sequence = $null; Source = $null; Status = '3|BUSINESS|PLANNED' }
)

$problems = [System.Collections.Generic.List[string]]::new()
foreach ($case in $cases) {
    $got = ConvertFrom-L2PlanLegRowReading -Texts $case.Texts -Name $case.RowName
    $ok = $got.Sequence -eq $case.Sequence -and $got.Source -eq $case.Source -and $got.ItemStatus -eq $case.Status
    "{0}    {1} -> Sequence={2} Source={3} ItemStatus={4}" -f $(if ($ok) { 'ok  ' } else { 'FAIL' }), $case.Name, $got.Sequence, $got.Source, $got.ItemStatus
    if (-not $ok) { $problems.Add($case.Name) }
}

$bytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'L2MultiStopJourney.psm1'))
$control = @(for ($i = 0; $i -lt $bytes.Length; $i++) { if ($bytes[$i] -lt 32 -and $bytes[$i] -notin 9, 10, 13) { $i } })
"{0}    L2MultiStopJourney.psm1 carries no control character other than tab and line ends -> {1} found" -f $(if ($control.Count -eq 0) { 'ok  ' } else { 'FAIL' }), $control.Count
if ($control.Count -ne 0) { $problems.Add("control characters at byte offsets $($control -join ', ')") }

if ($problems.Count -ne 0) { throw "L2MultiStopJourney self-check failed: $($problems -join '; ')" }
"L2MultiStopJourney self-check: all $($cases.Count + 1) cases as expected."
