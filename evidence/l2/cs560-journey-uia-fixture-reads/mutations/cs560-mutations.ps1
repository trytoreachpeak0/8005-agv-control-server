#Requires -Version 7
# control-server#560 mutation and pre-fix runs of Test-G3JourneyUiaFixtureReads.ps1, each in its own copy of scripts/l2.
param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$null = New-Item -ItemType Directory -Path $Out -Force
$mutations = @(
    @{ Id = 'M1'; File = 'scenarios/MultiStopRigCommon.ps1'; Expect = 'A2'; What = 'drop the pre-submit read-back check'
       From = @'
        $Journal.Note("ScanTextBox read back just before submit ($Label): $(Format-L2ScanText $text).")
        if ($text -cne $Sublot) {
'@; To = @'
        $Journal.Note("ScanTextBox read back just before submit ($Label): $(Format-L2ScanText $text).")
        if ($false) {
'@ }
    @{ Id = 'M2'; File = 'scenarios/MultiStopRigCommon.ps1'; Expect = 'A4'; What = 'swallow a refusal instead of failing'
       From = '    if ($null -ne $outcome.Refusal) {'; To = '    if ($null -ne $outcome.Refusal -and $false) {' }
    @{ Id = 'M3'; File = 'scenarios/MultiStopRigCommon.ps1'; Expect = 'A4'; What = 'take a refusal logged before the submit'
       From = ' -or $at -le $After) { continue }'; To = ') { continue }' }
    @{ Id = 'M4'; File = 'L2MultiStopJourney.psm1'; Expect = 'B4'; What = 'treat an empty read value as unreadable and re-read'
       From = '            if ($element) {'; To = '            if ($element -and [string]$element.Current.ItemStatus) {' }
    @{ Id = 'M5'; File = 'L2MultiStopJourney.psm1'; Expect = 'B1'; What = 'no re-read: one read only'
       From = '    for ($read = 1; $read -le $Attempts; $read++) {'; To = '    for ($read = 1; $read -le 1; $read++) {' }
    @{ Id = 'M6'; File = 'scenarios/g3-waiting-point-idle-return.ps1'; Expect = 'B6'; What = 'held window ignores an unreadable reading'
       From = '-Until { param($v) -not $v.Readable -or $v.Status -ne $atPoint -or $v.CanSubmit }'
       To = '-Until { param($v) ($v.Readable -and $v.Status -ne $atPoint) -or $v.CanSubmit }' }
    @{ Id = 'M7'; File = 'scenarios/g3-waiting-point-idle-return.ps1'; Expect = 'B7'; What = 'G3-12-07 back on the first reading'
       From = '($heldReading.Readable -and $heldAtPoint -eq $atPoint -and -not $heldReading.CanSubmit'
       To = '($shownAtPoint -eq $atPoint -and -not $heldReading.CanSubmit' }
)
$source = Join-Path $Repo 'scripts/l2'
foreach ($m in $mutations) {
    $copy = Join-Path $Out "$($m.Id)/scripts/l2"
    $null = New-Item -ItemType Directory -Path (Split-Path $copy) -Force
    Copy-Item -LiteralPath $source -Destination $copy -Recurse
    $path = Join-Path $copy $m.File
    $text = [IO.File]::ReadAllText($path)
    $from = $m.From -replace "`r?`n", "`r`n"
    $to = $m.To -replace "`r?`n", "`r`n"
    if (-not $text.Contains($from)) { $from = $m.From -replace "`r?`n", "`n"; $to = $m.To -replace "`r?`n", "`n" }
    if (-not $text.Contains($from)) { throw "$($m.Id): pattern not found in $($m.File)" }
    [IO.File]::WriteAllText($path, $text.Replace($from, $to), [Text.UTF8Encoding]::new($false))
    $output = & pwsh -NoProfile -File (Join-Path $copy 'Test-G3JourneyUiaFixtureReads.ps1') 2>&1
    $exit = $LASTEXITCODE
    $bad = @($output | Where-Object { "$_" -like 'BAD*' } | ForEach-Object { ("$_" -split '\s+')[1] })
    $killed = $exit -eq 1 -and $m.Expect -in $bad
    $output | Set-Content -LiteralPath (Join-Path $Out "$($m.Id).txt")
    "{0} {1,-6} exit={2} expected {3} red; red: {4}  ({5})" -f $m.Id, $(if ($killed) { 'KILLED' } else { 'ALIVE' }), $exit, $m.Expect, ($bad -join ','), $m.What
    Remove-Item -LiteralPath (Join-Path $Out $m.Id) -Recurse -Force
}
