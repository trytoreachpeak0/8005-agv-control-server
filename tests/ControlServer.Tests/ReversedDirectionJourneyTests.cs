using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// STAGING_TO_WIRE, the one task type whose fixed station is the origin (control-server#163): the vehicle
/// loads at the dispatch staging station bound to the task type and unloads at the demand's AREA machine
/// station. Its direction comes from the task type rule, never from swapping the two ends.
/// </summary>
public sealed class ReversedDirectionJourneyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

    private static readonly RiotMapStation Machine = new(12, "N1-1");

    private static readonly RiotMapStation Gate = new(210, "关卡");

    private static readonly RiotMapStation Staging = new(305, "派工待送取货");

    private static readonly RiotMapStationCatalogSnapshot Map = new(
        25, Now, new string('c', 64), [Machine, Gate, Staging]);

    private static readonly JourneyRuntimeOptions Options = new()
    {
        MapId = 25,
        MapIdentity = "MAP-25",
        DispatchGeneration = 1,
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The fixed station at the origin end is the pickup and the AREA machine station the drop-off; the
    /// plan sent to the vehicle runs TO_PICKUP at the staging station, then TO_DROPOFF at the machine, and
    /// carries no public station function.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AStagingToWirePlanPicksUpAtTheStagingStationAndDropsOffAtTheAreaMachine()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(TransportTaskTypes.StagingToWire);

        Assert.Equal(DispatchAdmissionChain.Eligible, await ResolveAsync(evaluation));

        ResolvedJourneyRoute route = evaluation.Route!;
        Assert.Equal(("派工待送取货", 305), (route.PickupStationId, route.PickupStationRiotId));
        Assert.Equal(("N1-1", 12), (route.DropoffStationId, route.DropoffStationRiotId));

        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptAsync(fixture, evaluation);
        // control-server#208 起计划由停靠序列投影出来，所以这里从库里取这趟旅程的两个停靠——受理事务在写旅程行的
        // 同一次保存里写了它们。
        JourneyStopRow[] stops = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .Where(row => row.JourneyId == runtime.JourneyId)
            .OrderBy(row => row.Sequence)
            .ToArrayAsync(Token);
        UpcomingStopPlanProjection plan = JourneyPlanBuilder.Plan(
            runtime, stops, stops[0], arrivedAtCurrent: false, runtime.PlanRevision);
        Assert.Equal(
            [("TO_PICKUP", 1, "派工待送取货", "ACTIVE"), ("TO_DROPOFF", 2, "N1-1", "PLANNED")],
            plan.Legs.Select(leg => (leg.LegType, leg.Sequence, leg.StationId, leg.State)));
        Assert.All(plan.Legs, leg => Assert.Null(leg.PublicStationFunction));
        Assert.Equal(
            ["派工待送取货", "N1-1"],
            JourneyPlanBuilder
                .Plan(runtime, stops, stops[1], arrivedAtCurrent: true, runtime.PlanRevision + 2)
                .Legs.Select(leg => leg.StationId));
    }

    /// <summary>
    /// Scope specification 5.3: the route evidence id hashes its two ends in order, so a reverse route
    /// built with the ends swapped would still compile and would silently fail an idempotent replay. The
    /// same reverse demand resolves to the same id every time and its replay is accepted; the id of the
    /// swapped route is a different one, and a replay carrying it is refused.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AReplayedStagingToWireDemandKeepsItsRouteEvidenceAndASwappedOneIsRefused()
    {
        DispatchCandidateEvaluation first = Evaluation(TransportTaskTypes.StagingToWire);
        DispatchCandidateEvaluation replay = Evaluation(TransportTaskTypes.StagingToWire);
        Assert.Equal(DispatchAdmissionChain.Eligible, await ResolveAsync(first));
        Assert.Equal(DispatchAdmissionChain.Eligible, await ResolveAsync(replay));
        Assert.Equal(first.Route!.RouteEvidenceId, replay.Route!.RouteEvidenceId);

        string swapped = MapStationResolver.BuildRouteEvidenceId(
            Map, new RouteEndpoints { Origin = Machine, Destination = Staging }, "N1-1", "EQP-01");
        Assert.NotEqual(swapped, first.Route.RouteEvidenceId);

        await using TaskTypeStationPersistenceFixture fixture = await TaskTypeStationPersistenceFixture.CreateAsync();
        JourneyRuntimeRow accepted = await AcceptAsync(fixture, first);
        JourneyRuntimeRow replayed = await AcceptAsync(fixture, replay);
        Assert.Equal(accepted.RouteEvidenceId, replayed.RouteEvidenceId);

        DispatchCandidateEvaluation swappedReplay = Evaluation(TransportTaskTypes.StagingToWire);
        swappedReplay.Route = replay.Route with { RouteEvidenceId = swapped };
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => AcceptAsync(fixture, swappedReplay));
    }

    private static DispatchCandidateEvaluation Evaluation(string workType)
    {
        string demandId = $"DEMAND-{workType}";
        AcceptedDemandSnapshot candidate = new(
            demandId,
            $"SUBLOT-001|{workType}",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            $"SERIES-{demandId}",
            workType,
            "SUBLOT-001",
            1,
            Now.AddMinutes(-10),
            Now.AddMinutes(-9),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now.AddMinutes(-10), "PDFN5×6-8L(12R)"));
        DispatchRoundFacts round = new(
            new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, [candidate]),
            Map,
            new RuleView(),
            new HashSet<string>(StringComparer.Ordinal),
            Now,
            new VehicleDispatchPolicy(
                [], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"));
        return new DispatchCandidateEvaluation(
            candidate,
            round,
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now))
        {
            AreaAssignment = new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"),
        };
    }

    private static Task<string> ResolveAsync(DispatchCandidateEvaluation evaluation) =>
        new StationResolutionCriterion(new MapStationResolver(), Microsoft.Extensions.Options.Options.Create(Options))
            .EvaluateAsync(evaluation, Token);

    /// <summary>Accepts the evaluated candidate the way the engine does and returns the journey row it wrote.</summary>
    private static async Task<JourneyRuntimeRow> AcceptAsync(
        TaskTypeStationPersistenceFixture fixture,
        DispatchCandidateEvaluation evaluation)
    {
        JourneyExecutionPlan plan = Plan(evaluation);
        AcceptedDemandSnapshot snapshot = evaluation.Candidate;
        await new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            snapshot,
            JourneyPlanBuilder.PickupIntent(plan, snapshot.DemandId, Now),
            plan,
            Token);
        fixture.Context.ChangeTracker.Clear();
        return await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == snapshot.DemandId, Token);
    }

    private static JourneyExecutionPlan Plan(DispatchCandidateEvaluation evaluation) =>
        new JourneyPlanBuilder(Options).CreatePlan(
            new FleetVehicle("AGV-1", "BROKERX-0001", 1),
            new EligibleDispatchCandidate(evaluation.Candidate, evaluation.Route!, 1, [1], Now),
            Now);

    /// <summary>The factory rules: STAGING_TO_WIRE fixed at the origin, everything else at the destination.</summary>
    private sealed class RuleView : IFixedTaskStationView
    {
        public FixedTaskStationResolution Resolve(string taskType) => taskType == TransportTaskTypes.StagingToWire
            ? FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Origin, Staging)
            : FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Destination, Gate);
    }
}
