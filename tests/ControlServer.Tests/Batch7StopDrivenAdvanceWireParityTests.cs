using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-03（control-server#208）的主证据：推进改成由当前停靠驱动之后，单需求旅程两个停靠发出去的每一条报文与每一张
/// RIoT 订单，与改动前逐字相同。
/// </summary>
/// <remarks>
/// <para>
/// 本票把清单、录入请求、计划、装卸命令改从停靠表与从属需求表生成，修订号改取按车计数器。这些都是车载端看得见的东西：
/// 消息 id 变了，重连后的补发就认不回同一条；修订号变了，车载端按消息类型记住的已采纳修订号会回退，直接
/// <c>SNAPSHOT_REVISION_REGRESSION</c> 断会话。所以「行为不变」在这张票上不是一句结论，是一份逐字对照。
/// </para>
/// <para>
/// 对照的粒度见 <see cref="WirePin"/>；驱动见 <see cref="Batch7StopDrivenAdvanceDriver"/>。
/// </para>
/// </remarks>
public sealed class Batch7StopDrivenAdvanceWireParityTests
{
    [Fact]
    public async Task ANormalJourneySendsTheSameElevenLinesAndTheSameTwoOrders()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);

        JourneyRuntimeRow runtime = await RunJourneyToCompletionAsync(
            fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);

        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "normal-journey", fixture.Peer.Lines);
    }

    [Fact]
    public async Task TwoJourneysOnOneVehicleKeepTheirRevisionStreamsMonotonic()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);
        await RunJourneyToCompletionAsync(
            fixture, FirstDemandId, FirstSublot, FirstSubmissionId, FirstSafetyResultId);

        fixture.Catalog.Set(fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9)));
        fixture.BoxCounts.Set(SecondSublot, 4);
        JourneyRuntimeRow second = await RunJourneyToCompletionAsync(
            fixture, SecondDemandId, SecondSublot, SecondSubmissionId, SecondSafetyResultId);

        Assert.Equal(JourneyRuntimeStage.Completed, second.Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "two-journeys-one-vehicle", fixture.Peer.Lines);
    }

    /// <summary>
    /// 到了取货站却没人扫码，站点期限走完，服务端自己结束这个停靠。推进段独力走完的一条终结路径——本票把这段挪到
    /// 「当前停靠」上，所以它发过什么、没发过什么都要一字不差：特别是一条装卸命令都不该有。
    /// </summary>
    [Fact]
    public async Task AStopEndedByItsStationDeadlineSendsTheSameLines()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await fixture.ProveSlotDoorsClosedAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await TickAndRunAsync(fixture);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);
        await WirePin.AssertMatchesAsync(fixture.Context, "station-deadline", fixture.Peer.Lines);
    }

    /// <summary>
    /// 车已经装好、等着离站核验的答复，这时车上的安全状态版本变了，原来那次核验就过期了：服务端作废它，换一个新身份
    /// 再问一次。
    /// </summary>
    /// <remarks>
    /// 新身份是 <c>StableGuid(旧 id, "reissued-after-expiry")</c> 派生的，而本票动的正是「id 从哪里来」这件事——
    /// 离站核验的消息 id 与核验 id 现在挂在取货停靠行上（<c>JourneyStopRow.DepartureSafetyCheckMessageId</c> 与
    /// <c>DepartureSafetyCheckId</c>），重发时两处都要跟着换。这条路径专门盯它。
    /// </remarks>
    [Fact]
    public async Task AnExpiredDepartureCheckIsRetiredAndAskedAgainUnderTheSameDerivedIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        JourneyRuntimeRow runtime = await SettleLoadAsync(fixture, FirstDemandId);
        string expiredCheckId = runtime.PreDepartureSafetyCheckId;
        string expiredMessageId = runtime.PreDepartureSafetyCheckMessageId;
        await AddInboxAsync(
            fixture, FirstSafetyResultId, "PreDepartureSafetyCheckResult",
            SafeDepartureAnswer(fixture, expiredCheckId, safetyStateVersion: 7), expiredCheckId);
        await fixture.AddSafetyStateChangedAsync(8, departureSafe: true, vehicleStopped: true);
        await TickAndRunAsync(fixture);

        runtime = await fixture.RuntimeAsync(FirstDemandId);
        Assert.Equal("PREDEPARTURE_CHECK_EXPIRED", runtime.BlockReasonCode);
        Assert.NotEqual(expiredCheckId, runtime.PreDepartureSafetyCheckId);
        Assert.NotEqual(expiredMessageId, runtime.PreDepartureSafetyCheckMessageId);
        await WirePin.AssertMatchesAsync(fixture.Context, "departure-check-expired", fixture.Peer.Lines);
    }

    /// <summary>
    /// 车载端断线、换一个会话代次重连，服务端把还没被确认的报文按原 id 重发一遍。
    /// </summary>
    /// <remarks>
    /// 这是本票风险最集中的一条：重放集合原本是写死的 11 个 id（<c>RuntimeMessageIds</c>），本票改成按停靠枚举。
    /// 少枚举一个，那条报文就永远不再补发，车载端等一个再也不来的东西；多枚举一个或者换了 id，车载端会当成没见过的
    /// 新消息。所以这里既钉发件箱里每行的会话代次，也钉真正写到线上的那一串——后者是唯一能看见「补发了哪几条、按什么
    /// 顺序」的地方。
    /// </remarks>
    [Fact]
    public async Task AReconnectReplaysExactlyTheUnacknowledgedLinesOfThisJourney()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 4);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await fixture.ReconnectAsync(2);
        await fixture.AdvanceSessionAsync(2);
        await TickAndRunAsync(fixture);

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync(FirstDemandId)).Stage);
        await WirePin.AssertMatchesAsync(fixture.Context, "reconnect-replay", fixture.Peer.Lines);
    }
}
