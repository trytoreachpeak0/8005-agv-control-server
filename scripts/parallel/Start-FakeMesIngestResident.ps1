#Requires -Version 7

<#
.SYNOPSIS
    Runs ControlServer.FakeMesIngest as the resident demand source for the v2 parallel
    instance, and re-seeds its catalog every time it starts.

.DESCRIPTION
    control-server#262. The parallel instance must never read the production MesIngest, so it
    reads this double instead for the whole parallel period. Three properties of the double
    make plain "start the exe" the wrong answer for a machine nobody watches:

      * it is a console ASP.NET Core application with no UseWindowsService, so the service
        control manager would kill it for never reporting in -- the same reason the dashboard
        runs as a scheduled task (Install-ControlServerLocal.ps1);
      * its catalog lives in memory. A restart empties it, and an empty catalog is
        indistinguishable from a quiet shift: no error, no alarm, nothing in a dashboard. So
        the catalog is re-seeded by this script on every start rather than being something an
        operator remembers to redo;
      * it refuses to listen anywhere but loopback unless told otherwise, which is exactly
        right here and must stay that way -- the server that reads it runs on the same host.

    This script is the scheduled task's action. It lives as long as the double does: the task's
    restart policy therefore applies to the double, and a non-zero exit is a real failure
    rather than a script that finished its work and left.

.PARAMETER ExecutablePath
    ControlServer.FakeMesIngest.exe, published self-contained.

.PARAMETER Port
    Loopback port to listen on. Must agree with MesIngest:baseUrl in the parallel instance's
    appsettings.Production.json; Install-ParallelInstanceLocal.ps1 derives both from the same
    instance definition.

.PARAMETER SeedPath
    JSON holding the demands to inject after every start. Missing or empty means an empty
    catalog, which is the safe state: a demand nobody asked for makes "no demand was accepted"
    impossible to assert.

.PARAMETER LogPath
    Where the double's own stdout/stderr and this script's progress go. A scheduled task has
    no console.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $ExecutablePath,
    [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int] $Port,
    [string] $SeedPath,
    [Parameter(Mandatory = $true)][string] $LogPath,
    [ValidateRange(5, 600)][int] $ReadyTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

function Write-Log {
    param([string] $Message)
    $line = '{0} {1}' -f [DateTimeOffset]::Now.ToString('O'), $Message
    $directory = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    [IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Write-Host $line
}

# The plain-text reason this script exists at all, restated where an operator reading the log
# after an incident will see it.
Write-Log "FakeMesIngest resident starting: exe=$ExecutablePath port=$Port seed=$SeedPath"

if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    Write-Log "FATAL: executable not found at $ExecutablePath"
    exit 3
}

$baseUrl = "http://127.0.0.1:$Port"
$workingDirectory = Split-Path -Parent $ExecutablePath
$stdoutPath = "$LogPath.out"
$stderrPath = "$LogPath.err"

# listenAddress is passed explicitly even though 127.0.0.1 is the default: this host carries a
# production service and a CI-reachable internal switch, and a default that quietly changed
# would put the double on both.
$arguments = @(
    "--FakeMesIngest:listenAddress=127.0.0.1"
    "--FakeMesIngest:port=$Port"
)

$process = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments `
    -WorkingDirectory $workingDirectory -NoNewWindow -PassThru `
    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
Write-Log "Started pid $($process.Id)"

