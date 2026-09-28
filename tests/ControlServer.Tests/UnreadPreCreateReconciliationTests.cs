using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 建单前的对账读没读到（SDK 超时这类非精确的 <c>Unknown</c>）：这张单一次都没发出过，之后 RIoT 明确答「没有这张单」时要照常建，
/// 取货腿、关卡腿、同车重建三种入口都一样（control-server#375）。
/// </summary>
/// <remarks>
/// <para>
/// 修之前，那一次没读到就把意图标成 <c>RESULT_UNKNOWN</c>，而 <c>RESULT_UNKNOWN</c> 之后读到 NotFound 一律按「建过单、结果不明」处理，
/// 永不再建：旅程停在 <c>PICKUP_ResultUnknown</c>／<c>GATE_ResultUnknown</c>／<c>OWN_ORDER_REBUILD_ORDER_UNCONFIRMED</c>，现场只能改库。
/// </para>
/// <para>
/// 反向那一格——建单真的发出去过（<c>CreateAttemptCount &gt; 0</c>）、回应丢了、RIoT 又说没有这张单——仍然不建第二张，
/// 见 <see cref="ACreateSentWhoseOrderRiotNoLongerHasIsNeverSentAgain"/>。
/// </para>
/// </remarks>
public sealed class UnreadPreCreateReconciliationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 受理后第一段腿（开往取货站）：建单前那一次对账读超时，本轮不建；下一轮读得到了、RIoT 答没有这张单，就建，只建一张，车到站照常进等录入。
    /// </summary>
    [Fact]
    public async Task APickupLegWhosePreCreateReadTimedOutIsCreatedOnceRiotAnswers()
    {
        await using RuntimeFixture fixture = await PickupReadTimedOutAsync();

        JourneyRuntimeRow unread = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, unread.Stage);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertNeverSentAsync(fixture, unread.PickupUpperId);

        fixture.Riot.AbsentOrdersReadAsTimeout = false;
        await RoundsAsync(fixture, 3);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
        fixture.Riot.MovementState = "MT_FINISHED";
        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, FirstDemandId, "TO_PICKUP");
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
    }

    /// <summary>
    /// 装好货、离站核验通过、开往关卡：建单前那一次对账读超时，本轮不建；下一轮 RIoT 答没有这张单，就建，只建一张。
    /// </summary>
    [Fact]
    public async Task AGateLegWhosePreCreateReadTimedOutIsCreatedOnceRiotAnswers()
    {
        await using RuntimeFixture fixture = await LoadedAndCheckedAsync();

        JourneyStopRow gate = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        await AssertNeverSentAsync(fixture, gate.UpperId);

        fixture.Riot.AbsentOrdersReadAsTimeout = false;
        await RoundsAsync(fixture, 3);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 同车重建（control-server#318）：已决定重建、新单的意图已落库，建单前那一次对账读超时——cs#366 会话的临时探针就是这样卡住的，
    /// 码停在 <c>OWN_ORDER_REBUILD_ORDER_UNCONFIRMED</c>。下一轮 RIoT 答没有这张单，就建，重建记成已建成，前后只多建一张。
    /// </summary>
    [Fact]
    public async Task ARebuildWhosePreCreateReadTimedOutIsCreatedOnceRiotAnswers()
    {
        await using RuntimeFixture fixture = await DispatchedToPickupAsync();
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        fixture.Riot.CancelOrder(before.PickupUpperId);
        await TickAndRunAsync(fixture);
        fixture.Riot.AbsentOrdersReadAsTimeout = true;

        await OwnOrderRebuildTests.PassTheDelayAsync(fixture);

        OwnOrderRebuildRow deciding = await SingleRebuildAsync(fixture);
        Assert.Equal(OwnOrderRebuildStates.Ordering, deciding.State);
        Assert.Equal("OWN_ORDER_REBUILD_ORDER_UNCONFIRMED", (await fixture.RuntimeAsync()).BlockReasonCode);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        await AssertNeverSentAsync(fixture, deciding.NewUpperId);

        fixture.Riot.AbsentOrdersReadAsTimeout = false;
        await RoundsAsync(fixture, 3);

        Assert.Equal(2, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Equal(OwnOrderRebuildStates.Rebuilt, (await SingleRebuildAsync(fixture)).State);
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 关卡腿建单前读超时之后，会话不就绪（真车载端在本车有在途单时整段不就绪；这里单还没建，车载端照理是就绪的，
    /// 这一格按最坏情况——它为了别的原因不就绪——来断）：闸门后面不建，码是闸门自己的；会话回到就绪，就建，只建一张。
    /// </summary>
    /// <remarks>
    /// 闸门后面的 <c>NameUnconfirmedOrderWithoutThePeerAsync</c> 只对账发出过建单的意图（<c>CreateAttemptCount &gt; 0</c>），
    /// 从没发出过的留给闸门前面（control-server#367）。本票没有改这一条：补建只发生在会话就绪、闸门前面那一侧。
    /// </remarks>
    [Fact]
    public async Task ANeverSentGateLegWaitsBehindTheSessionGateAndIsCreatedOnceTheSessionIsReady()
    {
        await using RuntimeFixture fixture = await LoadedAndCheckedAsync();
        fixture.Riot.AbsentOrdersReadAsTimeout = false;
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);

        await RoundsAsync(fixture, 3);

        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await fixture.RuntimeAsync()).BlockReasonCode);
        await AssertNeverSentAsync(fixture, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);

        await fixture.RestoreSessionReadyAsync();
        await RoundsAsync(fixture, 3);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 建单前读超时之后、补建之前，车况变坏了：RIoT 报急停，或者车载端的离站摘要说仓没锁好。补建不再有离站核验护着
    /// （那一次核验早在读超时那一轮之前就用掉了），所以补建前重看车况：不行就不建，码写成 <c>{腿}_CREATE_WAITING_VEHICLE</c>，
    /// 等下一轮；车况恢复之后恰好建一张。
    /// </summary>
    /// <remarks>
    /// 调度判为准入线①：从离站核验通过到真正建单，中间可能隔几轮甚至几分钟，门、车况、急停都可能变了，而一张新单发出去车就会动。
    /// 判据与同车重建（cs#366 M1）是同一组：<c>VehicleConditionReasonsAsync</c>。拿掉这道检查，「一张都没建」那一行红。
    /// </remarks>
    [Theory]
    [InlineData("TO_PICKUP", "emergency")]
    [InlineData("TO_PICKUP", "slot-unlocked")]
    [InlineData("TO_GATE", "emergency")]
    [InlineData("TO_GATE", "slot-unlocked")]
    public async Task ANeverSentLegWaitsWhileTheVehicleMayNotMoveAndIsCreatedOnceItMay(string purpose, string worsenedBy)
    {
        await using RuntimeFixture fixture = purpose == "TO_PICKUP" ? await PickupReadTimedOutAsync() : await LoadedAndCheckedAsync();
        fixture.Riot.AbsentOrdersReadAsTimeout = false;
        if (worsenedBy == "emergency")
        {
            fixture.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];
        }
        else
        {
            await fixture.SetDepartureSummaryAsync(allTargetSlotsLocked: false);
        }

        await RoundsAsync(fixture, 3);

        Assert.Equal(0, fixture.Riot.CreateCount(purpose));
        string leg = purpose == "TO_PICKUP" ? "PICKUP" : "GATE";
        Assert.Equal($"{leg}_CREATE_WAITING_VEHICLE", (await fixture.RuntimeAsync()).BlockReasonCode);
        await AssertNeverSentAsync(fixture, (await CurrentStopAsync(fixture, FirstDemandId)).UpperId);

        fixture.Riot.SafetyReasons = [];
        await fixture.SetDepartureSummaryAsync();
        await RoundsAsync(fixture, 3);

        Assert.Equal(1, fixture.Riot.CreateCount(purpose));
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync(purpose));
        Assert.Null((await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 同一道车况检查也管 <c>PENDING_RECONCILIATION</c>、从没发出过的意图：它在到站等待阶段同样会被 <c>EnsureMovementConfirmedAsync</c>
    /// 建出去（建单门禁关着时的演练、「已决定未建单」之后的重启），本票之前就是这样，只是少见。这里把读超时留下的意图改回
    /// <c>PENDING_RECONCILIATION</c> 来造这个状态。
    /// </summary>
    [Fact]
    public async Task APendingNeverSentGateLegAlsoWaitsWhileTheVehicleMayNotMove()
    {
        await using RuntimeFixture fixture = await LoadedAndCheckedAsync();
        fixture.Riot.AbsentOrdersReadAsTimeout = false;
        string gateUpperId = (await CurrentStopAsync(fixture, FirstDemandId)).UpperId;
        await using (ControlServerDbContext writing = new(fixture.DbOptionsForTests))
        {
            OrderIntentRow intent = await writing.OrderIntents.SingleAsync(row => row.UpperId == gateUpperId, Token);
            intent.Status = "PENDING_RECONCILIATION";
            await writing.SaveChangesAsync(Token);
        }
        fixture.Riot.SafetyReasons = ["RIOT_EMERGENCY_NOT_OK"];

        await RoundsAsync(fixture, 3);

        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("GATE_CREATE_WAITING_VEHICLE", (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Riot.SafetyReasons = [];
        await RoundsAsync(fixture, 3);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_GATE"));
    }

    /// <summary>
    /// 反向那一格：关卡腿的建单发出去过（<c>CreateAttemptCount == 1</c>），回应丢了，之后 RIoT 说没有这张单。
    /// 发出去的那一次可能已经让车动了、也可能 RIoT 之后才把它列出来，所以不再建，意图停在 <c>RESULT_UNKNOWN</c>，只建过一张。
    /// </summary>
    /// <remarks>修前修后都绿。它要红给放宽条件的变异看：把「从没发出过」的判断放宽到发出过的意图，这里就会建出第二张。</remarks>
    [Fact]
    public async Task ACreateSentWhoseOrderRiotNoLongerHasIsNeverSentAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync(() => fixture.Riot.LoseNextCreateResponseOf = "TO_GATE");
        fixture.Context.ChangeTracker.Clear();
        JourneyStopRow gate = await CurrentStopAsync(fixture, FirstDemandId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        fixture.Riot.ForgetOrder(gate.UpperId);

        await RoundsAsync(fixture, 5);

        Assert.Equal(1, fixture.Riot.CreateCount("TO_GATE"));
        OrderIntentRow intent = await IntentAsync(fixture, gate.UpperId);
        Assert.Equal(("RESULT_UNKNOWN", 1), (intent.Status, intent.CreateAttemptCount));
        Assert.Equal("GATE_ResultUnknown", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>受理后派往取货站那一轮，建单前的对账读超时。</summary>
    private static async Task<RuntimeFixture> PickupReadTimedOutAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.Riot.AbsentOrdersReadAsTimeout = true;
        await TickAndRunAsync(fixture);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>装货、离站核验通过，开往关卡那一轮建单前的对账读超时。</summary>
    private static async Task<RuntimeFixture> LoadedAndCheckedAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync(() => fixture.Riot.AbsentOrdersReadAsTimeout = true);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<RuntimeFixture> DispatchedToPickupAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Riot.MovementState = "MT_FINISHED";
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    /// <summary>这张单一次都没发出过：没有建单计数，也没有建单尝试。本票放开的只有这一种意图。</summary>
    private static async Task AssertNeverSentAsync(RuntimeFixture fixture, string upperId)
    {
        OrderIntentRow intent = await IntentAsync(fixture, upperId);
        Assert.Equal((0, (string?)null), (intent.CreateAttemptCount, intent.CreateAttemptId));
    }

    private static async Task<OrderIntentRow> IntentAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OrderIntents.AsNoTracking().SingleAsync(row => row.UpperId == upperId, Token);
    }

    private static async Task<OwnOrderRebuildRow> SingleRebuildAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.OwnOrderRebuilds.AsNoTracking().SingleAsync(Token);
    }

    private static async Task RoundsAsync(RuntimeFixture fixture, int rounds)
    {
        for (int round = 0; round < rounds; round++)
        {
            await fixture.HearFromPeerAsync();
            fixture.Context.ChangeTracker.Clear();
            await TickAndRunAsync(fixture);
            fixture.Context.ChangeTracker.Clear();
        }
    }
}
