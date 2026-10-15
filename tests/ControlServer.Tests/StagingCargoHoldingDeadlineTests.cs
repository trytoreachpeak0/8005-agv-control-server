using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Release;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.StagingPickupNoCountdownTests;

namespace ControlServer.Tests;

/// <summary>
/// 待送点装上至少一篮后有持货期限，以本站为取货站的新需求追加进来时期限重新起算，到期带已装的货走、没装的放回待派（control-server#727，
/// CP-0014 修订项一，协议 v4 候选 wire-notes「Staging pickup stops」第 3～5 条，向量 <c>CV-STAGING-CARGO-HOLDING-DEADLINE-MOVED</c>、
/// <c>CV-STAGING-CARGO-HOLDING-TIMEOUT</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 夹具是 control-server#709 那一份（<see cref="StagingPickupNoCountdownTests.ArrangeAsync"/>）。离站期限五分钟；持货期限有的用例改成两分钟，
/// 好让「持货到期」与「站点离站期限」在钟上分得开：机台取货到期之后还要等站点期限走完才走，待送点没有站点期限，到期即走。
/// </para>
/// <para>
/// 零篮不计时由 <see cref="StagingPickupNoCountdownTests.AnHourWithNobodyScanningEndsOnlyAMachinePickup"/> 守（一小时没人扫码，待送点不结束）；
/// 起算点怎样挪的纯规则（撤销到零篮、追加先后）由 <see cref="StagingCargoHoldingStartTests"/> 守。
/// </para>
/// </remarks>
public sealed class StagingCargoHoldingDeadlineTests
{
    private static readonly TimeSpan ShortHolding = TimeSpan.FromMinutes(2);

    private const string ThirdDemand = "10000000-0000-4000-8000-000000000727";

    private const string ThirdSublot = "SUBLOT-727";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 装上第一篮：三种停靠都给出持货期限（起算点加两分钟），车上与看板一致。到期：三种都以 <c>CARGO_HOLDING_TIMEOUT</c> 关闭装货阶段；
    /// 只有待送点同时就走（发离站核验），机台与规则关还要等站点离站期限（五分钟）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task TheFirstBasketStartsTheDeadlineAndOnlyTheStagingStopLeavesTheMomentItExpires(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        fixture.Options.CargoHoldingTimeout = ShortHolding;
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        JourneyRuntimeRow loaded = await fixture.RuntimeAsync();
        DateTimeOffset deadline = loaded.CargoHoldingStartedAt!.Value + ShortHolding;

