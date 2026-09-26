#Requires -Version 7
# Reverse verification for control-server#349: each mutation removes one guard the new tests lean on, runs the class,
# and restores the file from a byte copy. Never committed.
param([Parameter(Mandatory)][string]$Repo, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Set-Location $Repo
$rebuild = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.OwnOrderRebuild.cs'
$supervisor = 'src/ControlServer.Host/Runtime/Commands/EmergencyStopSupervisor.cs'
$mutations = @(
    @{ Id = 'M1-no-delay'; File = $rebuild; From = 'if (now < rebuild.DueAt)'; To = 'if (false && now < rebuild.DueAt)' }
    @{ Id = 'M2-fault-not-a-vehicle-condition'; File = $rebuild; From = '.AnyAsync(row => row.AgvId == runtime.AgvId && row.Level != VehicleFaultLevel.None'; To = '.AnyAsync(row => row.AgvId == "__none__" && row.Level != VehicleFaultLevel.None' }
    @{ Id = 'M3-no-cargo-evidence'; File = $rebuild; From = 'if (rebuild.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard && rebuild.CargoProvenAt is null)'; To = 'if (false && rebuild.CargoProvenAt is null)' }
    @{ Id = 'M4-no-session-gate'; File = $rebuild; From = 'if (!mayCreate)'; To = 'if (false && !mayCreate)' }
    @{ Id = 'M5-release-ignores-unfinished-orders'; File = $supervisor; From = 'confirmation, emergency, episode is not null, orders);'; To = 'confirmation, emergency, episode is not null, orders with { HasUnfinishedOrder = false });' }
    @{ Id = 'M6-cancel-source-needs-cargo-evidence'; File = $rebuild; From = 'if (rebuild.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard && rebuild.CargoProvenAt is null)'; To = 'if ((rebuild.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard || rebuild.Source == OwnOrderRebuildSources.CancelledInRiot) && rebuild.CargoProvenAt is null)' }
)
New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($m in $mutations) {
    $backup = Join-Path $Out "$($m.Id).bak"
    Copy-Item $m.File $backup
    try {
        $text = [IO.File]::ReadAllText((Resolve-Path $m.File))
        $count = ([regex]::Matches($text, [regex]::Escape($m.From))).Count
        if ($count -ne 1) { throw "$($m.Id): expected exactly one match, found $count" }
        [IO.File]::WriteAllText((Resolve-Path $m.File), $text.Replace($m.From, $m.To))
        $diff = git diff --numstat -- $m.File
        "=== $($m.Id) diff: $diff" | Tee-Object -Append (Join-Path $Out 'summary.txt')
        $build = dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental 2>&1
        $errors = ($build | Select-String ' Error\(s\)').Line
        "    build: $errors" | Tee-Object -Append (Join-Path $Out 'summary.txt')
        $test = dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~EmergencyReleaseVersusOwnOrderRebuildTests' 2>&1
        $test | Set-Content (Join-Path $Out "$($m.Id).log")
        ($test | Select-String '\[FAIL\]|Passed!|Failed!|\.cs:line') | ForEach-Object { "    $($_.Line.Trim())" } |
            Tee-Object -Append (Join-Path $Out 'summary.txt')
    }
    finally {
        Copy-Item $backup $m.File -Force
        (Get-Item $m.File).LastWriteTime = Get-Date
    }
}
$clean = git status --porcelain -- src
"=== src clean after restore: '$clean'" | Tee-Object -Append (Join-Path $Out 'summary.txt')
dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental 2>&1 | Select-String ' Error\(s\)'
