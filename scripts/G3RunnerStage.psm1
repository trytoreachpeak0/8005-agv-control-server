#Requires -Version 7

# The four G3 runners' stage handling (control-server#585): what they clone, whether there is room, and what they
# leave behind. Kept here, not in each runner, so that scripts/Test-G3RunnerStage.ps1 can check it offline.

# The sparse-checkout patterns of a ControlServer exact clone: everything but the top-level evidence/. On 2026-10-20
# evidence/ was 92,337 of this repository's 94,099 files and about 2.2 GB of its checkout; leaving it out took a
# clone's checkout from 40 seconds to 1 and its size to 151 MB. No G3 runner, nor anything one starts (the L2 rig,
# its scenarios, the builds), reads evidence/ out of a clone: they name it in comments and error text only (checked for
# control-server#585, see its PR).
#
# Non-cone, since a cone names what to include and this names the one directory to leave out. Set between the
# --no-checkout clone and the checkout, so the excluded files are never written. git status under it stays strict in
# the way New-ExactClone needs: an excluded tracked file is skip-worktree and, while absent, not reported; an untracked
# file under evidence/ is reported, so is a file written at an excluded tracked path (' M' under git 2.56), and so is
# any change to a checked-out file.
#
# Run it from pwsh. Git Bash rewrites '/*' and '!/evidence/' into Windows paths before git sees them, and the result
# is a sparse checkout that leaves nothing out.
function Get-G3EvidenceSparseCheckoutArguments {
    return @('sparse-checkout', 'set', '--no-cone', '/*', '!/evidence/')
}

# Stops a runner before it creates its stage when the drive under it has less than MinFreeGiB free (G3_DISK_LOW).
# The batch-8 exit filled C: with G3 stages; the exit rule since is "at least 30 GB free on C:" before a G3 round.
function Assert-G3FreeDiskSpace {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][double]$MinFreeGiB
    )

    # The stage does not exist yet, and a relative one is relative to the current location, which is not the process
    # directory [IO.Path]::GetFullPath would use.
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $free = [Math]::Round(([IO.DriveInfo]::new($root)).AvailableFreeSpace / 1GB, 2)
    if ($free -lt $MinFreeGiB) {
        throw "G3_DISK_LOW: $free GiB free on $root, below -MinFreeGiB $MinFreeGiB. Nothing was started; free space, or pass a lower -MinFreeGiB."
    }
    Write-Host "G3 disk: $free GiB free on $root (floor $MinFreeGiB GiB)"
    return [double]$free
}

# Before a runner creates either root: the evidence root may not be the stage, inside it, or around it, since a passed
# run's stage is removed and would take run-result.json with it (G3_EVIDENCE_IN_STAGE; review S2 of PR #806).
function Assert-G3EvidenceOutsideStage {
    param(
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][string]$EvidenceRoot
    )

    $resolve = { param($path)
        [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)).TrimEnd('\', '/') + '\' }
    $stage = & $resolve $StageRoot
    $evidence = & $resolve $EvidenceRoot
    if ($evidence.StartsWith($stage, [StringComparison]::OrdinalIgnoreCase) -or $stage.StartsWith($evidence, [StringComparison]::OrdinalIgnoreCase)) {
        throw "G3_EVIDENCE_IN_STAGE: -EvidenceRoot $EvidenceRoot and -StageRoot $StageRoot overlap ($evidence, $stage). A passed run removes its stage; give the evidence a directory of its own."
    }
}

# The hidden pwsh that removes a stage once the process that loaded files out of it has exited. It deletes only when
# that process is gone: a process id that now names another process (a different start time), or a wait past
# TimeoutSeconds, keeps the stage and says so in <stage>.removal.log beside it (G3_STAGE_REMOVAL_SKIPPED; review S1 of
# PR #806). The command line is encoded, so g3.yml's leftover-process step finds no work-root path in it.
function Start-G3StageRemovalAfterExit {
    param(
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][DateTime]$ProcessStartTimeUtc,
        [int]$TimeoutSeconds = 3600
    )

    $quoted = $StageRoot.Replace("'", "''")
    $script = @"
