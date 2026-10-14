using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 站点超时不再静默丢单（control-server#703，用户 2026-10-14 定「能放回待派队列的放回，并防止死循环；不适合放回的至少要告警」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>规则</b>：取货停靠离站期限到期、一条也没录入，本站的待做项不终结，按释放改派退回积压（归属移除原因
/// <c>RELEASED_AT_STATION_DEADLINE</c>，需求仍是 <c>Accepted</c>、不写业务键抑制），下一轮派车重新受理。同一条需求这样放回满
/// <see cref="JourneyRuntimeOptions.StationTimeoutRedispatchLimit"/> 次之后，再一次站点超时就以 <c>CANCELLED_BY_STATION_TIMEOUT</c>
/// 终结并抑制，同时在看板的结构性派车阻断里挂一行人工搬运告警，直到需求离开 MES 目录。
/// </para>
/// <para>
/// 确定的装货失败（<c>OPERATOR_TIMEOUT</c>）不放回：部分仓位可能已经装上，货的去向要人判断，所以终结照旧、加告警。
/// 操作员主动取消的三个码不动，仍然抑制。
/// </para>
/// <para>
/// 在这之前两种情形都以 <c>CANCELLED_BY_STATION_TIMEOUT</c> 终结并按业务键永久抑制；v2 对 MesIngest 只读，那条需求就一直挂在 MES 目录里、
/// 被排除在饥饿告警之外，货留在机台上没人知道要去搬。
/// </para>
/// </remarks>
public sealed class StationTimeoutRedispatchTests
{
    private const string Demand = "10000000-0000-4000-8000-000000000001";
    private const string Sublot = "SUBLOT-001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 到站没人扫码，期限一到：旅程收尾、车放开，需求回到积压（不终结、不抑制、不告警）；下一轮把它作为新的一代重新派出去，车再开到这一站等录入。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AnUnscannedPickupGoesBackToTheBacklogAtTheStationDeadlineAndIsDispatchedAgain()
    {
        await using RuntimeFixture fixture = await SingleDemandAsync();
        JourneyRuntimeRow first = await fixture.AdvanceToSublotWaitAsync();

        DateTimeOffset deadline = await RunOutTheStationDeadlineAsync(fixture);

        JourneyRuntimeRow closed = await JourneyAsync(fixture, first.JourneyId);
        Assert.Equal(JourneyRuntimeStage.Completed, closed.Stage);
        Assert.Equal(DemandJourneyLookup.ReleasedAtStationDeadlineReason, closed.BlockReasonCode);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        JourneyDemandRow released = Assert.Single(await MembershipsAsync(fixture));
        Assert.Equal(deadline, released.RemovedAt);
        Assert.Equal(DemandJourneyLookup.ReleasedAtStationDeadlineReason, released.RemovalReason);
        Assert.True(
            await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == Demand, Token),
            "The demand released at the station deadline is not waiting to be dispatched again.");
        Assert.False(await fixture.Context.Set<VehiclePurposeClaimRow>().AsNoTracking().AnyAsync(Token));
        ProtocolOutboxRow entryRequest = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .SingleAsync(row => row.MessageId == first.SublotRequestMessageId, Token);
        Assert.Equal(deadline, entryRequest.AcknowledgedAt);
        Assert.Empty(await AlarmsAsync(fixture));

        JourneyRuntimeRow again = await RedispatchAndWaitForEntryAsync(fixture);

