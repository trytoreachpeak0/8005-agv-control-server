#Requires -Version 7
Set-StrictMode -Version Latest

<#
The machine's real-rig run ledger: one place that says which real-onboard runs happened on this
machine, when each started and ended, and which three commits each one bound.

Why it exists. Until control-server#203 there was no ledger in the repository at all -- the one that
control-server#164 worked from was a file that implementation session created for itself, and its
wrapper scripts appended to it. Two background batches ran at once and it lost two lines: 2b's exit
and 3b's entry (PR #176, "evidence identity correction"). A lost line is worse than no ledger,
because the runs around it still look accounted for.

So the writer moved into `Invoke-L2Scenario.ps1`, which is the one thing every real-rig run goes
through however it was started -- by hand, from a wrapper, from CI. A wrapper that forgets to append
cannot exist if no wrapper appends.

Concurrency. A real-rig run holds the machine-wide desktop lock for its whole length, so two of them
cannot overlap on this machine today, and that alone would make a naive append look correct. This
appends safely anyway: opening the file exclusively for each line, and retrying when someone else
holds it. The ledger's whole job is to be trustworthy about what ran concurrently -- resting that on
"the caller happens to hold a lock that is not this file's" would make it a record whose accuracy
depends on a rule written somewhere else. `Test-L2RunLedger.ps1` runs two processes at it.

Format is NDJSON, one object per line, because that is what survives being appended to from several
processes: a partial write is one broken line rather than a corrupt file, and a reader can say which
line it could not parse.

The file lives outside the repository (`%LOCALAPPDATA%\8005-l2\rig-runs.log` by default) -- it is a
record of this machine, not of this checkout, and it has to outlive worktrees being removed.
#>

<#
Where the ledger lives. `W2G_L2_RUN_LEDGER` overrides it, which is how the self-check gets a
throwaway path; nothing else sets it.
#>
function Get-L2RunLedgerPath {
    [CmdletBinding()]
    param()

    if (-not [string]::IsNullOrWhiteSpace($env:W2G_L2_RUN_LEDGER)) { return $env:W2G_L2_RUN_LEDGER }
    $root = if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { [System.IO.Path]::GetTempPath() } else { $env:LOCALAPPDATA }
    return (Join-Path $root '8005-l2\rig-runs.log')
}

<#
Appends one line, whole or not at all.

`FileShare.Read` is the point: a second writer cannot open the file while this one has it, so two
processes serialise here rather than interleaving inside one line. The loser gets an IOException and
comes back, with a jittered backoff so two processes that collided do not keep colliding.

Returns $true when the line was written. A caller that could not write the ledger has still run its
scenario, so this never throws -- the run's verdict does not depend on the ledger, and a failed
append that killed the run would be the ledger costing more than it is worth.
#>
function Add-L2RunLedgerLine {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Line,
        [int]$AttemptLimit = 50)

    if ($Line.Contains("`n") -or $Line.Contains("`r")) {
        throw 'A ledger line cannot contain a line break; one record is one line.'
    }
    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory) -and -not (Test-Path -LiteralPath $directory)) {
        $null = New-Item -ItemType Directory -Path $directory -Force -ErrorAction SilentlyContinue
    }
    # UTF-8 without a BOM, written once per line: appending a BOM mid-file would corrupt the record
    # for every reader (and the encoder would emit one on each open if it were asked to).
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $bytes = $encoding.GetBytes($Line + "`n")
    for ($attempt = 1; $attempt -le $AttemptLimit; $attempt++) {
        try {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Append,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::Read)
            try {
                $stream.Write($bytes, 0, $bytes.Length)
                $stream.Flush()
            } finally { $stream.Dispose() }
            return $true
        } catch [System.IO.IOException] {
            if ($attempt -eq $AttemptLimit) { return $false }
            Start-Sleep -Milliseconds (Get-Random -Minimum 5 -Maximum 25)
        } catch {
            return $false
        }
    }
    return $false
}

<#
One run's entry or exit, as the ledger records it. `Fields` carries whatever that end of the run
knows; `at`, `event` and `runId` are always present so a reader can pair the two lines up.
#>
function Write-L2RunLedgerEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('start', 'end')][string]$Event,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][hashtable]$Fields,
        [string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { $Path = Get-L2RunLedgerPath }
    $record = [ordered]@{
        at    = [DateTimeOffset]::UtcNow.ToString('o')
        event = $Event
        runId = $RunId
        pid   = $PID
    }
    foreach ($key in @($Fields.Keys | Sort-Object)) { $record[$key] = $Fields[$key] }
    $line = ($record | ConvertTo-Json -Compress -Depth 4)
    if (-not (Add-L2RunLedgerLine -Path $Path -Line $line)) {
        Write-Warning "Could not append to the real-rig run ledger at $Path; this run is not recorded there."
        return $false
    }
    return $true
}

Export-ModuleMember -Function Get-L2RunLedgerPath, Add-L2RunLedgerLine, Write-L2RunLedgerEvent
