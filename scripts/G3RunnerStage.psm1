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
# the way New-ExactClone needs: an excluded tracked file is skip-worktree and not reported, but an untracked file
# under evidence/ is, and so is any change to a checked-out file.
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

# After a runner has written its run-result.json: a passed run's stage goes -- clones, builds, publishes, peers and
# runtime, all rebuilt from the commits -- and a red run's stays for whoever reads the failure. The evidence root is
# a different directory and is never touched. A stage that will not go (a file still held open) is a warning and
# removed=false, never a change to the verdict; nothing here unlocks it with `dotnet build-server shutdown`, which would
# stop every other session's builds on the machine.
function Complete-G3RunnerStage {
    param(
        [Parameter(Mandatory)][string]$StageRoot,
        [Parameter(Mandatory)][bool]$Passed,
        [int]$Attempts = 3,
        [double]$RetrySeconds = 5
    )

    if (-not $Passed) {
        Write-Host "G3_STAGE_KEPT: $StageRoot (the run did not pass)"
        return [pscustomobject]@{ removed = $false; reason = 'NOT_PASSED' }
    }
    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts -and (Test-Path -LiteralPath $StageRoot); $attempt++) {
        try { Remove-Item -LiteralPath $StageRoot -Recurse -Force -ErrorAction Stop }
        catch {
            $lastError = $_.Exception.Message
            if ($attempt -lt $Attempts) { Start-Sleep -Seconds $RetrySeconds }
        }
    }
    if (-not (Test-Path -LiteralPath $StageRoot)) {
        Write-Host "G3_STAGE_REMOVED: $StageRoot"
        return [pscustomobject]@{ removed = $true; reason = $null }
    }
    $left = @(Get-ChildItem -LiteralPath $StageRoot -Recurse -Force -File -ErrorAction Ignore)
    $gib = [Math]::Round((($left | Measure-Object Length -Sum).Sum) / 1GB, 2)
    Write-Warning "G3_STAGE_NOT_REMOVED: $StageRoot keeps $($left.Count) files, $gib GiB, after $Attempts attempts: $lastError The verdict stands; remove it once whatever holds it has exited."
    return [pscustomobject]@{ removed = $false; reason = 'NOT_REMOVED' }
}

Export-ModuleMember -Function Get-G3EvidenceSparseCheckoutArguments, Assert-G3FreeDiskSpace, Complete-G3RunnerStage