        Assert.NotEqual(first.JourneyId, again.JourneyId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, again.Stage);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        JourneyDemandRow[] memberships = await MembershipsAsync(fixture);
        Assert.Equal([1L, 2L], memberships.Select(row => row.DispatchGeneration).ToArray());
        Assert.Null(memberships[1].RemovedAt);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// 上限 1：第一次放回，第二次就不放了——需求以 <c>CANCELLED_BY_STATION_TIMEOUT</c> 终结并抑制，看板挂一行人工搬运告警。之后 MES 目录里它
    /// 一直在，派车轮一轮轮跑，不再派车、告警也不被清；直到它离开目录，告警才清。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task AtTheLimitTheStationDeadlineEndsTheDemandAndRaisesAManualCarryAlarmThatStaysUntilMesClosesIt()
    {
        await using RuntimeFixture fixture = await SingleDemandAsync();
        fixture.Options.StationTimeoutRedispatchLimit = 1;
        await fixture.AdvanceToSublotWaitAsync();
        await RunOutTheStationDeadlineAsync(fixture);
        Assert.Equal(DemandExecutionStatus.Accepted, (await fixture.DemandRowAsync()).Status);
        JourneyRuntimeRow second = await RedispatchAndWaitForEntryAsync(fixture);

        DateTimeOffset deadline = await RunOutTheStationDeadlineAsync(fixture);

        await AssertEndedWithManualCarryAlarmAsync(
            fixture, second.JourneyId, ManualCarryAlarms.StationTimeoutRedispatchLimitReached, deadline);
        for (int round = 0; round < 3; round++)
        {
            await fixture.HearFromPeerAsync();
            await TickAndRunAsync(fixture);
        }
        Assert.Equal(2, await fixture.Context.JourneyRuntimes.CountAsync(Token));
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Null(Assert.Single(await AlarmsAsync(fixture)).ClearedAt);

        fixture.Catalog.Set();
        await TickAndRunAsync(fixture);
        Assert.NotNull(Assert.Single(await AlarmsAsync(fixture)).ClearedAt);
    }

    /// <summary>
    /// 计数在库里，不在进程里：放回一次之后服务重启，上限 1 照样在第二次站点超时时生效。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task TheCountOfReleasesSurvivesARestart()
    {
        await using RuntimeFixture fixture = await SingleDemandAsync();
        fixture.Options.StationTimeoutRedispatchLimit = 1;
        await fixture.AdvanceToSublotWaitAsync();
        await RunOutTheStationDeadlineAsync(fixture);

        await fixture.RecreateEngineAsync();
        JourneyRuntimeRow second = await RedispatchAndWaitForEntryAsync(fixture);
        DateTimeOffset deadline = await RunOutTheStationDeadlineAsync(fixture);

        await AssertEndedWithManualCarryAlarmAsync(
            fixture, second.JourneyId, ManualCarryAlarms.StationTimeoutRedispatchLimitReached, deadline);
    }

    /// <summary>上限 0：第一次站点超时就终结并告警——今天的行为加上告警。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ALimitOfZeroEndsTheDemandAtTheFirstDeadlineAndStillRaisesTheAlarm()
    {
        await using RuntimeFixture fixture = await SingleDemandAsync();
        fixture.Options.StationTimeoutRedispatchLimit = 0;
        JourneyRuntimeRow first = await fixture.AdvanceToSublotWaitAsync();

        DateTimeOffset deadline = await RunOutTheStationDeadlineAsync(fixture);

        await AssertEndedWithManualCarryAlarmAsync(
            fixture, first.JourneyId, ManualCarryAlarms.StationTimeoutRedispatchLimitReached, deadline);
    }

    /// <summary>
    /// 确定的装货失败不放回：需求照旧以 <c>CANCELLED_BY_STATION_TIMEOUT</c> 终结并抑制，另挂一行人工告警——部分仓位可能已经装上，
    /// 那批货在哪要人看。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ADeterminateLoadFailureIsNotReleasedButEndsWithAManualAlarm()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await JourneyRuntimeWorkerLoadDeadlineTests.AdvanceToLoadWithStationDeadlineAsync(fixture);
        string journeyId = (await fixture.RuntimeAsync()).JourneyId;
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        await JourneyRuntimeWorkerLoadDeadlineTests.ReportLoadResultAsync(
            fixture, JourneyRuntimeWorkerLoadDeadlineTests.DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT"));
        DateTimeOffset settledAt = fixture.Clock.GetUtcNow();

        await fixture.Engine.ExecuteOnceAsync(Token);

        string demandId = (await fixture.DemandRowAsync()).DemandId;
        await AssertEndedWithManualCarryAlarmAsync(
            fixture, journeyId, ManualCarryAlarms.DeterminateLoadFailureNeedsManual, settledAt, demandId);
    }

