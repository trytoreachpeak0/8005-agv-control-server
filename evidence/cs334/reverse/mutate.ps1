#Requires -Version 7
<#
Reverse validation for control-server#334: each mutation takes one piece of the fix (or of what guard a protects) away,
rebuilds from scratch and runs the related test classes. A mutation that does not apply exactly once stops the script:
an injection that matched nothing would look exactly like a surviving mutant.

Run from the repository root: pwsh -File evidence/cs334/reverse/mutate.ps1 [-Only M1,M2]
#>
param([string] $Only = '')

$ErrorActionPreference = 'Stop'
$root = (Get-Location).Path
$outDir = Join-Path $root 'evidence/cs334/reverse'
$filter = 'FullyQualifiedName~GateHeldOnboardSendTests|FullyQualifiedName~OnboardPowerLossReconnectTests|' +
    'FullyQualifiedName~OnboardOutboundFunnelArchitectureTests|FullyQualifiedName~StoppedRebuildExitTests|' +
    'FullyQualifiedName~VehicleFaultRecoveryTests|FullyQualifiedName~OnboardSilentLivenessLossTests'

$peer = 'src/ControlServer.Host/Transport/OnboardPeer.cs'
$server = 'src/ControlServer.Host/Transport/OnboardTcpServer.cs'
$stopped = 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs'
$saveThenSend = @'
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await JourneyClosure.SendAsync(publisher, dbContext, agvId, cancellationToken).ConfigureAwait(false);
'@

$mutations = [ordered]@{
    M0 = @{ What = 'the whole fix reverted: no write bound at all (SendAsync awaits the write directly)'; File = $peer
        From = '            await send.WaitAsync(_writeTimeout, cancellationToken).ConfigureAwait(false);'
        To = '            await send.WaitAsync(cancellationToken).ConfigureAwait(false);' }
    M1 = @{ What = 'on timeout the stream is left open (throw only)'; File = $peer
        From = "            await _stream.DisposeAsync().ConfigureAwait(false);`n"
        To = '' }
    M2 = @{ What = 'the listener ignores OnboardTransport:WriteTimeout and uses the default'; File = $server
        From = 'new(stream, _options.WriteTimeout);'
        To = 'new(stream);' }
    M3 = @{ What = 'no startup validation of WriteTimeout'; File = $server
        From = "        if (_options.WriteTimeout <= TimeSpan.Zero)`n        {`n            throw new InvalidOperationException(`"OnboardTransport:WriteTimeout must be positive.`");`n        }`n"
        To = '' }
    M4 = @{ What = 'giving up a stopped trip reads RIoT under the gate'; File = $stopped
        From = $saveThenSend
        To = $saveThenSend.Replace(
            "            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);`n        }",
            "            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);`n" +
            "            await emergencyFacts.ReadEmergencyStateAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);`n        }") }
    M5 = @{ What = 'giving up a stopped trip sends its closure snapshots under the gate'; File = $stopped
        From = $saveThenSend
        To = @'
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await JourneyClosure.SendAsync(publisher, dbContext, agvId, cancellationToken).ConfigureAwait(false);
        }

'@ }
}

$selected = $Only -eq '' ? @($mutations.Keys) : @($Only -split ',' | ForEach-Object { $_.Trim() })
foreach ($id in $selected) {
    $m = $mutations[$id]
    $path = Join-Path $root $m.File
    $original = [System.IO.File]::ReadAllText($path)
    $from = $m.From.Replace("`r`n", "`n")
    $to = $m.To.Replace("`r`n", "`n")
    $count = ([regex]::Matches($original, [regex]::Escape($from))).Count
    if ($count -ne 1) {
        throw "$id matched $count times in $($m.File); it must match exactly once."
    }
    $backup = "$path.cs334-backup"
    Copy-Item $path $backup -Force
    try {
        $index = $original.IndexOf($from, [StringComparison]::Ordinal)
        [System.IO.File]::WriteAllText($path, $original.Remove($index, $from.Length).Insert($index, $to))
        $diff = git -C $root diff --stat -- $m.File
        $build = dotnet build tests/ControlServer.Tests -c Debug --no-incremental 2>&1 | Select-String -Pattern 'Error\(s\)|error CS' | Select-Object -First 5
        $log = Join-Path $outDir "$id.txt"
        dotnet test tests/ControlServer.Tests -c Debug --no-build --filter $filter 2>&1 | Out-File -FilePath $log -Encoding utf8NoBOM
        $exit = $LASTEXITCODE
        $failed = @(Select-String -Path $log -Pattern '^\s+Failed (ControlServer\.Tests\.\S+)' |
            ForEach-Object { $_.Line.Trim() -replace '^Failed ', '' -replace ' \[[^\]]+\]$', '' })
        $summary = (Select-String -Path $log -Pattern 'Passed!|Failed!' | Select-Object -Last 1).Line
        $record = @(
            "# $id -- $($m.What)", '',
            "- file: ``$($m.File)``, base ``$(git -C $root rev-parse --short HEAD)``, matched exactly once",
            "- build: $($build -join ' / ')",
            "- test exit code: $exit",
            "- summary: $summary",
            "- red tests ($($failed.Count)):"
        ) + ($failed | ForEach-Object { "  - ``$_``" }) + @('', '```diff') + @(git -C $root diff -- $m.File) + @('```')
        $record | Out-File -FilePath (Join-Path $outDir "$id.record.md") -Encoding utf8NoBOM
        Write-Host "$id exit=$exit red=$($failed.Count) :: $summary"
    }
    finally {
        Copy-Item $backup $path -Force
        (Get-Item $path).LastWriteTime = Get-Date
        Remove-Item $backup
        git -C $root diff --quiet -- $m.File
        if ($LASTEXITCODE -ne 0) { throw "$($m.File) did not restore cleanly after $id." }
    }
}
dotnet build tests/ControlServer.Tests -c Debug --no-incremental 2>&1 | Select-String -Pattern 'Error\(s\)'
