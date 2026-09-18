#Requires -Version 7

<#
.SYNOPSIS
    Self-check for L2Lanes.psm1's lane plan: how CI deals its scenarios out to parallel port slots.

.DESCRIPTION
    Pure input, no rig, a second. What it holds down (control-server#130):

    - Every run of a scenario lands in one lane, in order. Three consecutive runs is a claim about one
      scenario running three times in a row; spread over three lanes they would run at the same moment and
      prove nothing about consecutiveness.
    - Every scenario is planned exactly once, and nothing is planned that was not asked for.
    - Lanes take slots 1..N and never 0: slot 0 belongs to the real-onboard rig, run-journey-g3.ps1 and every
      checkout older than the slots, and a CI lane there would queue against them.
    - Longest first onto the least loaded lane, checked on a case whose balanced answer is known by hand.
    - No empty lanes: fewer scenarios than lanes starts only as many lanes as there are scenarios.
    - A scenario with no estimate is planned as if it took 30 seconds rather than dropped or put first.

    Exits 1 when any case fails, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2Lanes.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2Lanes.psm1') -Force

$failures = @()
function Assert-Case {
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][bool]$Condition,
          [Parameter(Mandatory)][string]$Detail)
    Write-Host "$(if ($Condition) { 'PASS' } else { 'FAIL' })  $Name -- $Detail"
    if (-not $Condition) { $script:failures += $Name }
}
function Format-Plan($Plan) {
    ($Plan | ForEach-Object { "lane $($_.Lane)/slot $($_.Slot): " +
        (($_.Items | ForEach-Object { "$($_.Name)x$($_.Runs)" }) -join ',') + " ($($_.EstimateSeconds)s)" }) -join '; '
}

# A worked example: six scenarios of 10..5 seconds over three lanes. Longest first onto the least loaded
# lane gives 10+5, 9+6 and 8+7 -- fifteen seconds each -- and nothing cleverer is possible.
$items = @(10, 9, 8, 7, 6, 5 | ForEach-Object { [pscustomobject]@{ Name = "s$_"; Runs = 1; EstimateSeconds = $_ } })
$plan = @(Get-L2LanePlan -Items $items -LaneCount 3)
Assert-Case -Name 'longest-first-onto-the-least-loaded-lane' `
    -Condition ((($plan | ForEach-Object EstimateSeconds) -join ',') -eq '15,15,15' -and
        (($plan[0].Items.Name) -join ',') -eq 's10,s5' -and (($plan[1].Items.Name) -join ',') -eq 's9,s6' -and
        (($plan[2].Items.Name) -join ',') -eq 's8,s7') `
    -Detail (Format-Plan $plan)

Assert-Case -Name 'lanes-take-slots-one-to-n-never-zero' `
    -Condition ((($plan | ForEach-Object { "$($_.Lane)=$($_.Slot)" }) -join ',') -eq '1=1,2=2,3=3') `
    -Detail (Format-Plan $plan)

# A scenario's runs count toward its lane as a whole, and stay one item: the lane runs them back to back.
$items = @(
    [pscustomobject]@{ Name = 'triple'; Runs = 3; EstimateSeconds = 20 }
    [pscustomobject]@{ Name = 'long'; Runs = 1; EstimateSeconds = 50 }
    [pscustomobject]@{ Name = 'short'; Runs = 1; EstimateSeconds = 15 }
)
$plan = @(Get-L2LanePlan -Items $items -LaneCount 2)
$tripleLanes = @($plan | Where-Object { 'triple' -in $_.Items.Name })
Assert-Case -Name 'every-run-of-a-scenario-is-in-one-lane' `
    -Condition ($tripleLanes.Count -eq 1 -and @($tripleLanes[0].Items | Where-Object Name -eq 'triple').Count -eq 1 -and
        ($tripleLanes[0].Items | Where-Object Name -eq 'triple').Runs -eq 3 -and
        (($plan | ForEach-Object EstimateSeconds) -join ',') -eq '60,65') `
    -Detail (Format-Plan $plan)

$planned = @($plan | ForEach-Object { $_.Items.Name } | Sort-Object)
Assert-Case -Name 'every-scenario-is-planned-exactly-once' `
    -Condition (($planned -join ',') -eq 'long,short,triple') `
    -Detail "planned: $($planned -join ', ')"

$plan = @(Get-L2LanePlan -Items @([pscustomobject]@{ Name = 'only'; Runs = 1; EstimateSeconds = 40 }) -LaneCount 4)
Assert-Case -Name 'no-empty-lanes' `
    -Condition ($plan.Count -eq 1 -and $plan[0].Slot -eq 1) `
    -Detail (Format-Plan $plan)

$items = @(
    [pscustomobject]@{ Name = 'unknown'; Runs = 1 }
    [pscustomobject]@{ Name = 'known'; Runs = 1; EstimateSeconds = 31 }
)
$plan = @(Get-L2LanePlan -Items $items -LaneCount 1)
Assert-Case -Name 'a-scenario-without-an-estimate-counts-thirty-seconds' `
    -Condition ((($plan[0].Items.Name) -join ',') -eq 'known,unknown' -and $plan[0].EstimateSeconds -eq 61) `
    -Detail (Format-Plan $plan)

$refused = $false
try { $null = Get-L2LanePlan -Items $items -LaneCount 5 } catch { $refused = $true }
Assert-Case -Name 'no-more-lanes-than-slots' -Condition $refused -Detail 'LaneCount 5 is refused: there are four slots besides slot 0'

# Lanes start several pwsh processes at the same moment, and every one of them imports L2.psm1 before it
# does anything. A PowerShell class is compiled when its module is parsed, so every type it names must
# already be loaded then -- and Microsoft.PowerShell.Commands.* types live in module assemblies pwsh loads
# lazily. Under the lanes' concurrent start-up that lost the race: 3 of 30 runs on 2026-09-18 died on
# "ParserError: Unable to find type [Microsoft.PowerShell.Commands.HttpResponseException]" before their
# scenario began, and 4 of 48 concurrent bare imports reproduced it. So no class in an L2 module may name
# such a type; a method that needs one checks it at run time instead.
$lazyTypes = @(@(foreach ($module in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.psm1') {
    $ast = [Management.Automation.Language.Parser]::ParseFile($module.FullName, [ref]$null, [ref]$null)
    $classes = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.TypeDefinitionAst] }, $true)
    foreach ($class in $classes) {
        $class.FindAll({ param($node) $node -is [Management.Automation.Language.TypeConstraintAst] -or
                $node -is [Management.Automation.Language.TypeExpressionAst] -or
                $node -is [Management.Automation.Language.CatchClauseAst] }, $true) |
            ForEach-Object {
                $names = if ($_ -is [Management.Automation.Language.CatchClauseAst]) { $_.CatchTypes.TypeName.FullName } else { $_.TypeName.FullName }
                foreach ($name in $names) {
                    if ($name -like 'Microsoft.PowerShell.Commands.*') { "$($module.Name) class $($class.Name): [$name]" }
                }
            }
    }
}) | Select-Object -Unique)
Assert-Case -Name 'no-l2-class-names-a-lazily-loaded-type' `
    -Condition ($lazyTypes.Count -eq 0) `
    -Detail $(if ($lazyTypes.Count -eq 0) { 'no class in scripts/l2/*.psm1 names a Microsoft.PowerShell.Commands.* type' } else { $lazyTypes -join '; ' })

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("L2_LANES_SELFTEST_FAILED: $($failures -join ', ')")
    exit 1
}
Write-Host 'L2_LANES_SELFTEST_PASSED'
exit 0
