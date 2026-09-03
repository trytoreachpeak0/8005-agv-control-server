#Requires -Version 7

<#
The L2 scenario orchestrator's shared machinery: bring an environment up, drive the doubles'
control planes, assert against the server's own database, and write evidence in the shape the G3
runs already use.

Two rules run through all of it.

  Never sleep to wait for something. Every wait is a predicate with a deadline, so a slow machine
  is slower rather than flaky, and a stuck run says which predicate never came true.

  Never assert through a shortcut. State comes from the server's SQLite store and the doubles'
  snapshot endpoints, both of which a scenario reads exactly as an operator would.
#>

Set-StrictMode -Version Latest

class L2Journal {
    [string]$Path
    [hashtable]$Last = @{}

    L2Journal([string]$path) {
        $this.Path = $path
        [IO.File]::WriteAllText($path, '', [Text.UTF8Encoding]::new($false))
    }

    # Append-only, one observation per line, and only when a criterion actually flips. That shape
    # comes from remote-ops/status/Get-WireToGateStatus.ps1, which is what let the 2026-09-03
    # investigation pin "12:56:49 STOPPED -> 12:57:15 UNKNOWN" to the second.
    [void] Observe([string]$criterion, [object]$value, [hashtable]$detail) {
        $rendered = if ($null -eq $value) { '(null)' } else { [string]$value }
        if ($this.Last.ContainsKey($criterion) -and $this.Last[$criterion] -eq $rendered) {
            return
        }
        $this.Last[$criterion] = $rendered
        $line = [ordered]@{
            at        = [DateTimeOffset]::UtcNow.ToString('o')
            criterion = $criterion
            value     = $rendered
        }
        if ($detail) { $line['detail'] = $detail }
        Add-Content -LiteralPath $this.Path -Value ($line | ConvertTo-Json -Compress -Depth 8) -Encoding utf8NoBOM
    }

    [void] Note([string]$message) {
        $line = [ordered]@{
            at      = [DateTimeOffset]::UtcNow.ToString('o')
            note    = $message
        }
        Add-Content -LiteralPath $this.Path -Value ($line | ConvertTo-Json -Compress -Depth 8) -Encoding utf8NoBOM
    }
}

function New-L2Journal {
    param([Parameter(Mandatory)][string]$Path)
    return [L2Journal]::new($Path)
}

