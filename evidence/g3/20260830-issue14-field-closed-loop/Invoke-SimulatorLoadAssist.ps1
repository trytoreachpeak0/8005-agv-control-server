[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:58006/api/v1',
    [Parameter(Mandatory)][string]$LogPath,
    [int]$PollMilliseconds = 500,
    [int]$MaxMinutes = 45
)

# Stands in for the operator at the slot doors while the eight-slot IO simulator is the IO input.
# The onboard opens a door and waits for the physical act to finish; a real operator puts the basket
# in (or takes it out) and closes the door. The workflow allows 120 s for the whole operation, and
# the simulator only ever holds one door open, so a slow or wrong reaction costs the entire run.
#
# The plan for a door is decided ONCE, when that door is first seen open: an empty slot is being
# loaded, an occupied slot is being unloaded. After that the assist only ever drives toward that
# plan and retries the close. An earlier version re-derived the intent from the live cargo state on
# every poll, so a close that failed to parse made it flip the cargo back and forth and a slot
# finished the operation in the wrong state.

$ErrorActionPreference = 'Continue'
$deadline = [DateTime]::UtcNow.AddMinutes($MaxMinutes)

function Write-Line([string]$Text) {
    $line = '{0} {1}' -f [DateTimeOffset]::Now.ToString('HH:mm:ss.fff'), $Text
    Add-Content -LiteralPath $LogPath -Value $line -Encoding utf8NoBOM
}

function Invoke-Simulator([string]$Method, [string]$Path, [string]$Body) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $raw = if ($Body) {
            & curl.exe --noproxy '*' -s --max-time 8 -X $Method -H 'Content-Type: application/json' -d $Body "$BaseUrl$Path" 2>$null
        } else {
            & curl.exe --noproxy '*' -s --max-time 8 "$BaseUrl$Path" 2>$null
        }
        $text = ($raw | Out-String).Trim()
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            try { return ($text | ConvertFrom-Json) } catch { Write-Line "  unparsable response: $($text.Substring(0, [Math]::Min(160, $text.Length)))" }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Get-Snapshot { return (Invoke-Simulator 'GET' '/snapshot' $null) }

function Send-Command([string]$Method, [string]$Path, [hashtable]$Extra) {
    $snapshot = Get-Snapshot
    if ($null -eq $snapshot) { return $null }
    $payload = @{ runId = $snapshot.runId; commandId = [Guid]::NewGuid().ToString('N'); expectedRevision = $snapshot.revision }
    foreach ($pair in $Extra.GetEnumerator()) { $payload[$pair.Key] = $pair.Value }
    return (Invoke-Simulator $Method $Path ($payload | ConvertTo-Json -Compress))
}

Write-Line "assist started (single-decision plan, poll ${PollMilliseconds}ms)"
$plan = @{}
while ([DateTime]::UtcNow -lt $deadline) {
    $snapshot = Get-Snapshot
    if ($null -eq $snapshot) { Start-Sleep -Milliseconds $PollMilliseconds; continue }

    foreach ($slot in $snapshot.slots) {
        $slotNo = $slot.slotNo
        if ($slot.doorState -ne 'OPEN') {
            if ($plan.ContainsKey($slotNo)) {
                Write-Line "slot $slotNo closed, cargo=$($slot.cargoState) (plan $($plan[$slotNo]) done)"
                $plan.Remove($slotNo)
            }
            continue
        }

        if (-not $plan.ContainsKey($slotNo)) {
            $plan[$slotNo] = if ($slot.cargoState -eq 'EMPTY') { 'OCCUPIED' } else { 'EMPTY' }
            Write-Line "slot $slotNo opened with cargo=$($slot.cargoState); plan = $($plan[$slotNo])"
        }

        if ($slot.cargoState -ne $plan[$slotNo]) {
            $r = Send-Command 'PUT' "/slots/$slotNo/cargo" @{ state = $plan[$slotNo] }
            Write-Line "  slot $slotNo set cargo $($plan[$slotNo]): rev=$($r.revision) reason=$($r.reasonCode)"
            continue
        }

        $c = Send-Command 'POST' "/slots/$slotNo/close-door" @{}
        Write-Line "  slot $slotNo close-door: rev=$($c.revision) changed=$($c.changed) reason=$($c.reasonCode) $($c.message)"
    }
    Start-Sleep -Milliseconds $PollMilliseconds
}
Write-Line 'assist finished'
