using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 自动重建停住之后的人工出口（control-server#345）：护栏三停住（<c>OWN_ORDER_REBUILD_STOPPED</c>）时人工重建一次，
/// 走 #299 的同一个入口、同一套署名与审计。
/// </summary>
/// <remarks>
/// <para>
/// <b>出口形态是调度 Coordinator 8 于 2026-09-23 确认的</b>（本票第一步的提案与答复）：甲（人工重建）做；丙（释放改派）不做；
/// 乙（放弃这趟、终止需求）等用户答复，本类不含它。人工重建之后 REQ-0361 的窗口从人工请求那一刻重新算——人工重建之后再出问题，
/// 照样停住等人。
/// </para>
/// <para>
/// 人工重建不绕过 #318 的任何一道建单前检查：车况、建单门禁、车载端会话就绪与离站判定、有货来源的新快照证据，全都照走引擎那条路。
/// 它只替人做一件事：把「停住」换成「现在就重建一次」。
/// </para>
/// <para>码写成字面量：它们是现场拿到的东西，改名应当让这里红。</para>
/// </remarks>
public sealed class StoppedRebuildExitTests
{
    private const string OperatorId = "L1-OPERATOR-345";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 甲：人工重建 ----------------------------------------------------------------------------------------------

    /// <summary>
    /// 护栏三停住的旅程（重建出来的单在窗口内又被取消）：人工重建——停住的那条记录转 <c>ENDED</c>、停住原因原样留着，另落一条
    /// 由这个人发起的新记录；请求本身不向 RIoT 发任何东西，也不释放、不改派；引擎下一轮给同一辆车、同一条需求建第三张单，
    /// 停靠指向它，旅程码清空。每次调用记告警（事件 9203，写明动作与人）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0361")]
    public async Task AStoppedRebuildIsRebuiltOnceOnAPersonsRequest()
    {
        await using RuntimeFixture fixture = await StoppedByTheThirdGuardAsync();
        JourneyRuntimeRow stopped = await fixture.RuntimeAsync();
        JourneyStopRow stop = await CurrentStopAsync(fixture, FirstDemandId);
        VehicleFaultRecoveryTests.SiteRiot site = new(fixture);
        RecordingLogger<VehicleFaultRecoveryService> log = new();

        VehicleFaultRecoveryDecision decision = await VehicleFaultRecoveryTests.Service(fixture, site, logger: log)
            .RecoverAsync(Rebuild(fixture), Token);
        DateTimeOffset requestedAt = fixture.Clock.GetUtcNow();

        Assert.Equal(
            (VehicleFaultRecoveryOutcome.RebuildRequested, VehicleFaultRecoveryDispositions.RebuildScheduled),
            (decision.Outcome, decision.Disposition));
        Assert.Empty(decision.Reasons);
        Assert.Empty(site.OrderCommands);
        Assert.Empty(site.EmergencyCommands);
        await using (ControlServerDbContext reading = new(fixture.DbOptionsForTests))
        {
            OwnOrderRebuildRow[] rows = await reading.OwnOrderRebuilds.AsNoTracking()
                .Where(row => row.EndedUpperId == stop.UpperId).ToArrayAsync(Token);
            Assert.Equal(2, rows.Length);
            OwnOrderRebuildRow ended = Assert.Single(rows, row => row.RebuildId == OwnOrderRebuilds.RebuildIdFor(stop.UpperId));
            Assert.Equal(
                (OwnOrderRebuildStates.Ended, "REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW"),
                (ended.State, ended.StoppedReason));
            OwnOrderRebuildRow manual = Assert.Single(rows, row => row.RebuildId != ended.RebuildId);
            Assert.Equal(
                (OwnOrderRebuildSources.CancelledInRiot, OwnOrderRebuildStates.Pending, OperatorId, stop.StopId),
                (manual.Source, manual.State, manual.OperatorId, manual.StopId));
            Assert.Equal((requestedAt, requestedAt, requestedAt), (manual.IncidentAt, manual.RecordedAt, manual.DueAt));
            JourneyRuntimeRow kept = await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
            Assert.Equal((stopped.JourneyId, JourneyRuntimeStage.AwaitingPickupArrival), (kept.JourneyId, kept.Stage));
            Assert.Null((await reading.Set<JourneyDemandRow>().AsNoTracking().SingleAsync(Token)).RemovedAt);
        }

        Assert.Single(log.Entries, entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.Contains("RebuildStoppedOrder", StringComparison.Ordinal) &&
            entry.Message.Contains(OperatorId, StringComparison.Ordinal) &&
            entry.Message.Contains("RebuildRequested", StringComparison.Ordinal));

        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(3, fixture.Riot.CreateCount("TO_PICKUP"));
        await using ControlServerDbContext after = new(fixture.DbOptionsForTests);
        OwnOrderRebuildRow rebuilt = await after.OwnOrderRebuilds.AsNoTracking()
            .SingleAsync(row => row.EndedUpperId == stop.UpperId && row.OperatorId == OperatorId, Token);
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, rebuilt.State);
        JourneyStopRow repointed = await after.Set<JourneyStopRow>().AsNoTracking().SingleAsync(row => row.StopId == stop.StopId, Token);
        Assert.Equal(rebuilt.NewUpperId, repointed.UpperId);
        OrderIntentRow intent = await after.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == rebuilt.NewUpperId, Token);
        Assert.Equal(
            (stopped.DemandId, stopped.VehicleKey, stop.StationRiotId, "CONFIRMED"),
            (intent.DemandId, intent.VehicleKey, intent.DestinationStationId, intent.Status));
        JourneyRuntimeRow moving = await after.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.Equal(
            (stopped.JourneyId, JourneyRuntimeStage.AwaitingPickupArrival, (string?)null),
            (moving.JourneyId, moving.Stage, moving.BlockReasonCode));
    }

    // ---- 夹具 ----------------------------------------------------------------------------------------------

    internal static VehicleFaultRecoveryRequest Rebuild(RuntimeFixture fixture) => new(
        new EmergencyStopSubject(fixture.Options.AgvId, fixture.Options.VehicleKey),
        VehicleFaultRecoveryAction.RebuildStoppedOrder,
        OperatorId,
        FaultRemedied: true,
        Note: "L1");

    /// <summary>
    /// 护栏三在取消来源上停住：开往取货站的单被取消、延迟后重建；重建出来的单在窗口内又被取消——不再重建，
    /// 旅程码 <c>OWN_ORDER_REBUILD_STOPPED</c>，前后建过两张取货单。
    /// </summary>
    internal static async Task<RuntimeFixture> StoppedByTheThirdGuardAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        fixture.Riot.CancelOrder((await fixture.RuntimeAsync()).PickupUpperId);
        await TickAndRunAsync(fixture);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);
        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.HearFromPeerAsync();
        fixture.Riot.CancelOrder((await CurrentStopAsync(fixture, FirstDemandId)).UpperId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("OWN_ORDER_REBUILD_STOPPED", (await fixture.RuntimeAsync()).BlockReasonCode);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }
}