`$stage = '$quoted'
`$log = `$stage + '.removal.log'
`$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
while (`$true) {
    `$process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (`$null -eq `$process) { break }
    if (`$process.StartTime.ToUniversalTime().Ticks -ne $($ProcessStartTimeUtc.ToUniversalTime().Ticks)) {
        "G3_STAGE_REMOVAL_SKIPPED: pid $ProcessId is now another process (started `$(`$process.StartTime.ToUniversalTime().ToString('o')), expected $($ProcessStartTimeUtc.ToUniversalTime().ToString('o'))); `$stage kept." | Add-Content -LiteralPath `$log
        exit 1
    }
    if ([DateTime]::UtcNow -gt `$deadline) {
        "G3_STAGE_REMOVAL_SKIPPED: pid $ProcessId still running after $TimeoutSeconds s; `$stage kept." | Add-Content -LiteralPath `$log
        exit 1
    }
    Start-Sleep -Milliseconds 500
}
for (`$i = 0; `$i -lt 30 -and (Test-Path -LiteralPath `$stage); `$i++) {
    Remove-Item -LiteralPath `$stage -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath `$stage) { Start-Sleep -Seconds 2 }
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    return Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded `
        -WindowStyle Hidden -PassThru
}

# After a runner has written its run-result.json: a passed run's stage goes -- clones, builds, publishes, peers and
# runtime, all rebuilt from the commits -- and a red run's stays for whoever reads the failure. The evidence root is
# a different directory (Assert-G3EvidenceOutsideStage) and is never touched. A stage that will not go (a file still
# held open) is a warning and removed=false, never a change to the verdict; nothing here unlocks it with `dotnet build-server shutdown`, which would
# stop every other session's builds on the machine.
function Complete-G3RunnerStage {
    param(
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][bool]$Passed,
        [int]$Attempts = 3,
        [double]$RetrySeconds = 5
    )

    # Every runner resolves its stage root before it gets here; a relative one would be removed relative to wherever
    # this happens to be standing.
    if (-not [IO.Path]::IsPathFullyQualified($StageRoot)) { throw "Complete-G3RunnerStage: -StageRoot $StageRoot is not an absolute path; nothing removed." }
    if (-not $Passed) {
        Write-Host "G3_STAGE_KEPT: $StageRoot (the run did not pass)"
        return [pscustomobject]@{ removed = $false; reason = 'NOT_PASSED' }
    }
    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts -and (Test-Path -LiteralPath $StageRoot); $attempt++) {
        # Everything that can go, each attempt: one held file must not keep the gigabytes around it.
        $failed = $null
        Remove-Item -LiteralPath $StageRoot -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable failed
        if ($failed) { $lastError = $failed[-1].Exception.Message }
        if ((Test-Path -LiteralPath $StageRoot) -and $attempt -lt $Attempts) { Start-Sleep -Seconds $RetrySeconds }
    }
    if (-not (Test-Path -LiteralPath $StageRoot)) {
        Write-Host "G3_STAGE_REMOVED: $StageRoot"
        return [pscustomobject]@{ removed = $true; reason = $null }
    }
    $left = @(Get-ChildItem -LiteralPath $StageRoot -Recurse -Force -File -ErrorAction Ignore)

    # staged, restart and demand-bearing read their databases through Add-Type -Path on the Microsoft.Data.Sqlite.dll in
    # their own publish directory, and a .NET process never unloads it or e_sqlite3.dll (g3.yml run 39436562681). When
    # what is left is only what this process has loaded, a hidden pwsh removes the stage once this process has exited.
    $loaded = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($module in [Diagnostics.Process]::GetCurrentProcess().Modules) { $null = $loaded.Add($module.FileName) }
    if ($left.Count -gt 0 -and @($left | Where-Object { -not $loaded.Contains($_.FullName) }).Count -eq 0) {
        $me = [Diagnostics.Process]::GetCurrentProcess()
        $remover = Start-G3StageRemovalAfterExit -StageRoot $StageRoot -ProcessId $me.Id -ProcessStartTimeUtc $me.StartTime.ToUniversalTime()
        Write-Host ("G3_STAGE_REMOVED_AFTER_EXIT: $StageRoot -- all but $($left.Count) file(s) this process has loaded " +
            "($(@($left | ForEach-Object Name) -join ', ')); pid $($remover.Id) removes the rest once pid $($me.Id) has exited " +
            "(or says why not in $StageRoot.removal.log).")
        return [pscustomobject]@{ removed = $false; reason = 'HELD_BY_THIS_PROCESS' }
    }
    $gib = [Math]::Round((($left | Measure-Object Length -Sum).Sum) / 1GB, 2)
    Write-Warning "G3_STAGE_NOT_REMOVED: $StageRoot keeps $($left.Count) files, $gib GiB, after $Attempts attempts: $lastError The verdict stands; remove it once whatever holds it has exited."
    return [pscustomobject]@{ removed = $false; reason = 'NOT_REMOVED' }
}

Export-ModuleMember -Function Get-G3EvidenceSparseCheckoutArguments, Assert-G3FreeDiskSpace, Assert-G3EvidenceOutsideStage,
    Start-G3StageRemovalAfterExit, Complete-G3RunnerStage
