#Requires -Version 7
# control-server#827 mutation self-check: each mutation edits exactly one file, builds, runs the tests that must turn red,
# and restores that one file with git checkout.
$ErrorActionPreference = 'Stop'
$repo = 'C:/work/8005-wt/cs827-8005-agv-control-server'
$out = 'C:/work/8005-scratch/cs827/mutations'
Set-Location $repo
$mutations = @(
    @{ Id = "M10-no-registration"; File = "src/ControlServer.Host/Runtime/Dispatch/Criteria/DispatchAdmissionCriteria.cs"; Old = "        services.AddScoped<IDispatchAdmissionCriterion, CatalogAgeWindowCriterion>();"; New = ""; Filter = "FullyQualifiedName~CatalogAgeWindowTests.TheHostRegistersTheCriterion" }
    @{ Id = "M11-append-skips-window"; File = "src/ControlServer.Host/Runtime/Dispatch/DispatchRoundRunner.cs"; Old = "            ? await AppendToJourneyAsync(selected, plan, intakeAt, FinalAdmissionAsync, cancellationToken)"; New = "            ? await AppendToJourneyAsync(selected, plan, intakeAt, token => FinalDynamicFactsReadyAsync(selected.Vehicle, underWay, expectedSessionGeneration, SlotRoom(selected.Candidate), selected.Facts, token), cancellationToken)"; Filter = "FullyQualifiedName~CatalogAgeWindowTests.AnEnRouteAppendIsJudgedAgainWhenIntakeRereadsTheCatalog" }
    @{ Id = "M12-no-ceiling"; File = "src/ControlServer.Host/Runtime/JourneyRuntimeOptions.cs"; Old = " || options.CatalogAgeWindow > TimeSpan.FromHours(24))"; New = ")"; Filter = "FullyQualifiedName~CatalogAgeWindowTests.TheWindowDefaultsToThirtyMinutesMustBePositiveAtMostADayAndIsShippedInAppSettings" }
)
$skipOld = @(
    @{ Id = 'M1-no-criterion'; File = 'src/ControlServer.Host/Runtime/Dispatch/Criteria/DispatchAdmissionCriteria.cs'
       Old = '            new CatalogAgeWindowCriterion(),'; New = ''
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests' }
    @{ Id = 'M2-no-intake-recheck'; File = 'src/ControlServer.Host/Runtime/Dispatch/DispatchRoundRunner.cs'
       Old = '            windowRefusalAtIntake = CatalogAgeWindow.Refusal(round, selected.Candidate.Snapshot, timeProvider.GetUtcNow());'
       New = '            windowRefusalAtIntake = null;'
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests.TheWindowIsJudgedAgainWhenIntakeRereadsTheCatalog' }
    @{ Id = 'M3-order-13'; File = 'src/ControlServer.Host/Runtime/Dispatch/Criteria/CatalogAgeWindowCriterion.cs'
       Old = '    public int Order => 45;'; New = '    public int Order => 13;'
       Filter = 'FullyQualifiedName~Batch7Req0185EutecticExclusionTests' }
    @{ Id = 'M4-no-sink-silence'; File = 'src/ControlServer.Host/Runtime/Dispatch/StructuralDispatchBlockSink.cs'
       Old = '        if (verdicts.Any(verdict => DispatchReasonCodes.IsCatalogAgeWindowRefusal(verdict.ReasonCode)))'
       New = '        if (verdicts.Any(verdict => false && DispatchReasonCodes.IsCatalogAgeWindowRefusal(verdict.ReasonCode)))'
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests.ADemandPastTheWindowRaisesNoStructuralBlockAndLosesTheOneItHad' }
    @{ Id = 'M5-no-starvation-exclusion'; File = 'src/ControlServer.Host/Runtime/Dispatch/StarvationExclusions.cs'
       Old = '            if (CatalogAgeWindow.Refusal(round, demand, round.Now) is not null)'
       New = '            if (CatalogAgeWindow.Refusal(round, demand, round.Now) is not null && demand.DemandId.Length < 0)'
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests.ADemandPastTheWindowIsNeverEscalatedForStarvation' }
    @{ Id = 'M6-no-redispatch-exemption'; File = 'src/ControlServer.Host/Runtime/Dispatch/CatalogAgeWindow.cs'
       Old = '        if (round.RedispatchGenerations.ContainsKey(demand.DemandId))'
       New = '        if (round.RedispatchGenerations.ContainsKey(demand.DemandId) && demand.DemandId.Length < 0)'
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests.ADemandReleasedBackForRedispatchIsRedispatchedWhateverItsAge' }
    @{ Id = 'M7-boundary-ge'; File = 'src/ControlServer.Host/Runtime/Dispatch/CatalogAgeWindow.cs'
       Old = 'at - demand.CreatedAt > round.CatalogAgeWindow'; New = 'at - demand.CreatedAt >= round.CatalogAgeWindow'
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests.ADemandExactlyAtTheWindowIsStillDispatched' }
    @{ Id = 'M8-missing-passes'; File = 'src/ControlServer.Host/Runtime/Dispatch/CatalogAgeWindow.cs'
       Old = '            return DispatchReasonCodes.DemandCatalogCreatedAtMissing;'; New = '            return null;'
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests.ADemandWithoutAFirstReadTimeIsNotDispatchedAndWaitsWithItsOwnReason' }
    @{ Id = 'M9-window-ignores-config'; File = 'src/ControlServer.Host/Runtime/Dispatch/DispatchRoundRunner.cs'
       Old = '            CatalogAgeWindow = runtimeOptions.CatalogAgeWindow,'; New = '            CatalogAgeWindow = JourneyRuntimeOptions.DefaultCatalogAgeWindow,'
       Filter = 'FullyQualifiedName~CatalogAgeWindowTests.TheWindowIsTheConfiguredOne' }
)
foreach ($m in $mutations) {
    if (git status --porcelain) { throw "tree not clean before $($m.Id)" }
    $path = Join-Path $repo $m.File
    $text = [IO.File]::ReadAllText($path)
    if (([regex]::Matches($text, [regex]::Escape($m.Old))).Count -ne 1) { throw "$($m.Id): anchor not found exactly once" }
    [IO.File]::WriteAllText($path, $text.Replace($m.Old, $m.New))
    $changed = @(git status --porcelain)
    if ($changed.Count -ne 1) { throw "$($m.Id): expected one changed file, got $($changed -join ', ')" }
    try {
        $log = Join-Path $out "$($m.Id).log"
        dotnet build tests/ControlServer.Tests -v q -nologo *> $log
        if ($LASTEXITCODE -ne 0) { "$($m.Id): BUILD FAILED" ; continue }
        dotnet test tests/ControlServer.Tests --no-build --filter $m.Filter *>> $log
        $exit = $LASTEXITCODE
        $summary = (Select-String -Path $log -Pattern 'Passed!|Failed!' | Select-Object -Last 1).Line
        "$($m.Id): exit=$exit $summary"
    }
    finally {
        git checkout -- $m.File
    }
}
if (git status --porcelain) { throw 'tree not clean after mutations' }
dotnet build tests/ControlServer.Tests -v q -nologo *> (Join-Path $out 'restore-build.log')
"restored build exit=$LASTEXITCODE"