    /// <summary>
    /// 第二个取货站上没人扫码，期限一到：那一条退回积压，第一站装上的那条照常送，车转入离站等待，不终结、不抑制。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AtASecondPickupOnlyTheUnscannedDemandGoesBackAndTheJourneyCarriesOnWithItsCargo()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await StopEndedJourneyContinuesTests.ArriveAtTheSecondPickupAsync(fixture);
        string journeyId = (await JourneyOfAsync(fixture, FirstDemandId)).JourneyId;
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);

        await RunOutTheStationDeadlineAsync(fixture);

        JourneyRuntimeRow runtime = await JourneyAsync(fixture, journeyId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == SecondDemandId, Token);
        Assert.Equal(DemandJourneyLookup.ReleasedAtStationDeadlineReason, second.RemovalReason);
        Assert.NotNull(second.RemovedAt);
        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == SecondDemandId, Token)).Status);
        Assert.Equal(
            JourneyDemandStatuses.Loaded,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == FirstDemandId, Token)).Status);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.Empty(await AlarmsAsync(fixture));
    }

    /// <summary>
    /// 真车载端的形状：放回之后重新派出的那一趟，车载端因为挂着本服务端的在途单整段未就绪。未就绪期间期限不跑、什么也不放回；车到站、会话回到
    /// 就绪，新的一趟从这次到站起算自己的期限，不继承上一趟早已过去的那一个。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ARedispatchedJourneyWhileTheSessionIsNotReadyOnItsOwnOrderRunsItsOwnDeadlineFromItsArrival()
    {
        await using RuntimeFixture fixture = await SingleDemandAsync();
        await fixture.AdvanceToSublotWaitAsync();
        await RunOutTheStationDeadlineAsync(fixture);
        JourneyRuntimeRow redispatched = await WaitForRedispatchAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, redispatched.Stage);

        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        for (int round = 0; round < 3; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await JourneyAsync(fixture, redispatched.JourneyId)).Stage);
        Assert.Null((await MembershipsAsync(fixture))[1].RemovedAt);

        await fixture.RestoreSessionReadyAsync();
        JourneyRuntimeRow arrived = await ArriveAsync(fixture, redispatched.JourneyId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await JourneyAsync(fixture, redispatched.JourneyId)).Stage);
        Assert.Null((await MembershipsAsync(fixture))[1].RemovedAt);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(DemandJourneyLookup.ReleasedAtStationDeadlineReason, (await MembershipsAsync(fixture))[1].RemovalReason);
    }

    /// <summary>
    /// 操作员主动取消的三个码不受本票影响：照旧终结、按业务键抑制，需求不进「已释放待改派」。
    /// </summary>
    [Theory]
    [InlineData("CANCELLED_BY_OPERATOR")]
    [InlineData("CANCELLED_BY_LOAD_COMPENSATION")]
    [InlineData("TERMINATED_BY_FAULT_CARGO_HANDOFF")]
    public async Task AnOperatorsOwnEndingStillSuppressesTheKeyAndIsNotReleased(string reasonCode)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        DateTimeOffset at = Batch7JourneyFixture.Now;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-703", "agv-01", "VK-01", at);
        await fixture.RenewContextAsync();
        await using (var transaction = await fixture.Context.Database.BeginTransactionAsync(Token))
        {
            JourneyRuntimeRow runtime = await DemandJourneyLookup.JourneyOf(fixture.Context, "D-703").SingleAsync(Token);
            await new PickupStopTermination(fixture.Context).StageAsync(runtime, "D-703", reasonCode, at.AddMinutes(5), Token);
            await fixture.Context.SaveChangesAsync(Token);
            await transaction.CommitAsync(Token);
        }

        await using ControlServerDbContext reading = fixture.NewContext();
        Assert.Equal(DemandExecutionStatus.Cancelled, (await reading.AcceptedDemands.SingleAsync(Token)).Status);
        Assert.Equal(reasonCode, Assert.Single(await SuppressionAssertions.AllAsync(reading)).ReasonCode);
        Assert.False(await DemandJourneyLookup.ReleasedForRedispatch(reading).AnyAsync(Token));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static async Task<RuntimeFixture> SingleDemandAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(Demand, Sublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(Sublot, 7);
        return fixture;
    }

    /// <summary>门都关着、车在线，把钟拨到期限上跑一轮。返回期限那一刻。</summary>
    private static async Task<DateTimeOffset> RunOutTheStationDeadlineAsync(RuntimeFixture fixture)
    {
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.HearFromPeerAsync();
        DateTimeOffset deadline = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(Token);
        return deadline;
    }

    /// <summary>跑派车轮直到这条需求又有了生效的归属，返回它所在的新旅程。</summary>
    private static async Task<JourneyRuntimeRow> WaitForRedispatchAsync(RuntimeFixture fixture)
    {
        for (int round = 0; round < 5; round++)
        {
            if (await DemandJourneyLookup.JourneyOf(fixture.Context, Demand).AsNoTracking().SingleOrDefaultAsync(Token)
                is { } journey)
            {
                return journey;
            }
            await fixture.HearFromPeerAsync();
            await TickAndRunAsync(fixture);
        }
        Assert.Fail("The demand released at the station deadline was never dispatched again.");
        return null!;
    }

    private static async Task<JourneyRuntimeRow> RedispatchAndWaitForEntryAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow journey = await WaitForRedispatchAsync(fixture);
        return await ArriveAsync(fixture, journey.JourneyId);
    }

    /// <summary>车开到这趟旅程当前停靠所在的站，用这个停靠自己的单号。</summary>
    private static async Task<JourneyRuntimeRow> ArriveAsync(RuntimeFixture fixture, string journeyId)
    {
        JourneyStopRow stop = (await JourneyStopCursor.LoadAsync(
            fixture.Context, await JourneyAsync(fixture, journeyId), Token)).Current;
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", stop.UpperId, stop.StationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = stop.StationRiotId };
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
        return await JourneyAsync(fixture, journeyId);
    }

    private static async Task AssertEndedWithManualCarryAlarmAsync(
        RuntimeFixture fixture, string journeyId, string alarmCode, DateTimeOffset at, string demandId = Demand)
    {
        JourneyRuntimeRow ended = await JourneyAsync(fixture, journeyId);
        Assert.Equal(JourneyRuntimeStage.Completed, ended.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", ended.BlockReasonCode);
        AcceptedDemandRow demand = await fixture.Context.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, Token);
        Assert.Equal(DemandExecutionStatus.Cancelled, demand.Status);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", Assert.Single(await SuppressionAssertions.AllAsync(fixture.Context)).ReasonCode);
        Assert.False(await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == demandId, Token));
        StructuralDispatchBlockRow alarm = Assert.Single(await AlarmsAsync(fixture));
        Assert.Equal((demandId, alarmCode, demand.TransportDemandKey), (alarm.DemandId, alarm.ReasonCode, alarm.TransportDemandKey));
        Assert.Equal(at, alarm.FirstRaisedAt);
        Assert.Null(alarm.ClearedAt);
        Assert.Contains(fixture.EngineLog.Entries, entry => entry.Message.Contains(alarmCode, StringComparison.Ordinal));
    }

    private static Task<JourneyRuntimeRow> JourneyAsync(RuntimeFixture fixture, string journeyId) =>
        fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journeyId, Token);

    private static async Task<JourneyDemandRow[]> MembershipsAsync(RuntimeFixture fixture) =>
        [.. (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .Where(row => row.DemandId == Demand)
                .ToArrayAsync(Token))
            .OrderBy(row => row.DispatchGeneration)];

    private static Task<StructuralDispatchBlockRow[]> AlarmsAsync(RuntimeFixture fixture) =>
        fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking().ToArrayAsync(Token);
}
