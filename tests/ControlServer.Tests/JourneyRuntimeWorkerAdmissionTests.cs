using System.Text.Json.Nodes;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Split out of the one partial JourneyRuntimeWorkerTests class so xunit runs it in parallel with the
/// rest (control-server#130); the shared fixture is <see cref="JourneyRuntimeWorkerTestKit"/>.
/// </summary>
public sealed class JourneyRuntimeWorkerAdmissionTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task HardAdmissionFiltersBeforeStableBacklogOrderingAndRemoteSideEffects()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot wrongWorkType = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "IGNORED", Now.AddMinutes(-30)) with
        {
            WorkType = "OTHER",
            TransportDemandKey = "IGNORED|OTHER"
        };
        AcceptedDemandSnapshot laterId = fixture.Demand(
            "10000000-0000-4000-8000-000000000003", "SUBLOT-003", Now.AddMinutes(-20));
        AcceptedDemandSnapshot stableWinner = fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-20));
        fixture.Catalog.Set(wrongWorkType, laterId, stableWinner);
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        fixture.BoxCounts.Set("SUBLOT-003", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(stableWinner.DemandId, (await fixture.RuntimeAsync()).DemandId);
        Assert.Equal(1, await fixture.Context.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow[] backlog = await fixture.Context.JourneyBacklog
            .OrderBy(row => row.DemandId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, backlog.Length);
        Assert.Equal("OUT_OF_SCOPE_WORK_TYPE", backlog[0].ReasonCode);
        Assert.Equal("ACCEPTED", backlog[1].ReasonCode);
        Assert.Equal("ELIGIBLE", backlog[2].ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task VolatileCatalogReadMetadataDoesNotOverwriteCurrentBacklogReason()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyBacklogRow initial = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", initial.ReasonCode);
        string initialFingerprint = initial.DecisionFingerprint;

        fixture.Catalog.Set(demand with
        {
            CatalogRevision = demand.CatalogRevision + 1,
            AcceptedAt = demand.AcceptedAt.AddMinutes(1)
        });
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow volatileRefresh = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", volatileRefresh.ReasonCode);
        Assert.Equal(initialFingerprint, volatileRefresh.DecisionFingerprint);

        AcceptedDemandSnapshot changedDecision = demand with
        {
            CatalogRevision = demand.CatalogRevision + 2,
            AcceptedAt = demand.AcceptedAt.AddMinutes(2),
            DemandRevision = demand.DemandRevision + 1
        };
        fixture.Catalog.Set(changedDecision);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow changed = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("DEMAND_DECISION_FACT_CHANGED", changed.ReasonCode);
        Assert.NotEqual(initialFingerprint, changed.DecisionFingerprint);
        string changedFingerprint = changed.DecisionFingerprint;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow stableAgain = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", stableAgain.ReasonCode);
        Assert.Equal(changedFingerprint, stableAgain.DecisionFingerprint);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task DemandLeavingTheCatalogUnacceptedStopsReadingAsWaitingBacklog()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot leaving = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        AcceptedDemandSnapshot staying = fixture.Demand(
            "10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-10));
        fixture.Catalog.Set(leaving, staying);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        DateTimeOffset lastInCatalog = (await fixture.BacklogAsync(leaving.DemandId)).LastSeenAt;

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Catalog.Set(staying);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow gone = await fixture.BacklogAsync(leaving.DemandId);
        Assert.Equal(DispatchReasonCodes.DemandLeftCatalog, gone.ReasonCode);
        Assert.Null(gone.AcceptedAt);
        // Last seen means last seen in the catalog: a later round that only confirms the absence
        // does not make the row look freshly evaluated.
        Assert.Equal(lastInCatalog, gone.LastSeenAt);
        JourneyBacklogRow waiting = await fixture.BacklogAsync(staying.DemandId);
        Assert.Equal("BATTERY_FACT_UNKNOWN", waiting.ReasonCode);
        Assert.Equal(fixture.Clock.GetUtcNow(), waiting.LastSeenAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task DemandReturningToTheCatalogIsJudgedAgainAndKeepsItsFirstSeen()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        DateTimeOffset firstSeen = (await fixture.BacklogAsync(demand.DemandId)).FirstSeenAt;
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Catalog.Set();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            DispatchReasonCodes.DemandLeftCatalog, (await fixture.BacklogAsync(demand.DemandId)).ReasonCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        fixture.Catalog.Set(demand);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow back = await fixture.BacklogAsync(demand.DemandId);
        Assert.Equal("BATTERY_FACT_UNKNOWN", back.ReasonCode);
        Assert.Equal(firstSeen, back.FirstSeenAt);
        Assert.Equal(fixture.Clock.GetUtcNow(), back.LastSeenAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task UnreadableCatalogDoesNotMarkBacklogAsLeft()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        fixture.Catalog.BeforeRead = _ => throw new HttpRequestException("MesIngest unreachable");
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("BATTERY_FACT_UNKNOWN", (await fixture.BacklogAsync(demand.DemandId)).ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task LargeCatalogBatchesBacklogPersistenceBeforeAcceptingEligibleJourney()
    {
        Assert.InRange(await SavesForARoundAsync(outOfScopeCount: 250), 1, 19);
    }

    /// <summary>
    /// 一轮的保存次数不随目录规模增长：五十条候选与二百五十条候选，保存次数一模一样。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上面那条用一个区间说同一件事，而区间是个魔数——它随每批新增的固定写入往上挪过好几次（FP-C13 挪到 13，
    /// B2 挪到 16，批次 4 与批次 6 各挪一格，批次7-06 挪到 19）。每挪一次，「批量」这个保证就被稀释一点：
    /// 只看区间，一份「每条候选保存 0.05 次」的实现在小目录上也能过。
    /// </para>
    /// <para>
    /// <b>这一条直接断言那个不变量。</b>两个规模差二百条候选，保存次数必须相等；只要有任何一处按候选保存，
    /// 哪怕只是偶尔，这里立刻就是两个不同的数。批次7-06（control-server#211）把轮次翻成任务优先之后，
    /// 循环的形状从「按车」变成了「按候选」，按候选保存正是这次改动最容易滑进去的退化——写这一条的直接原因
    /// 就是它真的滑进去过一次，二百五十一条候选跑出了 269 次保存。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task TheNumberOfSavesInARoundDoesNotGrowWithTheCatalog()
    {
        Assert.Equal(
            await SavesForARoundAsync(outOfScopeCount: 50),
            await SavesForARoundAsync(outOfScopeCount: 250));
    }

    /// <summary>
    /// 跑一轮：<paramref name="outOfScopeCount"/> 条不在范围内的候选加一条合格的，返回这一轮的保存次数。
    /// </summary>
    /// <remarks>顺带把这一轮该有的结果断言掉——合格那条被接走、每条候选都有积压行、只建了一个订单。</remarks>
    private static async Task<int> SavesForARoundAsync(int outOfScopeCount)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot[] outOfScope = Enumerable.Range(1, outOfScopeCount)
            .Select(index => fixture.Demand(
                $"20000000-0000-4000-8000-{index:D12}",
                $"IGNORED-{index:D3}",
                Now.AddMinutes(-20)) with
            {
                WorkType = "OTHER",
                TransportDemandKey = $"IGNORED-{index:D3}|OTHER"
            })
            .ToArray();
        AcceptedDemandSnapshot eligible = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10));
        fixture.Catalog.Set([.. outOfScope, eligible]);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.SaveChanges.Reset();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(eligible.DemandId, (await fixture.RuntimeAsync()).DemandId);
        Assert.Equal(outOfScopeCount + 1, await fixture.Context.JourneyBacklog.CountAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
        return fixture.SaveChanges.Count;
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task VehicleReadsThatAdvanceClockUsePostReadTimeForAdmission()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10));
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        DateTimeOffset beforeVehicleReads = fixture.Clock.GetUtcNow();
        fixture.Riot.BeforeReadVehicle = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        AcceptedDemandRow accepted = await fixture.DemandRowAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        OrderIntentRow pickup = await fixture.Context.OrderIntents.AsNoTracking().SingleAsync(
            row => row.Purpose == "TO_PICKUP",
            TestContext.Current.CancellationToken);
        Assert.Equal(demand.DemandId, accepted.DemandId);
        Assert.Equal(DemandExecutionStatus.Accepted, accepted.Status);
        Assert.Equal(demand.DemandId, runtime.DemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal(demand.DemandId, pickup.DemandId);
        Assert.Equal("CONFIRMED", pickup.Status);
        Assert.Equal("ORDER-TO_PICKUP", pickup.OrderId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
        Assert.True(fixture.Clock.GetUtcNow() > beforeVehicleReads);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("ACCEPTED", backlog.ReasonCode);
        Assert.NotNull(backlog.AcceptedAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task SnapshotsFromEarlierInALiveSessionStillAdmit()
    {
        // Onboard sends CapabilitySnapshot and SafetyStateSnapshot once per session and the
        // protocol mandates no cadence, so their payload age is not evidence of anything. As long
        // as the session is still being heard from, admission must proceed.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AgeOnboardSnapshotPayloadsAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ACCEPTED", (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AdmissionSafetyFactsComeFromTheLatestChangeNotTheSessionSnapshot()
    {
        // Onboard sends SafetyStateSnapshot once per session and reports every later change with
        // SafetyStateChanged. A session established while the vehicle is still moving therefore
        // carries a snapshot that says so for as long as the session lives, and reading admission
        // facts from that snapshot alone left vehicleStopped false forever: the vehicle could stop
        // at the station and still never be admitted, while the session row -- which
        // SafetyStateChanged does update -- correctly showed Ready.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.EstablishSessionWhileVehicleIsMovingAsync();
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: true);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "ACCEPTED",
            (await fixture.BacklogAsync("10000000-0000-4000-8000-000000000001")).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task AMidSessionReadingSnapshotDoesNotChangeWhichSlotsDispatchCountsAsAvailable()
    {
        // control-server#142: the dashboard asks Onboard for a SafetyStateSnapshot mid-session to show an overdue
        // slot's readings. That snapshot reads slots as the IO sees them now -- occupied, unlocked -- and slot
        // availability is a session baseline the reservation ledger is built against, not live IO. Taking it
        // from the newest snapshot would strand every slot a journey touched for the rest of the session.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AddMidSessionSafetySnapshotAsync(8);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "ACCEPTED",
            (await fixture.BacklogAsync("10000000-0000-4000-8000-000000000001")).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-15")]
    public async Task TheBaselineSnapshotIsTheSessionsLowestSafetyVersionEvenWhenTheServerClockSaysOtherwise()
    {
        // control-server#142 review: the baseline is the snapshot with the session's lowest safetyStateVersion. The
        // vehicle allocates versions under one lock and they are what orders its safety facts; the server's receive
        // time is not, and a clock stepped back between the two snapshots would make the mid-session answer look
        // first.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AddMidSessionSafetySnapshotAsync(8, receivedBeforeBaseline: true);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            "ACCEPTED",
            (await fixture.BacklogAsync("10000000-0000-4000-8000-000000000001")).ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ArrivalIsNotTrustedWhileTheLatestSafetyStateSaysTheVehicleIsMoving()
    {
        // The same staleness in the other direction, and the dangerous one: the session-start
        // snapshot says the vehicle is stopped, so every later arrival check went on believing it
        // after Onboard had reported the vehicle moving. Arrival must be judged against the newest
        // safety state, and departureSafe is not a proxy for it -- a moving vehicle can still
        // report that nothing blocks a departure, which is why the session row alone is not enough.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: false);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);

        await fixture.AddSafetyStateChangedAsync(9, departureSafe: true, vehicleStopped: true);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ArrivalIsNotTrustedWhenOnboardReportsTheVehicleMovingDuringTheSameIteration()
    {
        // The test above hands the engine a settled world: the safety change is already stored
        // before ExecuteOnceAsync is called. On the rig it lands while an iteration is running, and
        // on the transport's scope rather than the engine's -- and AdvanceAsync had already read the
        // session row at the top of the iteration on a tracking query, so every later read of that
        // row in the same iteration returned the tracked instance with the revision it carried when
        // the iteration began. The arrival check pins the safety summary to that revision, found the
        // previous message, which still said the vehicle was stopped, and trusted an arrival Onboard
        // had already contradicted. Caught by L2 session-established-while-moving in CI run
        // 35055524167 attempt 1; see
        // docs/defects/20260916-arrival-trusted-on-a-session-row-pinned-for-one-iteration.md.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };

        // Each iteration runs in its own scope in the host, so the window under test has to be the
        // one inside a single iteration and not a tracked row carried over from the previous one.
        await fixture.RecreateEngineAsync();

        // ReadVehicleAsync is called from CheckArrivalAsync, between the session read at the top of
        // AdvanceAsync and the safety facts that judge the arrival -- exactly the window the rig hit.
        fixture.Riot.BeforeReadVehicle = () => fixture
            .ApplySafetyStateChangedOnAnotherScopeAsync(8, departureSafe: true, vehicleStopped: false)
            .GetAwaiter().GetResult();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        fixture.Riot.BeforeReadVehicle = null;

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);

        // And the arrival is still trusted once Onboard reports the vehicle at rest, so reading the
        // row afresh does not simply strand the journey.
        await fixture.ApplySafetyStateChangedOnAnotherScopeAsync(9, departureSafe: true, vehicleStopped: true);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ABlockedJourneyKeepsTheReasonItWasBlockedForWhenTheSessionDrops()
    {
        // A station operation that times out blocks the journey and, per ADR-cross-0006 and
        // ADR-cross-0015, holds it there until a human drives the recovery handshake. The vehicle
        // is normally powered down for that repair, so the session leaves Ready -- and overwriting
        // BlockReasonCode with ONBOARD_SESSION_NOT_READY on the way through destroyed the only
        // record of why the journey is blocked and which recovery it is waiting for. Session
        // readiness is reported on its own row; the block reason is not recoverable once lost.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplyTimedOutResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", blocked.BlockReasonCode);

        await fixture.DropOnboardSessionAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow afterDrop = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, afterDrop.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", afterDrop.BlockReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AResultThatTurnsTheSessionRecoveryRequiredStillBlocksTheJourneyForItsOwnReason()
    {
        // 147f02c made a refused result move the session to RecoveryRequired, so the vehicle would
        // finally be told it needs recovery. AdvanceAsync reads the same row and returns early on
        // any session that is not Ready -- so that change also stops the runtime one step short of
        // the Blocked transition it was supposed to enable, with the result already durable in the
        // database. The recovery the journey is waiting for never gets named.
        //
        // Seeded from what L2 run 20260904-recovery-entry-after-announce-001 actually finished
        // with: OperationResults holding the refused load, StationOperations at RecoveryRequired,
        // SessionRecoveries at RecoveryRequired / OPERATION_RECOVERY_REQUIRED, and JourneyRuntimes
        // still reading AwaitingLoadResult / ONBOARD_SESSION_NOT_READY.
        //
        // A session in recovery is not an absent session. The vehicle is connected, the server
        // authorises recovery actions against this exact state, and the operator's entry on the
        // onboard HMI opens on it (8005-agv-onboard-hmi:
        // RecoveryRequiredAnnouncedOnAResultAckOpensTheRecoveryEntry). Refusing to name the block
        // is what leaves a stopped vehicle with nothing to recover from.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplyTimedOutResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load);
        await fixture.MarkSessionRecoveryRequiredByOperationAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow blocked = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.Blocked, blocked.Stage);
        Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", blocked.BlockReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ABlockedJourneyKeepsTheVehicleOutOfEveryOtherDemand()
    {
        // The single active slot is what makes the one-vehicle runtime safe. A blocked journey is
        // unresolved, not finished: the physical state of the slots is unproven, the dispatch
        // lease is still held, and admitting another demand would send the vehicle away on it. So
        // this is intended behaviour, recorded here so that no later change to Blocked quietly
        // turns it into a free vehicle. The way out is the recovery handshake, not discovery.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AdvanceToLoadResultAsync();
        await fixture.ApplyTimedOutResultAsync(
            await fixture.OperationAsync(SlotOperationType.Load), SlotOperationType.Load);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Blocked, (await fixture.RuntimeAsync()).Stage);

        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)),
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task InboundEnvelopesWithoutASessionGenerationDoNotBreakLiveness()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.AddInboxRowWithoutSessionGenerationAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ACCEPTED", (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task SupportsBatchUnlockFalseDoesNotBlockAdmission()
    {
        // The protocol declares supportsBatchUnlock with no semantics -- a bare boolean, unchanged
        // from protocol-v0.1.1 through the v2 candidate -- and its own canonical example sets it
        // false. Whether the vehicle can operate a given slot set is decided
        // against AvailableSlots when the command is sent, not by this flag.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.ClearSupportsBatchUnlockAsync();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ACCEPTED", (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task CandidateProcessingThatExpiresDynamicFactsDoesNotAcceptOrDispatch()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.BoxCounts.BeforeRead = () => fixture.Clock.Advance(TimeSpan.FromSeconds(31));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("FINAL_DYNAMIC_FACTS_NOT_READY", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task FinalCatalogRefreshThatExpiresDynamicFactsDoesNotAcceptOrDispatch()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(30);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.Catalog.BeforeRead = readCount =>
        {
            if (readCount == 2)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(31));
            }
        };
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("FINAL_DYNAMIC_FACTS_NOT_READY", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task UnknownOrStaleDynamicFactsFailClosedWithoutAcceptingOrDispatching()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = null };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.ProtocolOutbox.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("BATTERY_FACT_UNKNOWN", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
    }

    [Theory]
    [InlineData("N1-1", "EQP-01", "N1-1", 12)]
    [InlineData("N1-2", "EQP-02", "N1-2_N1-3", 13)]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task DynamicMapPickupResolutionFreezesEveryMatchingDemandStation(
        string area,
        string eqp,
        string expectedStationName,
        int expectedStationId)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            area,
            eqp));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        OrderIntentRow pickup = await fixture.Context.OrderIntents.SingleAsync(
            row => row.Purpose == "TO_PICKUP", TestContext.Current.CancellationToken);
        Assert.Equal(expectedStationName, runtime.PickupStationId);
        Assert.Equal(expectedStationId, runtime.PickupStationRiotId);
        Assert.Equal(expectedStationName, pickup.TargetStationId);
        Assert.Equal(expectedStationId, pickup.DestinationStationId);
        Assert.Equal(25, pickup.MapId);
        Assert.Matches("^MAPCAT-[0-9a-f]{64}$", runtime.RouteEvidenceId);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AnUncommissionedCatalogCreatesNothingAtAll()
    {
        // REQ-0302's hard block, in the place it has to hold: a whole runtime iteration against a
        // server whose two approved values were never configured. The server runs, polls, and
        // writes the demand into the backlog under the block's own name -- and creates no journey
        // and no RIoT order. Specification 8.6 asks for exactly this negative evidence: take the
        // parameters away and show that it really blocks.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(catalogApproved: false);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("CATALOG_PARAMETERS_NOT_APPROVED", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        // The station was never resolved either: REQ-0303 forbids resolving execution stations for
        // a new demand while the catalog is unusable, not merely acting on the result.
        Assert.Empty(await fixture.Context.FrozenDemandStations.ToArrayAsync(
            TestContext.Current.CancellationToken));
        // And RIoT's RouteCost was never asked -- the block is ahead of the gate, not inside it.
        Assert.Empty(fixture.RouteCosts.Calls);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AcceptingADemandFreezesBothEndpointsAndALaterRenameDoesNotRewriteThem()
    {
        // REQ-0305: the endpoints are taken from the snapshot that was fresh at creation time and
        // frozen there. A later rename changes the catalog, not the identity of the station this
        // task was already sent to, and it must not re-resolve the task.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        FrozenDemandStationRow[] frozen = await fixture.Context.FrozenDemandStations
            .OrderBy(row => row.Role)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, frozen.Length);
        FrozenDemandStationRow pickup = frozen.Single(row => row.Role == FrozenStationRole.Pickup);
        FrozenDemandStationRow dropoff = frozen.Single(row => row.Role == FrozenStationRole.Dropoff);
        Assert.Equal(12, pickup.StationId);
        Assert.Equal("N1-1", pickup.StationName);
        Assert.Equal(210, dropoff.StationId);
        Assert.NotEqual(0, pickup.CatalogRevision);

        // The Map renames the pickup station under the running journey.
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(210, "关卡"));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        FrozenDemandStationRow reread = await fixture.Context.FrozenDemandStations
            .SingleAsync(row => row.Role == FrozenStationRole.Pickup, TestContext.Current.CancellationToken);
        Assert.Equal(12, reread.StationId);
        Assert.Equal("N1-1", reread.StationName);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task AStationAddedToTheSharedMapDoesNotStrandCargoAlreadyBoundForTheGate()
    {
        // Map 25 is shared, and RIoT's other users add stations to it without telling this server.
        // Every iteration re-derives the admission policy from the area-named stations on the live
        // map and binds that set to JourneyRuntime:admissionPolicyVersion. One more such station
        // under the same version made ApplyAdmissionPolicyAsync throw before anything else ran, so
        // a loaded vehicle already on its way to the gate was never given its unload -- until
        // someone raised the version and restarted. ADR-cross-0050 and 0051: a policy change
        // affects operations not yet committed, and never interrupts one that is.
        const string first = "10000000-0000-4000-8000-000000000001";
        const string second = "10000000-0000-4000-8000-000000000002";
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(first, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.AdvanceToGateArrivalAsync()).Stage);

        fixture.Riot.SetMapStations(MapWithAStationAdded());
        fixture.Catalog.Set(
            fixture.Demand(first, "SUBLOT-001", Now.AddMinutes(-10)),
            fixture.Demand(second, "SUBLOT-002", Now.AddMinutes(-9)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        fixture.Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };

        // The worker logs an iteration's exception and tries again on the next tick, so what counts
        // is whether the journey moved -- asserted first, so a red run says that, not just the throw.
        Exception? iterationError = await Record.ExceptionAsync(() =>
            fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Null(iterationError);

        // The journey completes under the bound policy; the demand that turned up meanwhile is the
        // first thing the freed vehicle is offered, and the drift is what refuses it.
        StationOperationRow unload = await fixture.OperationAsync(SlotOperationType.Unload);
        await fixture.ApplySafeResultAsync(unload, SlotOperationType.Unload, SlotBusinessState.Empty);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RuntimeAsync()).Stage);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(AdmissionPolicyDriftCriterion.Reason, (await fixture.BacklogAsync(second)).ReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AStationAddedToTheSharedMapTakesOnNoNewDemandUntilThePolicyVersionIsRaised()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        // Binds version 1 to the map as it stands.
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        fixture.Riot.SetMapStations(MapWithAStationAdded());
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            AdmissionPolicyDriftCriterion.Reason,
            (await fixture.BacklogAsync("10000000-0000-4000-8000-000000000001")).ReasonCode);
        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        // The live map is not an import (ADR-cross-0051): version 1 still binds what it was imported with.
        // Read for WIRE_TO_GATE alone: since control-server#163 each station is paired with STAGING_TO_WIRE too.
        Assert.Equal(
            ["N1-1", "N1-2_N1-3"],
            await fixture.Context.StationTaskTypeAdmissions.AsNoTracking()
                .Where(row => row.TaskType == "WIRE_TO_GATE")
                .Select(row => row.StationId)
                .OrderBy(stationId => stationId)
                .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.AdmissionPolicyAudit
            .CountAsync(TestContext.Current.CancellationToken));

        // Raising the version is the deliberate re-import, and it is all that intake waits for.
        fixture.Options.AdmissionPolicyVersion = 2;
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(2, await fixture.Context.AdmissionPolicyAudit
            .CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task AStationAddedToTheSharedMapBeforeArrivalLoadsTheJourneysOwnDemandAndTakesOnNoOther()
    {
        const string first = "10000000-0000-4000-8000-000000000001";
        const string second = "10000000-0000-4000-8000-000000000002";
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(first, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);

        // A second demand at the same station turns up while the vehicle is on its way. A journey
        // here carries one demand and a vehicle on a journey is offered none, so the second one is
        // first scored in the round after this journey completes -- and must be refused there.
        fixture.Riot.SetMapStations(MapWithAStationAdded());
        fixture.Catalog.Set(
            fixture.Demand(first, "SUBLOT-001", Now.AddMinutes(-10)),
            fixture.Demand(second, "SUBLOT-002", Now.AddMinutes(-9)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);

        // The demand the journey already carries loads under the bound policy, gets its gate leg
        // created, and completes at the gate.
        Assert.Equal(JourneyRuntimeStage.Completed, (await fixture.RunToCompletionAsync()).Stage);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first, (await fixture.DemandRowAsync()).DemandId);
        Assert.Equal(AdmissionPolicyDriftCriterion.Reason, (await fixture.BacklogAsync(second)).ReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>The fixture's default map with one more area-named station, the way another RIoT user adds one.</summary>
    private static RiotMapStation[] MapWithAStationAdded() =>
    [
        new RiotMapStation(12, "N1-1"),
        new RiotMapStation(13, "N1-2_N1-3"),
        new RiotMapStation(14, "N1-4"),
        new RiotMapStation(210, "关卡"),
        new RiotMapStation(300, "等待点")
    ];

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task TheCatalogIsConfirmedOnAWholeReadAndOnlyOnAWholeRead()
    {
        // REQ-0302: what a confirmation is. A read that came back whole and whose machine stations
        // parsed is one; a read that threw is an attempt, and an attempt must not move the freshness
        // window. Since control-server#160 a fixed station missing from the Map no longer makes the
        // read an attempt -- that is its task type's admission question alone
        // (TaskTypeAdmissionRuntimeTests) -- so the attempt here is a read that failed outright.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        MapStationCatalogStateRow confirmed = await fixture.Context.MapStationCatalogStates
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MapStationCatalogState.Fresh, confirmed.State);
        Assert.Equal(Now, confirmed.LastCompleteConfirmationAt);
        Assert.Equal(30, confirmed.ApprovedSyncPeriodSeconds);
        Assert.Equal(300, confirmed.ApprovedMaxUnconfirmedSeconds);

        // Now the read fails: that is an attempt, not a confirmation.
        fixture.Riot.FailNextMapRead = new HttpRequestException("RIoT did not answer.");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        MapStationCatalogStateRow afterFailure = await fixture.Context.MapStationCatalogStates
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MapStationCatalogState.RefreshFailed, afterFailure.State);
        Assert.Equal(Now, afterFailure.LastCompleteConfirmationAt);
        Assert.NotNull(afterFailure.LastFailureReason);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AnUnreachablePickupIsRefusedACreateAndSaysWhy()
    {
        // The gate at Order 96, in the runtime. RIoT says the vehicle cannot reach the pickup
        // station, so the demand is not taken -- REQ-0147/REQ-0293's pre-create RouteCost check.
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.RouteCosts.Set(12, -1);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("CREATE_GATE_STATION_UNREACHABLE", backlog.ReasonCode);
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        CreateGateAuditRow audit = (await fixture.Context.CreateGateAudit
            .ToListAsync(TestContext.Current.CancellationToken)).Single();
        Assert.Equal(CreateGateVerdict.BlockedUnreachable, audit.Verdict);
        Assert.Equal(-1, audit.RiotRouteCostMm);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AmbiguousAreaStationMappingRemainsBackloggedAndNeverCreatesMovement()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.SetMapStations(
            new RiotMapStation(11, "N1-1"),
            new RiotMapStation(12, "N1-1_N1-2"),
            new RiotMapStation(210, "关卡"));
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("AREA_STATION_NOT_UNIQUE", backlog.ReasonCode);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Theory]
    [InlineData("D11-10", "OUT_OF_SCOPE_AREA")]
    [InlineData("Q18-10", "OUT_OF_SCOPE_AREA")]
    [InlineData("N22-1", "AREA_STATION_NOT_FOUND")]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task OutOfScopeOrMap25UnresolvableAreaNeverCreatesMissingPackageNoise(
        string area,
        string expectedReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            area,
            "EQP-01",
            "UNKNOWN-PACKAGE"));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(expectedReason, backlog.ReasonCode);
        Assert.Empty(await fixture.Context.MissingPackages.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    /// <summary>
    /// An AREA the area assignment table does not name is skipped silently (REQ-0191): the reason reaches the
    /// backlog and nothing else — no structural dispatch block, no log at Warning or above — round after round.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AnUnmappedAreaIsSkippedSilentlyWithNoStructuralBlockAndNoWarning()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        // A station exists for it and it starts with N: under the old prefix rule it would have been dispatched.
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "N1-1"),
            new RiotMapStation(13, "N1-7"),
            new RiotMapStation(210, "关卡"));
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            "N1-7"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow backlog = await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("OUT_OF_SCOPE_AREA", backlog.ReasonCode);
        Assert.Null(backlog.AcceptedAt);
        Assert.Empty(await fixture.Context.Set<StructuralDispatchBlockRow>().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.DoesNotContain(fixture.SlotCapacityLog.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// An AREA starting with T — a die-attach machine — is dispatched once the table names it. The old leading-N
    /// rule refused it however it was configured.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AMappedAreaStartingWithTIsAcceptedAndItsFreezeIsTheVersionItWasJudgedAgainst()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AreaAssignmentTableVersion table = await fixture.ImportAreaAssignmentsAsync(
        [
            new AreaAssignment("N1-1", fixture.Options.DispatchZone, "FRONT"),
            new AreaAssignment("T3-7", fixture.Options.DispatchZone, "REAR"),
        ]);
        fixture.Riot.SetMapStations(
            new RiotMapStation(12, "T3-7"),
            new RiotMapStation(210, "关卡"));
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            "T3-7"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("T3-7", runtime.PickupStationId);
        Assert.Equal(fixture.Options.DispatchZone, runtime.DispatchZone);
        DemandAreaAssignmentFreeze? freeze = await new DemandAreaAssignmentFreezeStore(fixture.Context)
            .ReadAsync(runtime.DemandId, TestContext.Current.CancellationToken);
        Assert.Equal(table.Version, freeze?.Version);
        Assert.Equal(table.SnapshotId, freeze?.SnapshotId);
    }

    /// <summary>
    /// A version imported after the round judged the demand and before intake commits refuses this acceptance —
    /// nothing is accepted and nothing is frozen — and the next round judges the demand again and freezes the
    /// new version.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AVersionImportedBetweenEvaluationAndAcceptanceIsRefusedAndTheNextRoundJudgesAgainstIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        AreaAssignmentTableVersion? imported = null;
        // Read 1 is the round's discovery; read 2 is intake's final read, after the round judged the candidate.
        fixture.Catalog.BeforeRead = read =>
        {
            if (read == 2)
            {
                imported = fixture.ImportAreaAssignmentsAsync(
                    [.. RuntimeFixture.DefaultAssignedAreas.Select(area =>
                        new AreaAssignment(area, fixture.Options.DispatchZone, "REAR"))])
                    .GetAwaiter().GetResult();
            }
        };

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, imported?.Version);
        Assert.Equal(
            "FINAL_CATALOG_DECISION_FACT_CHANGED",
            (await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<ConfigurationConsumerBindingRow>().ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);

        fixture.Catalog.BeforeRead = null;
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            2,
            (await new DemandAreaAssignmentFreezeStore(fixture.Context)
                .ReadAsync(runtime.DemandId, TestContext.Current.CancellationToken))?.Version);
    }

    /// <summary>
    /// A Map catalog revision — a station deleted and rebuilt under a new id, renamed, and the AREA moved onto
    /// another station — neither rewrites the area assignment table nor pauses dispatch for review (REQ-0350):
    /// the table is keyed on AREA, not on station.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AMapCatalogRevisionLeavesTheAreaAssignmentTableAloneAndDoesNotPauseDispatch()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AreaAssignmentTableVersion before = (await new AreaAssignmentStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .ReadCurrentAsync(TestContext.Current.CancellationToken))!;
        int auditsBefore = await fixture.Context.Set<BusinessAuditRecordRow>().CountAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        // Station 12 "N1-1" is gone; N1-1 now sits on a rebuilt, renamed station shared with N1-9. The station
        // admission policy is bound to station names, so the site republishes it under a new version, as it must.
        fixture.Options.AdmissionPolicyVersion++;
        fixture.Riot.SetMapStations(
            new RiotMapStation(13, "N1-2_N1-3"),
            new RiotMapStation(44, "N1-9_N1-1"),
            new RiotMapStation(210, "关卡"),
            new RiotMapStation(300, "等待点"));
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        fixture.Context.ChangeTracker.Clear();
        AreaAssignmentTableVersion after = (await new AreaAssignmentStore(fixture.Context, CreateGovernedPublisher(fixture.Context))
            .ReadCurrentAsync(TestContext.Current.CancellationToken))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ContentSha256, after.ContentSha256);
        Assert.Equal(before.ByArea.OrderBy(pair => pair.Key, StringComparer.Ordinal), after.ByArea.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(1, await fixture.Context.Set<DispatchZoneAreaAssignmentVersionRow>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(auditsBefore, await fixture.Context.Set<BusinessAuditRecordRow>().CountAsync(TestContext.Current.CancellationToken));
        JourneyRuntimeRow runtime = await fixture.Context.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(44, runtime.PickupStationRiotId);
        Assert.Null(runtime.BlockReasonCode);
        Assert.Equal(
            before.Version,
            (await new DemandAreaAssignmentFreezeStore(fixture.Context)
                .ReadAsync(runtime.DemandId, TestContext.Current.CancellationToken))?.Version);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task InScopeResolvedUnknownPackageIsDeduplicatedAndNeverCreatesOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            package: "UNKNOWN-PACKAGE"));

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        MissingPackageRow missing = await fixture.Context.MissingPackages.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN-PACKAGE", missing.Package);
        Assert.Equal("PENDING", missing.Status);
        Assert.Equal(Now, missing.FirstSeenAt);
        Assert.Equal(Now, missing.LastSeenAt);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task BatteryAtConfirmedThirtyPercentThresholdRemainsEligible()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.MinimumBatteryPercent = 30;
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 30 };
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Single(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task MultipleEqpsForOneAreaRemainBackloggedAndNeverCreateMovement()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(
            fixture.Demand(
                "10000000-0000-4000-8000-000000000001",
                "SUBLOT-001",
                Now.AddMinutes(-10),
                "N1-1",
                "EQP-01"),
            fixture.Demand(
                "10000000-0000-4000-8000-000000000002",
                "SUBLOT-002",
                Now.AddMinutes(-9),
                "N1-1",
                "EQP-02"));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.BoxCounts.Set("SUBLOT-002", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyBacklogRow[] backlog = await fixture.Context.JourneyBacklog
            .OrderBy(row => row.DemandId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, backlog.Length);
        Assert.All(backlog, row => Assert.Equal("AREA_EQP_NOT_UNIQUE", row.ReasonCode));
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Theory]
    [InlineData("route-missing", "AREA_STATION_NOT_FOUND")]
    [InlineData("zone-not-admitted", "DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING")]
    [InlineData("vehicle-not-idle", "RIOT_VEHICLE_NOT_IDLE")]
    [InlineData("vehicle-map-mismatch", "RIOT_VEHICLE_MAP_MISMATCH")]
    [InlineData("battery-low", "BATTERY_POLICY_NOT_SATISFIED")]
    [InlineData("riot-order-occupied", "RIOT_VEHICLE_ORDER_OCCUPIED")]
    [InlineData("box-count-missing", "SUBLOT_BOX_COUNT_UNAVAILABLE")]
    [InlineData("package-capacity-missing", "PACKAGE_CAPACITY_NOT_UNIQUE")]
    [InlineData("onboard-silent", "ONBOARD_FACTS_NOT_READY")]
    [InlineData("onboard-unsafe", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("slot-capacity", "SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE")]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task EveryAdmissionGateFailsClosedBeforeAcceptance(
        string scenario,
        string expectedReason)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001",
            Now.AddMinutes(-10),
            package: scenario == "package-capacity-missing" ? "UNKNOWN-PACKAGE" : "PDFN5×6-8L(12R)");
        fixture.Catalog.Set(demand);
        fixture.BoxCounts.Set("SUBLOT-001", 8);
        switch (scenario)
        {
            case "route-missing":
                fixture.Riot.SetMapStations(
                    new RiotMapStation(210, "关卡"),
                    new RiotMapStation(300, "等待点"));
                break;
            case "zone-not-admitted":
                fixture.Options.AllowedDispatchZones = [];
                break;
            case "vehicle-not-idle":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { ProcState = "EXECUTING" };
                break;
            case "vehicle-map-mismatch":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentMap = "OTHER-MAP" };
                break;
            case "battery-low":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
                break;
            case "riot-order-occupied":
                fixture.Riot.Vehicle = fixture.Riot.Vehicle with { LockStatus = 1, OrderTaskId = "ORDER-ACTIVE" };
                break;
            case "box-count-missing":
                fixture.BoxCounts.Remove("SUBLOT-001");
                break;
            case "package-capacity-missing":
                break;
            case "onboard-silent":
                await fixture.SilenceOnboardSessionAsync();
                break;
            case "onboard-unsafe":
                await fixture.SetOnboardUnknownAsync();
                break;
            case "slot-capacity":
                await fixture.KeepOnlyOneAvailableSlotAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        Assert.Equal(expectedReason, (await fixture.Context.JourneyBacklog.SingleAsync(
            TestContext.Current.CancellationToken)).ReasonCode);
    }
}
