#Requires -Version 7
# cs#578 mutation run: each mutation breaks one check; the named cases must turn red. Files are committed, restored with git.
param([Parameter(Mandatory = $true)][string] $Repo, [Parameter(Mandatory = $true)][string] $Out)
$ErrorActionPreference = 'Stop'
Set-Location $Repo
$p = 'scripts/parallel'
$mutations = @(
    @{ Id = 'M01'; File = "$p/ParallelHost.psm1"; What = 'configuration step does not hold the runtime off before the restart'
        From = '    $null = Set-ParallelInstanceJourneyRuntimeDisabled -Path $ConfigurationPath' + "`n" + '    $null = & $Actions.RestartService'
        To = '    $null = & $Actions.RestartService'
        Expect = @('first install: restarted with the runtime off', 'upgrade: restarted with the runtime off', 'rollback: restarted with the runtime off') }
    @{ Id = 'M02'; File = "$p/ParallelInstance.psm1"; What = 'held judge never asks for event 2001'
        From = '    if (-not $disabledSeen) {'; To = '    if ($false) {'
        Expect = @('held read-back: no event 2001 since the start', 'first install: phase one with enabled overridden to true, read before', 'upgrade: phase one with enabled overridden to true, read before', 'rollback: phase one with enabled overridden to true, read before') }
    @{ Id = 'M03'; File = "$p/ParallelInstance.psm1"; What = 'held judge ignores activity'
        From = '    if ($activity.Count -gt 0) {'; To = '    if ($false) {'
        Expect = @('held read-back: event 2101', 'held read-back: event 2002', 'held read-back: event 100', 'held read-back: event 2240', 'held read-back: event 2171') }
    @{ Id = 'M04'; File = "$p/ParallelInstance.psm1"; What = 'read-back decision ignores the held refusal'
        From = '    if ($heldRefusal) {' + "`n" + '        $verdict = [pscustomobject]@{ Action = ''StopServiceAndRefuse'''
        To = '    if ($false) {' + "`n" + '        $verdict = [pscustomobject]@{ Action = ''StopServiceAndRefuse'''
        Expect = @('first install: phase one with enabled overridden', 'upgrade: phase one with enabled overridden', 'rollback: phase one with enabled overridden') }
    @{ Id = 'M05'; File = "$p/ParallelHost.psm1"; What = 'release does not wait for the phase-one process'
        From = '    if (-not (& $Actions.ProcessExited ([int] $heldProcess))) {'; To = '    if ($false) {'
        Expect = @('held: the phase-one process has not exited') }
    @{ Id = 'M06'; File = "$p/ParallelHost.psm1"; What = 'release skips the second read-back'
        From = '    $null = & $Actions.ReadBackReleased'; To = '    $null = $null'
        Expect = @('first install: a second read-back that does not match', 'upgrade: a second read-back that does not match', 'rollback: a second read-back that does not match') }
    @{ Id = 'M07'; File = "$p/ParallelHost.psm1"; What = 'release skips the held read-back'
        From = '    $null = & $Actions.ReadBackHeld'; To = '    $null = $null'
        Expect = @('first install: phase one with enabled overridden', 'upgrade: phase one with enabled overridden', 'rollback: phase one with enabled overridden') }
    @{ Id = 'M08'; File = "$p/Install-ParallelInstanceLocal.ps1"; What = 'rollback path reads back once with the runtime on, no release'
        From = "        # off, and only then the runtime opened and read back again (control-server#578).`r`n        Invoke-JourneyRuntimeRelease"
        To = "        # off, and only then the runtime opened and read back again (control-server#578).`r`n        `$null = Assert-EffectiveConfiguration -Phase Released"
        Expect = @('wiring: the runtime is released on the install path and on the rollback path', 'wiring: no read-back runs outside the release') }
    @{ Id = 'M09'; File = "$p/Install-ParallelInstanceLocal.ps1"; What = 'install path (first install and upgrade) reads back once with the runtime on, no release'
        From = "        # journey runtime held off, and only then the runtime opened and read back again (control-server#578).`r`n        Invoke-JourneyRuntimeRelease"
        To = "        # journey runtime held off, and only then the runtime opened and read back again (control-server#578).`r`n        `$null = Assert-EffectiveConfiguration -Phase Released"
        Expect = @('wiring: the runtime is released on the install path and on the rollback path', 'wiring: no read-back runs outside the release') }
    @{ Id = 'M10'; File = "$p/Install-ParallelInstanceLocal.ps1"; What = 'ProcessExited action answers yes without waiting'
        From = 'ProcessExited = { param([int] $Id) $null = Wait-Process -Id $Id -Timeout 60 -ErrorAction SilentlyContinue; $null -eq (Get-Process -Id $Id -ErrorAction SilentlyContinue) }'
        To = 'ProcessExited = { param([int] $Id) $true }'
        Expect = @('wiring: each release follows Set-InstanceConfiguration') }
    @{ Id = 'M11'; File = "$p/Install-ParallelInstanceLocal.ps1"; What = 'installer read-back judges the binding alone, not through the phased decision'
        From = '            $verdict = Get-ParallelReadBackAction -Phase $Phase -Definition $definition -Lines @($lines) -Since $since'
        To = '            $verdict = Get-ParallelEffectiveConfigurationAction -Definition $definition -Effective (Find-ParallelEffectiveConfiguration -Lines @($lines) -Since $since)'
        Expect = @('wiring: the installer''s read-back decides through Get-ParallelReadBackAction') }
    @{ Id = 'M12'; File = "$p/ParallelInstance.psm1"; What = 'held judge counts lines from an earlier process'
        From = "        if (`$at -lt `$Since) { continue }`n        `$source = [string] `$event['SourceContext']"
        To = "        `$source = [string] `$event['SourceContext']"
        Expect = @('held read-back: an activity event from an earlier process does not count', 'held read-back: event 2001 only from an earlier process') }
    @{ Id = 'M13'; File = "$p/ParallelHost.psm1"; What = 'second read-back refusal leaves enabled=true'
        From = '        throw "$($_.Exception.Message) $(& $setBackFalse)"'; To = '        throw $_.Exception.Message'
        Expect = @('first install: a second read-back that does not match', 'upgrade: a second read-back that does not match', 'rollback: a second read-back that does not match') }
    @{ Id = 'M14'; File = "$p/ParallelHost.psm1"; What = 'configuration step ignores JourneyRuntime environment keys'
        From = '    if ($overrides.Count -gt 0) {'; To = '    if ($false) {'
        Expect = @('first install: JourneyRuntime__Enabled in the service', 'upgrade: JourneyRuntime__Enabled in the service', 'rollback: JourneyRuntime__Enabled in the service', 'first install: a DOTNET_-prefixed', 'upgrade: a DOTNET_-prefixed', 'rollback: a DOTNET_-prefixed') }
    @{ Id = 'M15'; File = "$p/ParallelInstance.psm1"; What = 'environment override ignores the DOTNET_/ASPNETCORE_ prefixes'
        From = '(?:DOTNET_|ASPNETCORE_)?JourneyRuntime'; To = 'JourneyRuntime'
        Expect = @('environment override: only JourneyRuntime keys count', 'first install: a DOTNET_-prefixed', 'upgrade: a DOTNET_-prefixed', 'rollback: a DOTNET_-prefixed') }
    @{ Id = 'M16'; File = "$p/Install-ParallelInstanceLocal.ps1"; What = 'installer computes the machine-level override but does not throw it'
        From = '    if ($environmentOverrides.Count -gt 0) {'; To = '    if ($false) {'
        Expect = @('wiring: a JourneyRuntime environment key found before the product script is thrown') }
    @{ Id = 'M17'; File = "$p/Install-ParallelInstanceLocal.ps1"; What = 'read-back refusal without the log file facts'
        From = '$where $logFacts $serviceName'; To = '$where $serviceName'
        Expect = @('wiring: a read-back refusal carries the log files') }
)
$report = [System.Collections.Generic.List[string]]::new()
foreach ($m in $mutations) {
    $text = [IO.File]::ReadAllText((Join-Path $Repo $m.File))
    $count = ([regex]::Matches($text, [regex]::Escape($m.From))).Count
    if ($count -ne 1) { $report.Add("$($m.Id) NOT APPLIED: anchor found $count times in $($m.File)"); continue }
    [IO.File]::WriteAllText((Join-Path $Repo $m.File), $text.Replace($m.From, $m.To), [Text.UTF8Encoding]::new($false))
    try {
        $output = & pwsh -NoProfile -File "$p/Test-ParallelInstance.ps1" 2>&1 | ForEach-Object { [string] $_ }
        $exit = $LASTEXITCODE
    } finally {
        git checkout -- $m.File
    }
    $failed = @($output | Where-Object { $_ -cmatch '^\s+FAIL\s{2}' } | ForEach-Object { ($_ -creplace '^\s+FAIL\s{2}', '').Trim() })
    $missing = @($m.Expect | Where-Object { $e = $_; -not ($failed | Where-Object { $_.StartsWith($e, [StringComparison]::Ordinal) }) })
    $summary = ($output | Where-Object { $_ -cmatch '^\d+ passed, \d+ failed' } | Select-Object -Last 1)
    $verdict = ($exit -ne 0 -and $missing.Count -eq 0) ? 'KILLED' : 'SURVIVED'
    $report.Add("$($m.Id) $verdict -- $($m.What) [$summary, exit $exit]")
    foreach ($f in $failed) { $report.Add("      red: $f") }
    foreach ($x in $missing) { $report.Add("      EXPECTED BUT NOT RED: $x") }
}
$report | Set-Content -LiteralPath $Out -Encoding utf8NoBOM
$report