<#
Waits for a predicate, polling on a fixed cadence until a deadline. Returns the last value the
predicate produced so a caller can assert on it without reading the world a second time.
#>
function Wait-L2Condition {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        [int]$TimeoutSeconds = 60,
        [int]$PollMilliseconds = 250,
        [L2Journal]$Journal,
        [string]$Criterion
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    while ($true) {
        try { $last = & $Probe } catch { $last = $null }
        if ($Journal -and $Criterion) { $Journal.Observe($Criterion, $last, $null) }
        if ($null -ne $last -and (& $Until $last)) { return $last }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            $seen = if ($null -eq $last) { '(nothing)' } else { ($last | ConvertTo-Json -Compress -Depth 6) }
            throw "Timed out after ${TimeoutSeconds}s waiting for: $Description. Last observed: $seen"
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
}

# --- control-plane client -----------------------------------------------------------------------

<#
One client for every double, because they all speak the same dialect: runId scoping, commandId
idempotency, expectedRevision optimistic concurrency. The runId is read fresh from the double
rather than cached, so a scenario that resets a double mid-run does not have to re-plumb it.
#>
class L2Double {
    [string]$Name
    [string]$BaseUrl

    L2Double([string]$name, [string]$baseUrl) {
        $this.Name = $name
        $this.BaseUrl = $baseUrl.TrimEnd('/')
    }

    [object] Snapshot() {
        return Invoke-RestMethod -Uri "$($this.BaseUrl)/control/v1/snapshot" -TimeoutSec 10
    }

    [object] Health() {
        return Invoke-RestMethod -Uri "$($this.BaseUrl)/control/v1/health" -TimeoutSec 10
    }

    [object] Command([string]$method, [string]$path, [hashtable]$body) {
        $payload = @{} + $body
        $payload['runId'] = $this.Snapshot().runId
        if (-not $payload.ContainsKey('commandId')) {
            $payload['commandId'] = [guid]::NewGuid().ToString('N')
        }
        return Invoke-RestMethod `
            -Uri "$($this.BaseUrl)/control/v1/$($path.TrimStart('/'))" `
            -Method $method `
            -ContentType 'application/json' `
            -Body ($payload | ConvertTo-Json -Depth 8) `
            -TimeoutSec 30
    }
}

function New-L2Double {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$BaseUrl
    )
    return [L2Double]::new($Name, $BaseUrl)
}

# --- processes ----------------------------------------------------------------------------------

<#
Starts one component and returns a handle that carries its log paths, so a failed run leaves the
stdout and stderr of every process behind rather than only the orchestrator's own view.
#>
function Start-L2Process {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory,
        [hashtable]$Environment = @{},
        [Parameter(Mandatory)][string]$LogRoot
    )

    $outLog = Join-Path $LogRoot "$Name.out.log"
    $errLog = Join-Path $LogRoot "$Name.err.log"
    $startArguments = @{
        FilePath               = $FilePath
        RedirectStandardOutput = $outLog
        RedirectStandardError  = $errLog
        WindowStyle            = 'Hidden'
        PassThru               = $true
    }
    if ($ArgumentList.Count -gt 0) { $startArguments['ArgumentList'] = $ArgumentList }
    if ($WorkingDirectory) { $startArguments['WorkingDirectory'] = $WorkingDirectory }
    if ($Environment.Count -gt 0) { $startArguments['Environment'] = $Environment }
    $process = Start-Process @startArguments
    return [pscustomobject]@{
        Name    = $Name
        Process = $process
        OutLog  = $outLog
        ErrLog  = $errLog
    }
}

<#
Stops components in reverse start order. Teardown runs from a finally block, so it must not throw:
a component that has already exited is the normal case, not an error to mask the real one with.
#>
function Stop-L2Process {
    param([Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$Handles)

    foreach ($handle in ($Handles | Where-Object { $_ } | Sort-Object -Descending { $_.Order })) {
        try {
            if (-not $handle.Process.HasExited) {
                $handle.Process.Kill($true)
                $null = $handle.Process.WaitForExit(10000)
            }
        } catch {
            Write-Warning "Could not stop $($handle.Name): $_"
        }
    }
}

# --- ControlServer database ---------------------------------------------------------------------

<#
Opens the server's own SQLite store read-only, borrowing Microsoft.Data.Sqlite and SQLitePCLRaw
from the ControlServer build under test rather than adding a dependency of its own. Read-only and
shared-cache so reading can never block or alter the server that owns the file.
#>
function Open-L2Database {
    param(
        [Parameter(Mandatory)][string]$HostDirectory,
        [Parameter(Mandatory)][string]$DatabasePath
    )

    foreach ($assembly in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll',
                            'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
        $path = Join-Path $HostDirectory $assembly
        if (Test-Path -LiteralPath $path) { Add-Type -LiteralPath $path -ErrorAction SilentlyContinue }
    }
    # Batteries_V2 reports a missing type on some builds and the queries work regardless, so this
    # is deliberately swallowed rather than allowed to fail the run before it starts.
    try { [SQLitePCL.Batteries_V2]::Init() } catch { }
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
        "Data Source=$DatabasePath;Mode=ReadOnly;Cache=Shared")
    $connection.Open()
    return $connection
}

function Invoke-L2Query {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$Sql
    )

    $command = $Connection.CreateCommand()
    $command.CommandText = $Sql
    $reader = $command.ExecuteReader()
    $rows = @()
    while ($reader.Read()) {
        $row = [ordered]@{}
        for ($index = 0; $index -lt $reader.FieldCount; $index++) {
            $row[$reader.GetName($index)] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
        }
        $rows += [pscustomobject]$row
    }
    $reader.Close()
    $command.Dispose()
    return , $rows
}

# --- evidence -----------------------------------------------------------------------------------

class L2Assertions {
    [System.Collections.Generic.List[object]]$Items = [System.Collections.Generic.List[object]]::new()

    [void] Add([string]$id, [string]$description, [bool]$passed, [object]$expected, [object]$actual) {
        $this.Items.Add([ordered]@{
            id          = $id
            description = $description
            outcome     = if ($passed) { 'PASS' } else { 'FAIL' }
            expected    = $expected
            actual      = $actual
        })
    }

    [bool] AllPassed() {
        return -not ($this.Items | Where-Object { $_.outcome -ne 'PASS' })
    }
}

function New-L2Assertions { return [L2Assertions]::new() }

function Write-L2Evidence {
    param(
        [Parameter(Mandatory)][string]$EvidenceRoot,
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$RunId,
        [Parameter(Mandatory)][L2Assertions]$Assertions,
        [Parameter(Mandatory)][string]$Outcome,
        [string]$FailureReason,
        [hashtable]$Identity = @{}
    )

    $assertionsPath = Join-Path $EvidenceRoot 'assertions.json'
    $document = [ordered]@{
        schemaVersion = 1
        scenario      = $Scenario
        runId         = $RunId
        outcome       = $Outcome
        failureReason = $FailureReason
        identity      = $Identity
        assertions    = $Assertions.Items
    }
    [IO.File]::WriteAllText(
        $assertionsPath,
        ($document | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# L2 场景证据：$Scenario")
    $lines.Add('')
    $lines.Add("结论：**$Outcome**")
    if ($FailureReason) {
        $lines.Add('')
        $lines.Add("失败原因：$FailureReason")
    }
    $lines.Add('')
    $lines.Add('## 身份')
    $lines.Add('')
    $lines.Add('| 项 | 值 |')
    $lines.Add('| --- | --- |')
    $lines.Add("| runId | ``$RunId`` |")
    foreach ($key in ($Identity.Keys | Sort-Object)) {
        $lines.Add("| $key | ``$($Identity[$key])`` |")
    }
    $lines.Add('')
    $lines.Add('## 判据')
    $lines.Add('')
    $lines.Add('| 判据 | 结论 | 期望 | 实际 |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($item in $Assertions.Items) {
        $expected = ($item.expected | Out-String).Trim() -replace '\|', '\|' -replace '\r?\n', ' '
        $actual = ($item.actual | Out-String).Trim() -replace '\|', '\|' -replace '\r?\n', ' '
        $lines.Add("| $($item.description) | $($item.outcome) | ``$expected`` | ``$actual`` |")
    }
    $lines.Add('')
    $lines.Add('## 目录内容')
    $lines.Add('')
    $lines.Add('- `assertions.json` —— 机器可读的判据结论')
    $lines.Add('- `timeline.jsonl` —— 一行一次判据翻转，只追加')
    $lines.Add('- `logs/` —— 每个组件的 stdout 与 stderr')
    $lines.Add('- `snapshots/` —— 收尾时各控制面与服务端数据库的快照')
    $lines.Add('')
    $lines.Add('L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，')
    $lines.Add('**不代表真实 RCS、真车、真实 IO 模块或接线合格**。')

    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'SUMMARY.md'),
        ($lines -join "`n") + "`n",
        [Text.UTF8Encoding]::new($false))
}

Export-ModuleMember -Function New-L2Journal, Wait-L2Condition, New-L2Double, Start-L2Process,
    Stop-L2Process, Open-L2Database, Invoke-L2Query, New-L2Assertions, Write-L2Evidence