        Batch7CargoHoldingTests.LoadingPhaseSnapshot holding = (await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture))[^1];
        Assert.Equal((LoadingPhaseStates.CargoHoldingWait, (DateTimeOffset?)deadline), (holding.State, holding.CargoHoldingDeadlineAt));
        JsonElement dashboard = Assert.Single(await CargoHoldingRowsAsync(fixture));
        Assert.Equal(deadline, dashboard.GetProperty("cargoHoldingDeadlineAt").GetDateTimeOffset());

        await fixture.ProveSlotDoorsClosedAsync();
        await TickUntilAsync(fixture, deadline - TimeSpan.FromSeconds(5));
        Assert.NotEqual(LoadingPhaseStates.Closed, (await fixture.RuntimeAsync()).LoadingPhaseState);
        Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), $"{kind}: left before the holding deadline.");

        await TickUntilAsync(fixture, deadline + TimeSpan.FromSeconds(5));
        JourneyRuntimeRow expired = await fixture.RuntimeAsync();
        Assert.Equal(
            (LoadingPhaseStates.Closed, LoadingClosedReasons.CargoHoldingTimeout),
            (expired.LoadingPhaseState, expired.LoadingClosedReason));
        Assert.Equal(
            kind == StopKind.StagingRulesOn,
            await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture));
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, Demand)).Status);
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
    }

    /// <summary>
    /// 本区不许途中追加（不适用持货等单）：机台与规则关照旧不持货、不给期限，按站点离站期限走；待送点照样持货等单、有期限、到期就走
    /// （REQ-0354「车辆在待送点取货停靠持货等单，不论其能够服务的 DispatchZone 是否允许途中追加」）。
    /// </summary>
    [Theory]
    [MemberData(nameof(Kinds), MemberType = typeof(StagingPickupNoCountdownTests))]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task WithAppendsForbiddenOnlyTheStagingStopStillHasTheDeadline(StopKind kind)
    {
        await using RuntimeFixture fixture = await ArrangeAsync(kind);
        fixture.Options.CargoHoldingTimeout = ShortHolding;
        await LoadTheBasketAsync(fixture);
        JourneyRuntimeRow loaded = await fixture.RuntimeAsync();
        DateTimeOffset deadline = loaded.CargoHoldingStartedAt!.Value + ShortHolding;

        await fixture.ProveSlotDoorsClosedAsync();
        await TickUntilAsync(fixture, deadline + TimeSpan.FromSeconds(5));

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Batch7CargoHoldingTests.LoadingPhaseSnapshot[] snapshots = await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture);
        if (kind == StopKind.StagingRulesOn)
        {
            Assert.Contains(snapshots, snapshot =>
                snapshot.State == LoadingPhaseStates.CargoHoldingWait && snapshot.CargoHoldingDeadlineAt == deadline);
            Assert.Equal(
                (LoadingPhaseStates.Closed, LoadingClosedReasons.CargoHoldingTimeout),
                (journey.LoadingPhaseState, journey.LoadingClosedReason));
            Assert.True(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), "The staging deadline sent nobody on.");
        }
        else
        {
            Assert.All(snapshots, snapshot => Assert.Null(snapshot.CargoHoldingDeadlineAt));
            Assert.NotEqual(LoadingClosedReasons.CargoHoldingTimeout, journey.LoadingClosedReason);
            Assert.False(await Batch7CargoHoldingTests.DepartureCheckSentAsync(fixture), $"{kind}: left before its station departure wait.");
        }
    }

    /// <summary>
    /// 向量 <c>CV-STAGING-CARGO-HOLDING-DEADLINE-MOVED</c> 与 <c>CV-STAGING-CARGO-HOLDING-TIMEOUT</c>：装了一篮之后三分钟，派车给这辆车追加了一条
    /// 以本站为取货站的需求：期限挪到追加时刻加五分钟，落库并发给车。原来的期限过了不走；新的期限到了，没扫的那条放回待派（不取消、不抑制、
    /// 不计站点超时次数），本站以 <c>DEMAND_RELEASED</c> 的空清单结束，装货阶段以 <c>CARGO_HOLDING_TIMEOUT</c> 关闭，带着第一篮走。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AnAppendForTheStationRestartsTheDeadlineAndItsExpiryReleasesWhatWasNotScanned()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);
        DateTimeOffset firstStart = (await fixture.RuntimeAsync()).CargoHoldingStartedAt!.Value;

        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        DateTimeOffset appendedAt = fixture.Clock.GetUtcNow();
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        await MoveToTheSecondStopAsync(fixture);
        Assert.Equal(appendedAt, (await fixture.RuntimeAsync()).CargoHoldingStartedAt);

        // 车已在第 2 停靠时再来一条：当前停靠不并，新开第 3 停靠（REQ-0196）。它同样以本站为取货站，期限再挪到它的追加时刻；到期时它也放回、
        // 第 3 停靠删掉，计划因此变了——向量线序里那张计划就是这样来的。
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        appendedAt = fixture.Clock.GetUtcNow();
        await AppendStagingDemandAsync(fixture, ThirdDemand, ThirdSublot);
        await TickAsync(fixture);
        Assert.NotEqual(
            (await MembershipAsync(fixture, SecondDemand)).PickupStopId,
            (await MembershipAsync(fixture, ThirdDemand)).PickupStopId);

        DateTimeOffset moved = appendedAt + fixture.Options.CargoHoldingTimeout;
        Assert.Equal(appendedAt, (await fixture.RuntimeAsync()).CargoHoldingStartedAt);
        Assert.Contains(
            await Batch7CargoHoldingTests.LoadingPhaseSnapshotsAsync(fixture),
            snapshot => snapshot.CargoHoldingDeadlineAt == moved);

        await TickUntilAsync(fixture, firstStart + fixture.Options.CargoHoldingTimeout + TimeSpan.FromSeconds(30));
        JourneyRuntimeRow pastTheOldDeadline = await fixture.RuntimeAsync();
        Assert.NotEqual(LoadingPhaseStates.Closed, pastTheOldDeadline.LoadingPhaseState);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, pastTheOldDeadline.Stage);
        Assert.Null((await MembershipAsync(fixture, SecondDemand)).RemovedAt);
        Assert.Null((await MembershipAsync(fixture, ThirdDemand)).RemovedAt);
        int checksBefore = await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture);
        await TickUntilAsync(fixture, moved - TimeSpan.FromSeconds(1));
        int wireBefore = fixture.Peer.Lines.Count;

        await TickUntilAsync(fixture, moved + TimeSpan.FromSeconds(10));
        // 线序（向量 CV-STAGING-CARGO-HOLDING-TIMEOUT）：关闭的业务快照、空清单、计划，最后才是离站核验——核验问的下一站要是截断之后的计划。
        // 每一轮开头会补发车还没确认的旧行，所以只看每条报文第一次上线、且此前没发过的。
        HashSet<string> seen = [.. fixture.Peer.Lines.Take(wireBefore).Select(line => Header(line).MessageId)];
        string[] sent = [.. fixture.Peer.Lines.Skip(wireBefore).Select(Header)
            .Where(header => seen.Add(header.MessageId))
            .Select(header => header.MessageType)
            .Where(type => type is "VehicleBusinessStateSnapshot" or "CurrentStopWorklistSnapshot" or "UpcomingStopPlanSnapshot" or "PreDepartureSafetyCheck")];
        Assert.True(
            sent.SequenceEqual(
                ["VehicleBusinessStateSnapshot", "CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot", "PreDepartureSafetyCheck"]),
            string.Join(", ", sent));
        JourneyRuntimeRow expired = await fixture.RuntimeAsync();
        Assert.Equal(
            (LoadingPhaseStates.Closed, LoadingClosedReasons.CargoHoldingTimeout),
            (expired.LoadingPhaseState, expired.LoadingClosedReason));
        foreach (string demandId in new[] { SecondDemand, ThirdDemand })
        {
            JourneyDemandRow released = await MembershipAsync(fixture, demandId);
            Assert.NotNull(released.RemovedAt);
            Assert.NotEqual(DemandJourneyLookup.ReleasedAtStationDeadlineReason, released.RemovalReason);
            Assert.True(
                await DemandJourneyLookup.ReleasedForRedispatch(fixture.Context).AnyAsync(row => row.DemandId == demandId, Token),
                $"{demandId} is not waiting to be dispatched again.");
            Assert.Equal(
                DemandExecutionStatus.Accepted,
                (await fixture.Context.AcceptedDemands.AsNoTracking().SingleAsync(row => row.DemandId == demandId, Token)).Status);
        }
        await SuppressionAssertions.AssertNothingSuppressedAsync(fixture.Context);
        Assert.Equal(JourneyDemandStatuses.Loaded, (await MembershipAsync(fixture, Demand)).Status);
        Assert.Null((await MembershipAsync(fixture, Demand)).RemovedAt);
        Assert.Equal("DEMAND_RELEASED", await LastStopEndedReasonAsync(fixture));
        Assert.True(
            await Batch7CargoHoldingTests.DepartureChecksSentAsync(fixture) > checksBefore,
            "The expiry did not send the vehicle on.");
    }

    /// <summary>
    /// 释放不挪期限：追加挪过的期限在那条需求被放回之后照旧（不退回第一篮的起算，也不挪到放回时刻）。追加之后、车还没开到那个停靠时就放回，
    /// 引擎在这之后才第一次读到它：追加是事实，它照样挪期限（CP-0014 第五节第 3 处「派给这辆车时才算」），放回本身不挪。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public async Task AReleaseNeitherMovesTheDeadlineNorTakesBackTheAppendsMove()
    {
        await using RuntimeFixture fixture = await ArrangeAsync(StopKind.StagingRulesOn);
        await AllowEnRouteAppendAsync(fixture);
        await LoadTheBasketAsync(fixture);

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        DateTimeOffset appendedAt = fixture.Clock.GetUtcNow();
        await AppendStagingDemandAsync(fixture, SecondDemand, SecondSublot);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await ReleaseAsync(fixture, SecondDemand);

        await TickAsync(fixture);
        await TickAsync(fixture);

        JourneyRuntimeRow journey = await fixture.RuntimeAsync();
        Assert.Equal(appendedAt, journey.CargoHoldingStartedAt);
        Assert.NotEqual(LoadingPhaseStates.Closed, journey.LoadingPhaseState);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static async Task TickAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    /// <summary>每次至多拨 20 秒、拨一次跑一轮，直到钟到 <paramref name="until"/>。</summary>
    private static async Task TickUntilAsync(RuntimeFixture fixture, DateTimeOffset until)
    {
        while (fixture.Clock.GetUtcNow() < until)
        {
            TimeSpan left = until - fixture.Clock.GetUtcNow();
            fixture.Clock.Advance(left < TimeSpan.FromSeconds(20) ? left : TimeSpan.FromSeconds(20));
            await fixture.ProveSlotDoorsClosedAsync();
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
        }
    }

    /// <summary>同站追加之后：纠错宽限一过原地离站核验、「到站」第 2 个停靠，停在等录入（照 <c>StagingDepartureRequestTests</c> 的做法）。</summary>
    private static async Task MoveToTheSecondStopAsync(RuntimeFixture fixture)
    {
        fixture.Clock.Advance(fixture.Options.SameStationAppendCorrectionGrace + TimeSpan.FromSeconds(1));
        await fixture.ProveSlotDoorsClosedAsync();
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await Batch7StopDrivenAdvanceDriver.AnswerDepartureSafetyAsync(
            fixture, Demand, Batch7StopDrivenAdvanceDriver.FirstSafetyResultId);
        await Batch7StopDrivenAdvanceDriver.ArriveAtCurrentStopAsync(fixture, SecondDemand, "TO_GATE");
        await fixture.ProveSlotDoorsClosedAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// 把一条还没开到的追加需求放回待派：与待送点装货中取消、出发的放回同一组写（归属移除、积压清受理标记、计划删空停靠），车还没到那个停靠。
    /// </summary>
    private static async Task ReleaseAsync(RuntimeFixture fixture, string demandId)
    {
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == demandId && row.RemovedAt == null, Token);
        await DemandReleaseService.StageReleasedMembershipAsync(fixture.Context, membership, fixture.Clock.GetUtcNow(), Token);
        await JourneyPlanRevisionStage.StageAsync(
            fixture.Context, membership.JourneyId, [demandId], currentStopMayGo: false, routing: null, Token);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static (string MessageId, string MessageType) Header(byte[] line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return (document.RootElement.GetProperty("messageId").GetString()!,
            document.RootElement.GetProperty("messageType").GetString()!);
    }

    private static Task<JourneyDemandRow> MembershipAsync(RuntimeFixture fixture, string demandId) =>
        fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => row.DemandId == demandId)
            .OrderByDescending(row => row.RemovedAt == null)
            .FirstAsync(Token);

    /// <summary>最后一张清单的 <c>stopEndedReason</c>（按清单号取最大的那一张）。</summary>
    private static async Task<string?> LastStopEndedReasonAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        return payloads
            .Select(json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement payload = document.RootElement.GetProperty("payload");
                JsonElement reason = payload.GetProperty("stopEndedReason");
                return (Revision: payload.GetProperty("worklistRevision").GetInt64(),
                    Reason: reason.ValueKind == JsonValueKind.Null ? null : reason.GetString());
            })
            .OrderBy(item => item.Revision)
            .Last()
            .Reason;
    }
}
