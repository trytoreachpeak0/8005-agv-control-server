#Requires -Version 7
# control-server#828 mutation self-test: each mutation edits exactly one file, runs the targeted classes, expects red, restores.
param([string] $Worktree = 'C:/work/8005-wt/cs-828-8005-agv-control-server', [string] $Out = 'C:/work/8005-scratch/cs828/mutations', [string[]] $Only = @())
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })
Set-Location $Worktree
New-Item -ItemType Directory -Force $Out | Out-Null
$d = 'src/ControlServer.Host/Runtime/Dispatch'
$mutations = @(
    @{ Id = 'M1'; File = "$d/Criteria/DispatchAdmissionCriteria.cs"; Old = "            new TrialSublotCriterion(),`n"; New = ''; Expect = 'ATrialSublotIsNotDispatchedAndWaitsWithItsReason' }
    @{ Id = 'M2'; File = "$d/Criteria/DispatchAdmissionCriteria.cs"; Old = "        services.AddScoped<IDispatchAdmissionCriterion, TrialSublotCriterion>();`n"; New = ''; Expect = 'TheHostRegistersTheCriterionAndTheSingletonLog' }
    @{ Id = 'M3'; File = "$d/Criteria/AreaEqpUniqueCriterion.cs"; Old = ' && TrialSublot.Refusal(round, item) is null'; New = ''; Expect = 'ATrialRowDoesNotMakeAnOrdinaryDemandsAreaAmbiguous' }
    @{ Id = 'M4'; File = "$d/StructuralDispatchBlockSink.cs"; Old = ' ||
                                    verdict.ReasonCode == DispatchReasonCodes.DemandTrialSublot'; New = ''; Expect = 'ABlockRaisedBeforeTheRuleWasOnIsClearedOnceItIs' }
    @{ Id = 'M5'; File = "$d/StarvationExclusions.cs"; Old = ' || TrialSublot.Refusal(round, demand) is not null'; New = ''; Expect = 'ATrialSublotIsNeverEscalatedForStarvation' }
    @{ Id = 'M6'; File = "$d/Criteria/TrialSublotCriterion.cs"; Old = 'public int Order => 42;'; New = 'public int Order => 46;'; Expect = 'ATrialSublotPastTheCatalogWindowIsRefusedAsATrialSublot' }
    @{ Id = 'M7'; File = "$d/TrialSublot.cs"; Old = 'if (round.RedispatchGenerations.ContainsKey(demand.DemandId))'; New = 'if (demand.DemandId.Length < 0)'; Expect = 'ATrialSublotReleasedBackForRedispatchIsRedispatched' }
    @{ Id = 'M8'; File = "$d/TrialSublot.cs"; Old = 'sublot.Length >= Position && Characters.Contains(sublot[Position - 1], StringComparison.Ordinal)'; New = 'sublot.Trim().Normalize(System.Text.NormalizationForm.FormKC).Length >= Position && Characters.Contains(sublot.Trim().Normalize(System.Text.NormalizationForm.FormKC)[Position - 1], StringComparison.Ordinal)'; Expect = 'ASublotThatIsNotATrialOneIsDispatched' }
    @{ Id = 'M9'; File = "$d/TrialSublot.cs"; Old = '[Position - 1]'; New = '[Position - 2]'; Expect = 'TheProductionRuleMatchesCharacterSixBeingAHalfWidthNine' }
    @{ Id = 'M10'; File = 'src/ControlServer.Host/Runtime/JourneyRuntimeOptions.cs'; Old = '(options.TrialSublotCharacterPosition > 0) != !string.IsNullOrEmpty(options.TrialSublotCharacters)'; New = 'false'; Expect = 'TheRuleShipsEmptyAndHalfARuleIsRefused' }
    @{ Id = 'M11'; File = "$d/TrialSublotFilterLog.cs"; Old = 'if (current.SequenceEqual(_last, StringComparer.Ordinal))'; New = 'if (current.Length < 0)'; Expect = 'TheFilteredSublotsAreLoggedOnlyWhenTheyChange' }
    @{ Id = 'M12'; File = "$d/DispatchRoundRunner.cs"; Old = 'TrialSublotRule = TrialSublotRule.From(runtimeOptions),'; New = ''; Expect = 'ATrialSublotIsNotDispatchedAndWaitsWithItsReason' }
    @{ Id = 'M13'; File = 'src/ControlServer.Host/Dashboard/DispatchBacklogQueryEndpoint.cs'; Old = '[DispatchReasonCodes.DemandTrialSublot] ='; New = '["X_" + DispatchReasonCodes.DemandTrialSublot] ='; Expect = 'TheDashboardListsTheRefusalWithAChineseDescription' }
)
$summary = @()
foreach ($m in $mutations) {
    if ($Only.Count -gt 0 -and $m.Id -notin $Only) { continue }
    $full = (Resolve-Path $m.File).Path
    $text = [IO.File]::ReadAllText($full)
    $count = ([regex]::Matches($text, [regex]::Escape($m.Old))).Count
    if ($count -ne 1) { $summary += "$($m.Id) SKIPPED: $count matches in $($m.File)"; continue }
    [IO.File]::WriteAllText($full, $text.Replace($m.Old, $m.New))
    $changed = @(git diff --name-only)
    if ($changed.Count -ne 1) { git checkout -- .; throw "$($m.Id): expected one changed file, got $($changed -join ', ')" }
    $log = Join-Path $Out "$($m.Id).txt"
    git diff | Out-File -FilePath $log -Encoding utf8
    dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter 'FullyQualifiedName~TrialSublotAdmissionTests' *>> $log
    $exit = $LASTEXITCODE
    git checkout -- $m.File
    if (@(git diff --name-only).Count -ne 0) { throw "$($m.Id): restore left changes" }
    $red = Select-String -Path $log -Pattern "Failed ControlServer\.Tests\.TrialSublotAdmissionTests\.$($m.Expect)" -Quiet
    $summary += "$($m.Id) exit=$exit expected-red=$($m.Expect) $(if ($red) { 'RED (killed)' } else { 'NOT RED (survived)' })"
}
$summary | Tee-Object -FilePath (Join-Path $Out 'SUMMARY.txt')