try {
    # ------------------------------------------------------------------- ready ---

    # Invoke-WebRequest, not curl.exe: Windows Server 2016 does not ship curl, and four calls
    # to it are what broke the MVP's first deployment on this machine.
    $deadline = [datetime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    $ready = $false
    while ([datetime]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            Write-Log "FATAL: the double exited during startup with code $($process.ExitCode)"
            Write-Log ("stderr: " + (Get-Content -LiteralPath $stderrPath -Raw -ErrorAction SilentlyContinue))
            exit 4
        }
        try {
            $response = Invoke-WebRequest -Uri "$baseUrl/control/v1/health" -NoProxy -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) {
        Write-Log "FATAL: the double did not answer $baseUrl/control/v1/health within $ReadyTimeoutSeconds s"
        if (-not $process.HasExited) { $process.Kill() }
        exit 5
    }
    Write-Log "Ready at $baseUrl"

    # -------------------------------------------------------------------- seed ---

    $demands = @()
    if ($SeedPath -and (Test-Path -LiteralPath $SeedPath -PathType Leaf)) {
        $seed = Get-Content -LiteralPath $SeedPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 10
        if ($seed.ContainsKey('demands') -and $seed['demands']) { $demands = @($seed['demands']) }
    } else {
        Write-Log "No seed file at '$SeedPath'; the catalog stays empty."
    }

    # The runId is the double's, not ours. CommandEngine.Apply refuses any command whose runId is
    # not the round it is currently in (RUN_ID_MISMATCH, HTTP 409), and a freshly started double
    # generates its own. Reset both starts a round and returns that round's id, which also makes
    # this seeding idempotent: whatever the catalog held, it now holds the seed file and nothing
    # else.
    $resetBody = ConvertTo-Json -InputObject ([ordered]@{ commandId = "reset-$([guid]::NewGuid().ToString('N'))" }) -Depth 4
    $resetResponse = Invoke-WebRequest -Uri "$baseUrl/control/v1/reset" -Method Post `
        -ContentType 'application/json' -Body $resetBody -NoProxy -TimeoutSec 15 -UseBasicParsing
    $runId = ($resetResponse.Content | ConvertFrom-Json).runId
    if ([string]::IsNullOrWhiteSpace($runId)) {
        throw "The double's reset response carried no runId: $($resetResponse.Content)"
    }
    Write-Log "Catalog reset; this round is $runId"

    $seeded = 0
    foreach ($demand in $demands) {
        if (-not $demand.ContainsKey('demandId')) {
            Write-Log "SKIP: a seed entry has no demandId"
            continue
        }
        $demandId = [string] $demand['demandId']
        $body = [ordered]@{ runId = $runId; commandId = "seed-$demandId" }
        foreach ($key in $demand.Keys) {
            if ($key -eq 'demandId') { continue }
            $body[$key] = $demand[$key]
        }
        try {
            $null = Invoke-WebRequest -Uri "$baseUrl/control/v1/demands/$demandId" -Method Put `
                -ContentType 'application/json' `
                -Body (ConvertTo-Json -InputObject $body -Depth 8) `
                -NoProxy -TimeoutSec 15 -UseBasicParsing
            $seeded++
        } catch {
            # A refused demand is a defective seed file, not a reason to leave the machine with
            # no catalog service at all. Say which one and carry on; the count below is what an
            # operator compares against the file.
            Write-Log "SEED FAILED for '$demandId': $($_.Exception.Message)"
        }
    }
    Write-Log "Seeded $seeded of $($demands.Count) demand(s) under runId $runId"

    # Read the catalog back. Counting what the double reports, rather than what this script
    # believes it sent, is what distinguishes "two demands are in the catalog" from "two PUTs
    # returned 200" -- and a mismatch here is a defective seed file, which should be visible
    # now rather than as an unexplained quiet shift a week later.
    $snapshot = (Invoke-WebRequest -Uri "$baseUrl/control/v1/snapshot" -NoProxy -TimeoutSec 15 -UseBasicParsing).Content |
        ConvertFrom-Json
    $inCatalog = @($snapshot.body.demands).Count
    Write-Log "Catalog now holds $inCatalog demand(s) at revision $($snapshot.body.catalogRevision)"
    if ($inCatalog -ne $demands.Count) {
        Write-Log "WARNING: the seed file lists $($demands.Count) demand(s) but the catalog holds $inCatalog."
    }

    # ------------------------------------------------------------------- serve ---

    Write-Log 'Serving. This script stays alive as long as the double does.'
    $process.WaitForExit()
    Write-Log "The double exited with code $($process.ExitCode)"
    exit $process.ExitCode
} catch {
    Write-Log "FATAL: $($_.Exception.Message)"
    if (-not $process.HasExited) { $process.Kill() }
    exit 6
}
