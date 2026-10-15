import subprocess, sys, os, datetime
os.chdir(r'F:/8005-wt/cs713-8005-agv-control-server')
HEAVY = r'C:/Users/szy/Desktop/8005-workspace-v2/Invoke-HeavyLocal.ps1'
ENGINE = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.StagingDeparture.cs'
RULE = 'src/ControlServer.Host/Runtime/CurrentStopDepartureRule.cs'
COORD = 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs'

MUTATIONS = [
    ('M1-release-becomes-cancel', ENGINE,
     '''            await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, at, cancellationToken)
                .ConfigureAwait(false);''',
     '''            await new PickupStopTermination(dbContext).StageDemandTerminationAsync(demandId, cancellationToken)
                .ConfigureAwait(false);''',
     'FullyQualifiedName~StagingDepartureRequestTests.DepartureAcceptedReleasesWhatIsNotLoadedAndLeavesWithWhatIs'),
    ('M2-staging-discrimination-removed', ENGINE,
     'AtStagingPickup: IsStagingPickup(current),',
     'AtStagingPickup: true,',
     'FullyQualifiedName~StagingDepartureRequestTests.DepartureNotAllowedAwayFromAStagingPickup'),
    ('M3a-anchor-not-released-with-nothing-loaded', ENGINE,
     '''                item.Demand.Status == DemandExecutionStatus.Accepted &&
                item.Membership.Status == JourneyDemandStatuses.PendingLoad)];''',
     '''                item.Demand.Status == DemandExecutionStatus.Accepted &&
                item.Membership.Status == JourneyDemandStatuses.PendingLoad &&
                !string.Equals(item.Demand.DemandId, runtime.DemandId, StringComparison.Ordinal))];''',
     'FullyQualifiedName~StagingDepartureRequestTests.WithNothingLoadedTheJourneyClosesInPlaceAndEveryDemandIncludingTheAnchorGoesBack'),
    ('M3b-anchor-guard-removed', RULE,
     '            facts.AnchorUnloadedWithCargo ? "The journey\'s first demand is not loaded while other cargo is on board." :\n',
     '',
     'FullyQualifiedName~StagingDepartureRequestTests.TheRuleChecksStationThenRevisionThenState'),
    ('M5-staging-cancellation-terminates', COORD,
     '        if (!_stagingRules.Enabled ||\n            stops.OpenStops.Count == 0 ||',
     '        if (true ||\n            stops.OpenStops.Count == 0 ||',
     'FullyQualifiedName~StagingLoadCancellationTests.ACancellationDuringTheLoadReleasesTheDemandOnlyAtTheStagingStation'),
    ('M4-anchor-cancel-keeps-others', COORD,
     '''        foreach (JourneyDemandRow other in alongside)
        {
            await Runtime.Release.DemandReleaseService.StageReleasedMembershipAsync(dbContext, other, now, cancellationToken)
                .ConfigureAwait(false);
        }
''',
     '',
     'FullyQualifiedName~StagingLoadCancellationTests.CancellingTheAnchorDuringItsLoadClosesTheJourneyAndReleasesEverything'),
    ('M6-wire-staging-never-named', 'src/ControlServer.Host/Runtime/JourneyPlanBuilder.cs',
     'isStagingPickup?.Invoke(stop) == true',
     'false',
     'FullyQualifiedName~StagingDepartureRequestTests.OnlyAStagingPickupLegNamesItsPublicStationFunction|FullyQualifiedName~StagingDepartureRequestTests.AnAppendedStagingStopIsNamedAsWell'),
]

def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='replace')
    return p.returncode, p.stdout + p.stderr

out = []
only = sys.argv[1:]
for name, path, old, new, flt in MUTATIONS:
    if only and name not in only:
        continue
    src = open(path, encoding='utf-8', newline='').read()
    assert src.count(old) == 1, (name, 'anchor text not unique')
    open(path, 'w', encoding='utf-8', newline='').write(src.replace(old, new))
    rc, diff = run(['git', 'diff', '--name-only'])
    changed = [l for l in diff.split('\n') if l.strip()]
    assert changed == [path], (name, changed)
    try:
        rc, log = run(['pwsh', '-File', HEAVY, '-Ticket', 'cs#713', 'dotnet', 'build',
                       'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release'])
        if rc != 0:
            verdict = 'BUILD-FAILED'
            summary = [l for l in log.split('\n') if ' error ' in l][:3]
        else:
            rc, log = run(['pwsh', '-File', HEAVY, '-Ticket', 'cs#713', 'dotnet', 'test',
                           'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release', '--no-build',
                           '--filter', flt])
            summary = [l for l in log.split('\n') if 'Passed!' in l or 'Failed!' in l or l.strip().startswith('Failed ')]
            verdict = 'KILLED (red)' if rc != 0 else 'SURVIVED (green)'
    finally:
        subprocess.run(['git', 'checkout', '--', path])
    rc, diff = run(['git', 'diff', '--name-only'])
    assert diff.strip() == '', ('not restored', diff)
    line = f'{datetime.datetime.now().isoformat(timespec="seconds")} {name}: {verdict} | ' + ' / '.join(s.strip() for s in summary)
    print(line, flush=True)
    out.append(line)

os.makedirs('evidence/cs713/mutations', exist_ok=True)
with open('evidence/cs713/mutations/results.txt', 'a', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(out) + '\n')
