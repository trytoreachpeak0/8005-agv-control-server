#Requires -Version 7
# cs#597 mutation self-check. Each mutation edits exactly one file, runs the targeted tests, expects red, restores that file.
$ErrorActionPreference = 'Stop'
$tree = 'F:/8005-wt/cs597-8005-agv-control-server'
$out = 'F:/8005-scratch/cs597-mutations'
New-Item -ItemType Directory -Force $out | Out-Null
$filter = 'FullyQualifiedName~SafetyStateLatestFirstReadTests|FullyQualifiedName~ExpectedActionOverdueTests|FullyQualifiedName~ProtocolInboxReadWindowTests|FullyQualifiedName~ArchitectureTests'
if (git -C $tree status --porcelain) { throw 'worktree not clean' }
$reader = 'src/ControlServer.Host/Runtime/SafetyMessagesNewestFirst.cs'
$dashboard = 'src/ControlServer.Host/Dashboard/ExpectedActionOverdueQueryEndpoint.cs'
$facts = 'src/ControlServer.Host/Runtime/Dispatch/OnboardDispatchFactsReader.cs'
$migration = 'src/ControlServer.Infrastructure/Persistence/Migrations/20261013041134_ProtocolInboxTypeNewestFirstIndex.cs'
$mutations = @(
    @{ Id = 'M1-type-tiebreak-flipped'; File = $reader
       From = 'ORDER BY "ReceivedAtUtcTicks" DESC, "MessageType", "InsertionOrder"'
       To = 'ORDER BY "ReceivedAtUtcTicks" DESC, "MessageType" DESC, "InsertionOrder"' }
    @{ Id = 'M2-insertion-tiebreak-flipped'; File = $reader
       From = 'ORDER BY "ReceivedAtUtcTicks" DESC, "MessageType", "InsertionOrder"'
       To = 'ORDER BY "ReceivedAtUtcTicks" DESC, "MessageType", "InsertionOrder" DESC' }
    @{ Id = 'M3-receive-order-ascending'; File = $reader
       From = 'ORDER BY "ReceivedAtUtcTicks" DESC, "MessageType", "InsertionOrder"'
       To = 'ORDER BY "ReceivedAtUtcTicks", "MessageType", "InsertionOrder"' }
    # Dashboard stops at the first snapshot it meets, coverage or not (a runtime condition, not a constant).
    @{ Id = 'M4-dashboard-stops-without-coverage'; File = $dashboard
       From = 'if (latestSnapshot is not null && coverage.CompleteAfter(latestVersion))'
       To = 'if (latestSnapshot is not null && (coverage.CompleteAfter(latestVersion) || latestVersion > long.MinValue))' }
    # Dispatch summary ignores the generation.
    @{ Id = 'M5-summary-ignores-generation'; File = $facts
       From = 'root.GetProperty("sessionGeneration").GetInt64() == generation &&
                root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64() == safetyRevision)'
       To = '(root.GetProperty("sessionGeneration").GetInt64() == generation || generation > 0) &&
                root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64() == safetyRevision)' }
    # The migration builds no composite index.
    @{ Id = 'M6-migration-builds-no-index'; File = $migration
       From = 'columns: new[] { "MessageType", "ReceivedAtUtcTicks" });'
       To = 'columns: new[] { "ReceivedAtUtcTicks", "MessageType" });' }
    # Review S1: other vehicles' and generations' versions counted towards coverage.
    @{ Id = 'M7-coverage-counts-every-vehicle'; File = $dashboard
       From = '            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("agvId", out JsonElement agv) || agv.GetString() != agvId'
       To = '            JsonElement root = document.RootElement;
            coverage.Saw(root.GetProperty("payload").GetProperty("safetyStateVersion").GetInt64());
            if (!root.TryGetProperty("agvId", out JsonElement agv) || agv.GetString() != agvId' }
    # Review addition 3: coverage asks only whether the session revision itself was read, not the whole range after the snapshot.
    @{ Id = 'M8-coverage-checks-only-the-revision'; File = $dashboard
       From = '            return firstUnseen > revision;'
       To = '            return seen.Contains(revision);' }
)
foreach ($m in $mutations) {
    $path = Join-Path $tree $m.File
    $text = [IO.File]::ReadAllText($path)
    $from = $m.From -replace "`r`n", "`n"
    $probe = $text -replace "`r`n", "`n"
    $occurrences = ($probe.Length - $probe.Replace($from, '').Length) / $from.Length
    if ($occurrences -ne 1) { throw "$($m.Id): anchor found $occurrences times, want exactly 1" }
    $mutated = $probe.Replace($from, ($m.To -replace "`r`n", "`n"))
    if ($text.Contains("`r`n")) { $mutated = $mutated -replace "`n", "`r`n" }
    [IO.File]::WriteAllText($path, $mutated)
    try {
        $changed = @(git -C $tree diff --name-only)
        if ($changed.Count -ne 1) { throw "$($m.Id): expected exactly one changed file, got $($changed -join ',')" }
        $log = "$out/$($m.Id).txt"
        git -C $tree diff | Set-Content $log
        dotnet test "$tree/tests/ControlServer.Tests/ControlServer.Tests.csproj" -nologo --filter $filter 2>&1 | Add-Content $log
        $summary = Select-String -Path $log -Pattern 'Passed!|Failed!' | Select-Object -Last 1
        $failed = Select-String -Path $log -Pattern '^\s+Failed ControlServer' | ForEach-Object { $_.Line.Trim() }
        "{0}: {1}" -f $m.Id, $summary.Line.Trim()
        $failed | ForEach-Object { "    $_" }
    }
    finally {
        git -C $tree checkout -- $m.File
    }
}
if (git -C $tree status --porcelain) { throw 'worktree not clean after restore' }
