using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-02（control-server#207）：「按需求找它所属的旅程」「按旅程列它的需求」只有一个答案，读从属需求表
/// （<c>JourneyDemands</c>），不再读旅程行上的锚列 <c>JourneyRuntimes.DemandId</c>。
/// </summary>
/// <remarks>
/// <para>
/// 单需求旅程里两个答案相同，所以现有的单需求测试（以及 <see cref="ZeroChangePin"/> 钉的终结状态与看板输出）证明改走查找口之后
/// 一字不变。这里补的是两者不同的时候：在库里直接造一趟两条需求的旅程（批次7-01 的表允许，引擎还造不出来），
/// 非锚需求也必须找到这趟旅程——旧的直查找不到，这些测试在改动前是红的。
/// </para>
/// </remarks>
public sealed class Batch7DemandJourneyLookupTests
{
    private const string Anchor = "D-7201";
    private const string Further = "D-7202";

    [Fact]
    public async Task AnAnchorDemandFindsTheJourneyItsOwnRowNames()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptAsync(fixture);

        JourneyRuntimeRow journey = await DemandJourneyLookup.JourneyOf(fixture.NewContext(), Anchor)
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyIdentity.ForAnchorDemand(Anchor), journey.JourneyId);
        Assert.Equal(Anchor, journey.DemandId);
    }

    [Fact]
    public async Task ADemandAddedToAJourneyFindsThatJourneyThoughTheJourneyRowNamesAnotherDemand()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        await using ControlServerDbContext reading = fixture.NewContext();

        JourneyRuntimeRow journey = await DemandJourneyLookup.JourneyOf(reading, Further)
            .AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(runtime.JourneyId, journey.JourneyId);
        Assert.Equal(runtime.JourneyId, await DemandJourneyLookup.JourneyIdOf(reading, Further)
            .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [Anchor, Further],
            (await DemandJourneyLookup.DemandIdsOf(reading, runtime.JourneyId)
                .ToArrayAsync(TestContext.Current.CancellationToken)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ARemovedMembershipNoLongerFindsTheJourney()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        await new JourneyMembershipStore(fixture.Context).RemoveDemandAsync(
            runtime.JourneyId, Further, "REDISPATCHED", Batch7JourneyFixture.Now.AddMinutes(1),
            TestContext.Current.CancellationToken);
        await using ControlServerDbContext reading = fixture.NewContext();

        Assert.False(await DemandJourneyLookup.JourneyOf(reading, Further).AnyAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [Anchor],
            await DemandJourneyLookup.DemandIdsOf(reading, runtime.JourneyId).ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The lookup is a composable query, so that the joins and the tracked reads that need it can use it; the batch 7
    /// membership port answers the same question for callers that want a journey id. They must never disagree.
    /// </summary>
    [Theory]
    [InlineData(Anchor)]
    [InlineData(Further)]
    [InlineData("D-NOT-ACCEPTED")]
    public async Task TheLookupAndTheMembershipPortGiveTheSameAnswer(string demandId)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptTwoDemandJourneyAsync(fixture);
        await using ControlServerDbContext reading = fixture.NewContext();

        Assert.Equal(
            await new JourneyMembershipStore(reading).FindJourneyIdByDemandAsync(demandId, TestContext.Current.CancellationToken),
            await DemandJourneyLookup.JourneyIdOf(reading, demandId).SingleOrDefaultAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// <c>WireToGateStore.DecideReadinessAsync</c> holds a vehicle for recovery while an operation of a demand it carries
    /// needs recovery. That join went from the operation's demand to the journey row's anchor, so an operation of any
    /// other demand on the vehicle held nothing.
    /// </summary>
    [Fact]
    public async Task AnOperationOfADemandAddedToAJourneyThatNeedsRecoveryHoldsItsVehicle()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptTwoDemandJourneyAsync(fixture);
        fixture.Context.SessionRecoveries.Add(ReadySession(departureSafe: true));
        fixture.Context.StationOperations.Add(Operation(Further, StationOperationStatus.RecoveryRequired));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SessionReadinessDecision decision = await new WireToGateStore(fixture.NewContext())
            .DecideReadinessAsync(AgvId, SessionGeneration, TestContext.Current.CancellationToken);

        Assert.Equal(SessionReadiness.RecoveryRequired, decision.Readiness);
    }

    /// <summary>
    /// The other half of that join: a door the vehicle reports unlocked is its own command's doing, and not a reason to
    /// hold it, while an operation of a demand it carries is Prepared. Of any demand it carries, not only the anchor.
    /// </summary>
    [Fact]
    public async Task APreparedOperationOfADemandAddedToAJourneyExplainsTheVehiclesOwnOpenLock()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptTwoDemandJourneyAsync(fixture);
        SessionRecoveryRow session = ReadySession(departureSafe: false);
        session.SafetyUnknownPresent = false;
        session.SafetyReasonCodesJson = """["LOCK_NOT_CLOSED"]""";
        fixture.Context.SessionRecoveries.Add(session);
        fixture.Context.StationOperations.Add(Operation(Further, StationOperationStatus.Prepared));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        SessionReadinessDecision decision = await new WireToGateStore(fixture.NewContext())
            .DecideReadinessAsync(AgvId, SessionGeneration, TestContext.Current.CancellationToken);

        Assert.Equal(SessionReadiness.Ready, decision.Readiness);
    }

    /// <summary>
    /// A hold's audit counts the demands of the task type under way (<see cref="TaskTypeInFlightDemands"/>): every demand
    /// an open journey carries, not one per journey.
    /// </summary>
    [Fact]
    public async Task EveryDemandAnOpenJourneyCarriesCountsAsInFlightForItsTaskType()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);

        Assert.Equal(2, await TaskTypeInFlightDemands.CountAsync(
            fixture.NewContext(), runtime.MapId, "WIRE_TO_GATE", TestContext.Current.CancellationToken));
    }

    private const string AgvId = "agv-01";
    private const long SessionGeneration = 3;

    private static Task<JourneyExecutionPlan> AcceptAsync(Batch7JourneyFixture fixture) =>
        Batch7JourneyFixture.AcceptAsync(fixture.Context, Anchor, AgvId, "VK-01", Batch7JourneyFixture.Now);

    /// <summary>
    /// 「已释放待改派」判据的每一个合取项都能单独让它不成立（批次7-10，control-server#215）。
    /// </summary>
    /// <remarks>
    /// 判据有四项：需求没终结、没有生效的归属、有一次移除的原因是释放、那一次之后没有代次更高的归属。每条反例只破坏其中一项，
    /// 所以任何一项写漏或写反，都有一条会红——「放过去了」与「当成孤儿」共用这一处定义，写宽了会把真孤儿也放过。
    /// </remarks>
    [Theory]
    [InlineData("released", true)]
    [InlineData("still-member", false)]
    [InlineData("removed-for-another-reason", false)]
    [InlineData("demand-ended", false)]
    [InlineData("released-then-later-removed-otherwise", false)]
    [InlineData("removed-otherwise-then-released-later", true)]
    [InlineData("released-then-rejoined", false)]
    [InlineData("released-then-rejoined-at-the-same-generation", false)]
    public async Task OnlyADemandWhoseLatestRemovalWasAReleaseIsReleasedForRedispatch(string shape, bool released)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        JourneyRuntimeRow runtime = await AcceptTwoDemandJourneyAsync(fixture);
        JourneyMembershipStore store = new(fixture.Context);
        DateTimeOffset at = Batch7JourneyFixture.Now.AddMinutes(1);
        switch (shape)
        {
            case "released":
                await store.RemoveDemandAsync(runtime.JourneyId, Further, DemandJourneyLookup.ReleasedForRedispatchReason, at, token);
                break;
            case "still-member":
                break;
            case "removed-for-another-reason":
                await store.RemoveDemandAsync(runtime.JourneyId, Further, "SOMETHING_ELSE", at, token);
                break;
            case "demand-ended":
                await store.RemoveDemandAsync(runtime.JourneyId, Further, DemandJourneyLookup.ReleasedForRedispatchReason, at, token);
                (await fixture.Context.AcceptedDemands.SingleAsync(row => row.DemandId == Further, token)).Status =
                    DemandExecutionStatus.Cancelled;
                await fixture.Context.SaveChangesAsync(token);
                break;
            case "released-then-later-removed-otherwise":
                await store.RemoveDemandAsync(runtime.JourneyId, Further, DemandJourneyLookup.ReleasedForRedispatchReason, at, token);
                await AddRemovedMembershipAsync(fixture, runtime, "journey:second", runtime.DispatchGeneration + 1, "SOMETHING_ELSE");
                break;
            case "removed-otherwise-then-released-later":
                await store.RemoveDemandAsync(runtime.JourneyId, Further, "SOMETHING_ELSE", at, token);
                await AddRemovedMembershipAsync(
                    fixture, runtime, "journey:second", runtime.DispatchGeneration + 1, DemandJourneyLookup.ReleasedForRedispatchReason);
                break;
            case "released-then-rejoined":
                await store.RemoveDemandAsync(runtime.JourneyId, Further, DemandJourneyLookup.ReleasedForRedispatchReason, at, token);
                await AddRemovedMembershipAsync(fixture, runtime, "journey:second", runtime.DispatchGeneration + 1, reason: null);
                break;
            case "released-then-rejoined-at-the-same-generation":
                // 「代次严格递增」这个前提被破坏时的形状：只有「没有生效的归属」那一项挡得住它。
                await store.RemoveDemandAsync(runtime.JourneyId, Further, DemandJourneyLookup.ReleasedForRedispatchReason, at, token);
                await AddRemovedMembershipAsync(fixture, runtime, "journey:second", runtime.DispatchGeneration, reason: null);
                break;
        }
        await using ControlServerDbContext reading = fixture.NewContext();

        string[] releasedIds = await DemandJourneyLookup.ReleasedForRedispatch(reading)
            .Select(row => row.DemandId).ToArrayAsync(token);
        string[] orphanCandidates = await DemandJourneyLookup.OrphanCandidates(reading)
            .Select(row => row.DemandId).ToArrayAsync(token);

        Assert.Equal(released, releasedIds.Contains(Further, StringComparer.Ordinal));
        // 锚需求是生效的归属，在哪种形状下都不是待改派的。
        Assert.DoesNotContain(Anchor, releasedIds, StringComparer.Ordinal);
        // 两个集合互补于 open：待改派的不进孤儿候选，open 且不是待改派的必须进（真孤儿照样被孤儿检查看见）。
        bool open = shape != "demand-ended";
        Assert.Equal(open && !released, orphanCandidates.Contains(Further, StringComparer.Ordinal));
    }

    /// <summary>
    /// 另一趟旅程里这条需求的一条归属：<paramref name="reason"/> 为空时是生效的归属（改派进去了），否则是已移除的——
    /// 「改派过又被移除」这种形状库里今天还产不出来。
    /// </summary>
    private static async Task AddRemovedMembershipAsync(
        Batch7JourneyFixture fixture, JourneyRuntimeRow runtime, string journeyId, long generation, string? reason)
    {
        JourneyDemandRow original = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == Further, TestContext.Current.CancellationToken);
        fixture.Context.Set<JourneyDemandRow>().Add(new JourneyDemandRow
        {
            JourneyId = journeyId,
            DemandId = Further,
            PickupStopId = $"{journeyId}|PICKUP",
            UnloadStopId = $"{journeyId}|UNLOAD",
            ExpectedBasketCount = original.ExpectedBasketCount,
            TargetSlotsJson = original.TargetSlotsJson,
            LoadSlotOperationAttemptId = $"{original.LoadSlotOperationAttemptId}-{generation}",
            LoadCommandMessageId = $"{original.LoadCommandMessageId}-{generation}",
            UnloadSlotOperationAttemptId = $"{original.UnloadSlotOperationAttemptId}-{generation}",
            UnloadCommandMessageId = $"{original.UnloadCommandMessageId}-{generation}",
            DispatchZone = runtime.DispatchZone,
            DispatchGeneration = generation,
            Status = JourneyDemandStatuses.PendingLoad,
            AddedAt = Batch7JourneyFixture.Now.AddMinutes(2),
            RemovedAt = reason is null ? null : Batch7JourneyFixture.Now.AddMinutes(3),
            RemovalReason = reason
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JourneyRuntimeRow> AcceptTwoDemandJourneyAsync(Batch7JourneyFixture fixture)
    {
        await AcceptAsync(fixture);
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        await JourneyMembershipSeed.AddFurtherDemandAsync(fixture.Context, runtime, Further);
        return runtime;
    }

    private static StationOperationRow Operation(string demandId, StationOperationStatus status) => new()
    {
        SlotOperationAttemptId = "attempt-" + demandId,
        DemandId = demandId,
        SublotId = "SUBLOT-" + demandId,
        TargetSlotsJson = "[3]",
        OperationType = SlotOperationType.Load,
        ForcedRecoveryGeneration = 0,
        ContentHash = new string('a', 64),
        Status = status,
        CreatedAt = Batch7JourneyFixture.Now
    };

    /// <summary>A session that is Ready on every count this class does not vary.</summary>
    private static SessionRecoveryRow ReadySession(bool departureSafe) => new()
    {
        AgvId = AgvId,
        SessionGeneration = SessionGeneration,
        ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
        ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        ProfileId = ProtocolCandidateIdentity.ProfileId,
        ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        CapabilityRevision = 1,
        SafetyRevision = 1,
        DepartureSafe = departureSafe,
        RecoveryReportId = "b0000000-0000-4000-8000-000000007201",
        ForcedRecoveryGeneration = 0,
        ReportedForcedRecoveryGeneration = 0,
        ProvenRecoveryCheckpoint = "NONE",
        ActiveUnlockSlotsJson = "[]",
        PendingAttemptIdsJson = "[]",
        PendingResultIdsJson = "[]",
        Readiness = SessionReadiness.Ready,
        ReasonCode = "READY",
        UpdatedAt = Batch7JourneyFixture.Now
    };
}
