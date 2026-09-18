using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// The seams batch 4 places in the dispatch chain before any feature ticket changes a dispatch result
/// (control-server#69): the reason code names, the area assignment lookup step, and the replay comparison
/// of what a plan froze.
/// </summary>
/// <remarks>
/// Nothing here changes whether a candidate is admitted. What is pinned is that the facts the feature
/// tickets #72, #73 and #74 will decide on are present, read once, and compared on replay.
/// </remarks>
public sealed class DispatchChainSeamTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The four reason codes the slot group work reports, named once so #70, #72, #73 and #74 do not each
    /// invent a spelling.
    /// </summary>
    /// <remarks>
    /// The literals are the contract: the dashboard (#70) maps them to operator text and the structural
    /// classification (#74) keys on them, so a rename is a breaking change and has to show up here.
    /// </remarks>
    [Fact]
    public void TheFourSlotGroupReasonCodesEachHaveTheirOwnName()
    {
        Assert.Equal("SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE", DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable);
        Assert.Equal("EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP", DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup);
        Assert.Equal("VEHICLE_SLOT_MODEL_UNRESOLVED", DispatchReasonCodes.VehicleSlotModelUnresolved);
        Assert.Equal("AREA_SLOT_GROUP_NOT_ASSIGNED", DispatchReasonCodes.AreaSlotGroupNotAssigned);
    }

    // ---- the area assignment lookup ------------------------------------------------------------

    /// <summary>
    /// The demand's AREA is looked up in the table the round read, and the version, the assignment and the
    /// slot group it requires are recorded for the criteria behind it.
    /// </summary>
    [Fact]
    public async Task TheLookupRecordsTheDemandsAssignmentFromTheTableTheRoundReadAndBlocksNothing()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            "N1-1",
            Table(3, new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"), new AreaAssignment("N1-5", "MAP-25-WIRE_TO_GATE", "REAR")));

        string reason = await new AreaAssignmentLookupCriterion().EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Equal(new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"), evaluation.AreaAssignment);
        Assert.Equal(3, evaluation.AreaAssignmentVersion);
        Assert.Equal("FRONT", evaluation.RequiredSlotPosition);
    }

    /// <summary>
    /// An AREA the table does not name is recorded as unassigned against the version that was consulted, and
    /// passes: refusing it is the whitelist's decision (#72), not this step's.
    /// </summary>
    [Fact]
    public async Task AnAreaTheTableDoesNotNameIsRecordedAsUnassignedButNotBlockedHere()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            "T3-7",
            Table(3, new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT")));

        string reason = await new AreaAssignmentLookupCriterion().EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Null(evaluation.AreaAssignment);
        Assert.Equal(3, evaluation.AreaAssignmentVersion);
        Assert.Null(evaluation.RequiredSlotPosition);
    }

    /// <summary>
    /// A server that has never imported a table decides exactly as it did before the table existed: no
    /// version, no assignment, no block.
    /// </summary>
    [Fact]
    public async Task WithNoTableEverImportedTheLookupRecordsNothingAndBlocksNothing()
    {
        DispatchCandidateEvaluation evaluation = Evaluation("N1-1", table: null);

        string reason = await new AreaAssignmentLookupCriterion().EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Null(evaluation.AreaAssignment);
        Assert.Null(evaluation.AreaAssignmentVersion);
        Assert.Null(evaluation.RequiredSlotPosition);
    }

    /// <summary>
    /// The lookup sits right behind the check that AREA is present and ahead of the first criterion that
    /// decides on AREA, in both the chain the tests assemble and the one the host registers.
    /// </summary>
    [Fact]
    public void TheLookupRunsAfterTheRequiredMesFactsAndBeforeTheFirstCriterionThatDecidesOnTheArea()
    {
        string[] order =
        [
            .. DispatchAdmissionCriteria.Default(
                    Options.Create(new JourneyRuntimeOptions()),
                    new MapStationResolver(),
                    packageCapacityStore: null!,
                    store: null!,
                    faultStore: null!,
                    boxCountReader: null!,
                    NullLogger<SlotCapacityCriterion>.Instance)
                .OrderBy(criterion => criterion.Order)
                .Select(criterion => criterion.GetType().Name)
        ];
        int lookup = Array.IndexOf(order, nameof(AreaAssignmentLookupCriterion));

        Assert.True(lookup > 0, "The default chain does not contain the area assignment lookup.");
        Assert.Equal(nameof(RequiredMesFactsCriterion), order[lookup - 1]);
        Assert.Equal(nameof(AreaScopeCriterion), order[lookup + 1]);

        ServiceCollection services = new();
        services.AddDispatchAdmission();
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IDispatchAdmissionCriterion)
            && descriptor.ImplementationType == typeof(AreaAssignmentLookupCriterion));
    }

    // ---- replaying an acceptance ---------------------------------------------------------------

    /// <summary>
    /// Replaying an acceptance whose plan carries the version the demand froze, and the slot group that
    /// version assigns its AREA, is the same acceptance.
    /// </summary>
    /// <remarks>
    /// The journey row has no column for either field: #66 kept them off <c>JourneyRuntimes</c>, whose key
    /// batch 7 rewrites, and put the frozen version in <c>ConfigurationConsumerBindings</c>. That binding plus
    /// the immutable table version is therefore the journey's durable record of both. Since #72 the acceptance
    /// transaction writes the freeze itself; freezing again by hand afterwards is idempotent.
    /// </remarks>
    [Fact]
    public async Task AReplayCarryingTheFrozenVersionAndItsSlotGroupIsTheSameAcceptance()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        AreaAssignmentTableVersion table = await fixture.AreaAssignments.WriteVersionAsync(
            [new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT")], Now, TestContext.Current.CancellationToken);
        WireToGateStore store = new(fixture.Context);
        JourneyExecutionPlan plan = Plan() with { AreaAssignmentVersion = table.Version, RequiredSlotPosition = "FRONT" };
        await store.AcceptWithOrderIntentAsync(Demand("N1-1"), PickupIntent(), plan, TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync(DemandId, table.Version, Now, TestContext.Current.CancellationToken);

        await store.AcceptWithOrderIntentAsync(Demand("N1-1"), PickupIntent(), plan, TestContext.Current.CancellationToken);

        Assert.Single(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A plan that carries no area assignment, replayed over a demand that froze none, replays exactly as it
    /// did before these fields existed.
    /// </summary>
    [Fact]
    public async Task AReplayCarryingNoAreaAssignmentOverADemandThatFrozeNoneIsTheSameAcceptance()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        WireToGateStore store = new(fixture.Context);
        await store.AcceptWithOrderIntentAsync(Demand("N1-1"), PickupIntent(), Plan(), TestContext.Current.CancellationToken);

        await store.AcceptWithOrderIntentAsync(Demand("N1-1"), PickupIntent(), Plan(), TestContext.Current.CancellationToken);

        Assert.Single(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A replay naming another version, another slot group, or no assignment at all, over a demand that froze
    /// one, is a different acceptance and is refused the way any other changed plan field is.
    /// </summary>
    [Fact]
    public async Task AReplayNamingAnotherVersionOrAnotherSlotGroupIsADifferentAcceptance()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        AreaAssignmentTableVersion first = await fixture.AreaAssignments.WriteVersionAsync(
            [new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT")], Now, TestContext.Current.CancellationToken);
        WireToGateStore store = new(fixture.Context);
        JourneyExecutionPlan plan = Plan() with { AreaAssignmentVersion = first.Version, RequiredSlotPosition = "FRONT" };
        await store.AcceptWithOrderIntentAsync(Demand("N1-1"), PickupIntent(), plan, TestContext.Current.CancellationToken);
        // Imported after the acceptance: before it, the acceptance itself would have been refused.
        AreaAssignmentTableVersion second = await fixture.AreaAssignments.WriteVersionAsync(
            [new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT")], Now.AddMinutes(1), TestContext.Current.CancellationToken);

        JourneyExecutionPlan[] different =
        [
            plan with { AreaAssignmentVersion = second.Version },
            plan with { RequiredSlotPosition = "REAR" },
            plan with { AreaAssignmentVersion = null, RequiredSlotPosition = null },
        ];
        foreach (JourneyExecutionPlan replay in different)
        {
            await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.AcceptWithOrderIntentAsync(
                Demand("N1-1"), PickupIntent(), replay, TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// A plan claiming a version the demand never froze is a different acceptance too: nothing durable says
    /// the original acceptance was judged against that version.
    /// </summary>
    [Fact]
    public async Task AReplayClaimingAVersionTheDemandNeverFrozeIsADifferentAcceptance()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        AreaAssignmentTableVersion table = await fixture.AreaAssignments.WriteVersionAsync(
            [new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT")], Now, TestContext.Current.CancellationToken);
        WireToGateStore store = new(fixture.Context);
        await store.AcceptWithOrderIntentAsync(Demand("N1-1"), PickupIntent(), Plan(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.AcceptWithOrderIntentAsync(
            Demand("N1-1"),
            PickupIntent(),
            Plan() with { AreaAssignmentVersion = table.Version, RequiredSlotPosition = "FRONT" },
            TestContext.Current.CancellationToken));
    }

    private static AreaAssignmentTableVersion Table(long version, params AreaAssignment[] assignments) => new(
        version,
        new string('a', 64),
        $"snapshot-{version}",
        Now,
        assignments.ToDictionary(assignment => assignment.Area, StringComparer.Ordinal));

    private static AcceptedDemandSnapshot Demand(string area) => new(
        DemandId,
        "SUBLOT-001|WIRE_TO_GATE",
        7,
        "11111111-1111-4111-8111-111111111111",
        21,
        Now,
        "SERIES-1",
        "WIRE_TO_GATE",
        "SUBLOT-001",
        1,
        Now.AddMinutes(-10),
        Now.AddMinutes(-9),
        "TRACE-1",
        "COMMIT-1",
        new LiveMesFieldSet(area, "EQP-01", "STEP-01", Now, "PDFN5×6-8L(12R)"));

    private static OrderIntent PickupIntent() => new(
        "pickup-leg",
        DemandId,
        $"W2G-{DemandId}-PICKUP-1",
        "TO_PICKUP",
        "N1-1",
        Now,
        "BROKERX-0001",
        25,
        12,
        1,
        1);

    private static JourneyExecutionPlan Plan() => new(
        "AGV-1",
        "BROKERX-0001",
        1,
        25,
        "MAP-25",
        "MAP-25-WIRE_TO_GATE",
        "MAPCAT-1",
        "N1-1",
        12,
        "关卡",
        210,
        2,
        [1, 2],
        "operation-session",
        "pickup-leg",
        $"W2G-{DemandId}-PICKUP-1",
        "gate-leg",
        $"W2G-{DemandId}-GATE-1",
        1,
        Now);

    private static DispatchCandidateEvaluation Evaluation(string area, AreaAssignmentTableVersion? table)
    {
        AcceptedDemandSnapshot candidate = Demand(area);
        return new DispatchCandidateEvaluation(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate]),
                new RiotMapStationCatalogSnapshot(25, Now, new string('c', 64), []),
                new RiotMapStation(210, "关卡"),
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"),
                table),
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now));
    }
}
