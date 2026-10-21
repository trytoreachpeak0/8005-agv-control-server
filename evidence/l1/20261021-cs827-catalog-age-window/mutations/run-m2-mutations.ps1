#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-Location 'C:/work/8005-wt/cs827-8005-agv-control-server'
$out = 'C:/work/8005-scratch/cs827/mutations'
$m = @{ Id = 'M13-accepted-rows-dropped'; File = 'src/ControlServer.Host/Runtime/Dispatch/Criteria/AreaEqpUniqueCriterion.cs'
        Old = '(round.AcceptedDemandIds.Contains(item.DemandId) || CatalogAgeWindow.Refusal(round, item, round.Now) is null)'
        New = '(CatalogAgeWindow.Refusal(round, item, round.Now) is null)'
        Filter = 'FullyQualifiedName~CatalogAgeWindowTests.AnAcceptedRowPastTheWindowStillTakesPartInTheAreaCheck' }
if (git status --porcelain) { throw 'tree not clean' }
$path = Join-Path (Get-Location) $m.File
$text = [IO.File]::ReadAllText($path)
if (([regex]::Matches($text, [regex]::Escape($m.Old))).Count -ne 1) { throw 'anchor' }
[IO.File]::WriteAllText($path, $text.Replace($m.Old, $m.New))
if (@(git status --porcelain).Count -ne 1) { throw 'more than one file changed' }
try {
    $log = Join-Path $out "$($m.Id).log"
    dotnet build tests/ControlServer.Tests -v q -nologo *> $log
    dotnet test tests/ControlServer.Tests --no-build --filter $m.Filter *>> $log
    "$($m.Id): exit=$LASTEXITCODE $((Select-String -Path $log -Pattern 'Passed!|Failed!' | Select-Object -Last 1).Line)"
}
finally { git checkout -- $m.File }
if (git status --porcelain) { throw 'not restored' }
dotnet build tests/ControlServer.Tests -v q -nologo *> (Join-Path $out 'restore-build-m13.log')
"restored build exit=$LASTEXITCODE"
