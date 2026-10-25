#Requires -Version 7
# 8005-workspace#68 mutation runs: each mutation edits exactly one committed file, runs the self-test, records its
# FAIL lines, and puts that one file back with git. Run from nowhere in particular; paths are absolute.
param([Parameter(Mandatory = $true)][ValidateSet('cs', 'ws')][string] $Repo, [Parameter(Mandatory = $true)][string] $OutDirectory)
$ErrorActionPreference = 'Stop'
$cs = 'C:\work\8005-wt\ws68-8005-agv-control-server'
$ws = 'C:\work\8005-wt\ws68-8005-workspace'
$mutations = @{
    cs = @(
        @{ Name = 'M1-close-ignores-riot'; File = 'scripts/parallel/ParallelInstance.psm1'
            Old = '    if ($Riot -isnot [System.Collections.IDictionary]) { return [string[]] @(''RIOT_NOT_READ: RIoT was not read'') }'
            New = '    return [string[]] @()  # MUTATION M1: RIoT not consulted' }
        @{ Name = 'M2-open-ignores-riot'; File = 'scripts/parallel/ParallelInstance.psm1'
            Old = '    if ($Riot -isnot [System.Collections.IDictionary]) { return ''RIOT_NOT_READ: RIoT was not read'' }'
            New = '    return $null  # MUTATION M2: a CONFIRMED order passes without RIoT' }
        @{ Name = 'M3-readstate-without-riot'; File = 'scripts/parallel/Set-ParallelDispatchGateLocal.ps1'
            Old = "        Read-ParallelRiotGateFacts -State (Get-ParallelJourneyDispatchState -DatabasePath `$databasePath -AssemblyDirectory `$layout.InstallRoot -RecoveryJourneyId `$recoveryJourneyId) ``"
            New = "        (Get-ParallelJourneyDispatchState -DatabasePath `$databasePath -AssemblyDirectory `$layout.InstallRoot -RecoveryJourneyId `$recoveryJourneyId) # MUTATION M3 ``" }
    )
    ws = @(
        @{ Name = 'W1-vehicles-not-asked'; File = 'remote-ops/onboard-hmi/scripts/V2RecoveryWindow.psm1'
            Old = '    param([AllowEmptyCollection()][object[]] $Rows, [AllowNull()][object[]] $VehicleMotion)'
            New = "    param([AllowEmptyCollection()][object[]] `$Rows, [AllowNull()][object[]] `$VehicleMotion)`r`n    return  # MUTATION W1: no vehicle may be under way" }
        @{ Name = 'W2-no-read-after-stop'; File = 'remote-ops/onboard-hmi/scripts/V2RecoveryWindow.psm1'
            Old = '        $again = & $journeyCheck'
            New = '        $again = @{ Refusal = $null }  # MUTATION W2: not read again after the stop' }
        @{ Name = 'W3-close-checks-named-journey'; File = 'remote-ops/onboard-hmi/scripts/V2RecoveryWindow.psm1'
            Old = '    if ($null -eq $RecoveryJourney -or $Target -ceq ''remove'') {'
            New = '    if ($null -eq $RecoveryJourney) {  # MUTATION W3: closing judged like opening' }
    )
}
$root = ($Repo -eq 'cs') ? $cs : $ws
New-Item -ItemType Directory -Force -Path $OutDirectory | Out-Null
$summary = [System.Collections.Generic.List[string]]::new()
foreach ($m in $mutations[$Repo]) {
    $path = Join-Path $root $m.File
    $text = [IO.File]::ReadAllText($path)
    $count = ([regex]::Matches($text, [regex]::Escape($m.Old))).Count
    if ($count -ne 1) { throw "$($m.Name): the text to mutate occurs $count times in $($m.File)" }
    [IO.File]::WriteAllText($path, $text.Replace($m.Old, $m.New), [Text.UTF8Encoding]::new($false))
    try {
        $changed = @(git -C $root diff --name-only)
        if ($changed.Count -ne 1 -or $changed[0] -cne $m.File) { throw "$($m.Name): expected exactly $($m.File) changed, got: $($changed -join ', ')" }
        $out = Join-Path $OutDirectory "$($m.Name).txt"
        if ($Repo -eq 'cs') {
            & pwsh -NoProfile -File (Join-Path $cs 'scripts/parallel/Test-ParallelInstance.ps1') *> $out
        } else {
            & pwsh -NoProfile -File (Join-Path $ws 'remote-ops/onboard-hmi/scripts/Test-SetV2RecoveryWindow.ps1') -ControlServerRepository $cs *> $out
        }
        $fails = @(Select-String -LiteralPath $out -Pattern '  FAIL  ' | ForEach-Object { $_.Line.Trim() })
        $total = (Select-String -LiteralPath $out -Pattern 'passed, \d+ failed' | Select-Object -Last 1).Line
        $summary.Add("## $($m.Name) ($($m.File)): $total")
        $summary.Add("   - $($m.Old.Trim())")
        $summary.Add("   + $($m.New.Trim())")
        foreach ($f in $fails) { $summary.Add("   $f") }
    } finally {
        git -C $root checkout -- $m.File
        if (@(git -C $root diff --name-only).Count -ne 0) { throw "$($m.Name): the worktree is not clean after restoring $($m.File)" }
    }
}
$summary | Set-Content -LiteralPath (Join-Path $OutDirectory 'SUMMARY.txt') -Encoding utf8NoBOM
$summary
