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
    /// The lookup sits behind the check that AREA is present and immediately ahead of the first criterion that
    /// decides on AREA, in both the chain the tests assemble and the one the host registers.
    /// </summary>
    /// <remarks>
    /// 「紧跟在 <c>RequiredMesFactsCriterion</c> 后面」在批次7-06（control-server#211）之后不再成立，也不再是
    /// 要守的东西：<c>SublotTaskTypeConflictCriterion</c>（Order 35）插在了两者之间。它判的是同一份快照里一个
    /// Sublot 命中了几种任务类型，一个 AREA 的字都不读，所以这条接缝真正的保证——<b>AREA 存在性检查在前、
    /// 第一个对 AREA 做决定的判据在后</b>——一字未变。下面因此改成比相对次序，再加一条「紧跟在它后面的仍是
    /// <c>AreaScopeCriterion</c>」：那一头才是「第一个对 AREA 做决定的」这句话的所在。
    /// </remarks>
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
                    NullLogger<SlotCapacityCriterion>.Instance,
                    suppressions: null!,
                    dbContext: null!,
                    chargingPolicy: TestChargingPolicies.AllApproved)
                .OrderBy(criterion => criterion.Order)
                .Select(criterion => criterion.GetType().Name)
        ];
        int lookup = Array.IndexOf(order, nameof(AreaAssignmentLookupCriterion));

        Assert.True(lookup > 0, "The default chain does not contain the area assignment lookup.");
        int requiredMesFacts = Array.IndexOf(order, nameof(RequiredMesFactsCriterion));
        Assert.True(
            requiredMesFacts >= 0 && requiredMesFacts < lookup,
            $"The AREA presence check must run before the lookup; the chain is {string.Join(" -> ", order)}.");
        // 区间里装了什么，逐条点名。只断「在前面」的话，任何判据都可以插进这两者之间而不被发现——
        // 包括一条读 AREA 的判据，而这条接缝守的恰恰是「第一个对 AREA 做决定的判据在查找之后」。
        // 名单要变是正常的（批次7-06 就往里加了一条），变的时候有人看见才是这条用例的作用。
        Assert.Equal(
            [nameof(SublotTaskTypeConflictCriterion)],
            order[(requiredMesFacts + 1)..lookup]);
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
                new SingleStationView(new RiotMapStation(210, "关卡")),
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

    /// <summary>
    /// 每条派车判据的 <see cref="IDispatchAdmissionCriterion.Order"/> 唯一，除非列在下面的白名单里（control-server#400 审查 S5）。
    /// 链按 Order 排序，两条判据同号时谁先判由 <c>OrderBy</c> 的稳定排序与注册先后决定——结构性告警按判据次序分类，这种次序不该靠碰巧。
    /// 本票合入 cs#389 时就撞过一次：空闲返回承诺判据与投运判据都取了 16。
    /// </summary>
    /// <remarks>
    /// 白名单里的三组并列是本票之前就有的，各自写了为什么无害。新加一个并列就要在这里写明理由。扫的是宿主程序集里全部实现，不是某条链。
    /// </remarks>
    [Fact]
    public void EveryDispatchCriterionHasItsOwnOrderUnlessTheTieIsListed()
    {
        (int Order, string[] Criteria)[] allowedTies =
        [
            // The lookup only records the AREA's assignment and never refuses, so whichever of the two runs first, the verdict is the
            // conflict's. The chain the tests assemble lists the conflict first; the host registers it after the lookup.
            (35, [nameof(AreaAssignmentLookupCriterion), nameof(SublotTaskTypeConflictCriterion)]),
            // Never in one chain: the en-route chain replaces the idle chain's dynamic facts with its own (control-server#211).
            (80, [nameof(InTransitVehicleFactsCriterion), nameof(VehicleDynamicFactsCriterion)]),
            // Independent of each other: the loading phase is the en-route chain's, the single occupancy a public station's.
            (99, [nameof(FixedStationSingleOccupancyCriterion), nameof(LoadingPhaseOpenCriterion)]),
        ];

        Type[] criteria =
        [
            .. typeof(IDispatchAdmissionCriterion).Assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IDispatchAdmissionCriterion).IsAssignableFrom(type))
        ];
        Assert.Contains(typeof(ChargingPolicyCommissioningCriterion), criteria);
        // Order is an expression-bodied constant on every criterion, so an uninitialised instance answers it.
        (int Order, string[] Criteria)[] ties =
        [
            .. criteria
                .GroupBy(type => ((IDispatchAdmissionCriterion)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type)).Order)
                .Where(group => group.Count() > 1)
                .OrderBy(group => group.Key)
                .Select(group => (group.Key, group.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray()))
        ];

        Assert.Equal(
            allowedTies.Select(tie => $"{tie.Order}: {string.Join(", ", tie.Criteria)}"),
            ties.Select(tie => $"{tie.Order}: {string.Join(", ", tie.Criteria)}"));
    }
}
