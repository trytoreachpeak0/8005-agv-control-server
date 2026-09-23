using System.Text.Json;
using ControlServer.Application;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.ForeignOrders;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 我们车上运行中的外来订单：认出即在 RIoT 取消，只取消一次，回查确认终结之前这辆车照样阻断；队列中、看不出车、不在我们车上的
/// 订单一律不读、不报、不动（control-server#330；需求基线 v1.5.0 修订后的 REQ-0148、REQ-0164）。
/// </summary>
/// <remarks>
/// <para>
/// 假 RIoT 是 <see cref="RecordingRiot"/>：按状态列单会同时列出本服务端自己建的在途单（按 RIoT 的样子：过了 QUEUEING 就带执行车辆），
/// 所以每一条驱动旅程的用例都让自己的单走一遍归属判定——自己的单若被当成外来单取消，<see cref="RecordingRiot.OrderCommands"/> 里看得见。
/// </para>
/// <para>
/// 取消后的状态按实验室知识（<c>rcs/riot-behavior-lab/knowledge/behavioral-contracts.md</c>）写：经 API 对 EXECUTING（BC-ORDER-003）或
/// HELD（BC-ORDER-006 第 2 条）的单发 <c>CMD_ORDER_CANCEL</c> 落到 CANCELLED（2）；在车上单机取消落到 HANG（9，BC-ORDER-015），
/// 那不是终结。对 HANG 的单经 API 取消会落到哪，实验室没有结论，所以这里不假设它一定成功：「发出后仍在运行」有自己的用例。
/// </para>
/// </remarks>
public sealed class ForeignRunningOrderTests
{
    private const string ForeignOrderId = "order-foreign-0001";
    private const string ForeignUpperId = "MES-FIELD-7788";
    private const string OtherVehicleKey = "BROKERX-0000000000000000000000000000ffff";
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 范围：只处理运行中、看得见执行车辆、且那辆车是我们的 -----------------------------------------------------------

    /// <summary>
    /// 我们车上运行中的外来订单（EXECUTING、HELD、HANG 三种都看得见执行车辆）：认出即取消，只发一次，发的只有取消；
    /// 回查读到 CANCELLED，记录落 <c>ENDED</c>，审计里那一次尝试对账为 Confirmed。
    /// </summary>
    [Theory]
    [InlineData(RiotOrderState.Executing)]
    [InlineData(RiotOrderState.Paused)]
    [InlineData(RiotOrderState.Hang)]
    [Trait("Requirement", "REQ-0164")]
    [Trait("Requirement", "REQ-0148")]
    public async Task Req0164AForeignOrderRunningOnOurVehicleIsCancelledOnceAndOnlyCancelled(int orderState)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, orderState, fixture.Options.VehicleKey);

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            [(RiotOrderCommandKind.Cancel, ForeignOrderId)],
            fixture.Riot.OrderCommands.Select(command => (command.Kind, command.OrderId)));
        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal(
            (ForeignRiotOrderOwnership.Foreign, ForeignRiotOrderStates.Ended, ForeignRiotOrderCancelResults.Cancelled, (int?)2),
            (row.Ownership, row.State, row.CancelResult, row.EndedOrderState));
        RiotOrderCommandAuditRow audit = Assert.Single(await AuditAsync(fixture));
        Assert.Equal(
            (RiotCommandTypeNames.CancelOrder, fixture.Options.AgvId, (string?)ForeignOrderId, RiotOrderCommandOutcome.Confirmed),
            (audit.CommandType, audit.AgvId, audit.TargetOrderId, audit.Outcome));
        Assert.Equal(audit.CommandAuditId, row.CancelCommandAuditId);
        Assert.Empty(await HeldAsync(fixture));
    }

    /// <summary>
    /// 看不出跑在我们车上的订单，不读、不报、不动：别的车上在跑的、队列中（哪怕指定的是我们的车、哪怕执行车辆栏里就写着我们的车）、
    /// 在跑却没有执行车辆的、跑在已归档（不再是当前有效绑定）的车上的。连跑三轮：零订单命令、零审计行、零记录、零告警。
    /// </summary>
    /// <remarks>
    /// 先断言三轮都真的列过单：不看就不动的「零」证明不了护栏。反向验证见证据目录的变异汇总：去掉「执行车辆是我们的」
    /// 或「运行态」任一个判据，对应的格子变红。
    /// </remarks>
    [Theory]
    [InlineData("running-on-another-vehicle")]
    [InlineData("queueing-appointed-to-our-vehicle")]
    [InlineData("queueing-with-our-key-as-executing-vehicle")]
    [InlineData("running-without-an-executing-vehicle")]
    [InlineData("running-on-our-archived-vehicle")]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AnOrderThatDoesNotShowItRunsOnOurVehicleIsNeitherRecordedNorAlarmedNorCommanded(string variant)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        string ours = fixture.Options.VehicleKey;
        switch (variant)
        {
            case "running-on-another-vehicle":
                fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, OtherVehicleKey);
                break;
            case "queueing-appointed-to-our-vehicle":
                fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Queueing, null, appointVehicleKey: ours);
                break;
            case "queueing-with-our-key-as-executing-vehicle":
                fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Queueing, ours);
                break;
            case "running-without-an-executing-vehicle":
                fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, null, appointVehicleKey: ours);
                break;
            case "running-on-our-archived-vehicle":
                fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, ours);
                fixture.Context.Set<AgvLifecycleRow>().Add(new AgvLifecycleRow
                {
                    AgvId = fixture.Options.AgvId,
                    LifecycleGeneration = fixture.Options.AgvLifecycleGeneration,
                    Archived = true,
                    UpdatedAt = fixture.Clock.GetUtcNow(),
                });
                await fixture.Context.SaveChangesAsync(Token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        }

        Assert.True(fixture.Riot.ListingReads >= 3, $"The listing was read {fixture.Riot.ListingReads} times.");
        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Empty(await AuditAsync(fixture));
        Assert.Empty(await fixture.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(fixture.ForeignOrderLog.Entries);
    }

    /// <summary>
    /// 列单没读全（翻页没覆盖全部记录，或读失败）什么也推不出：不记、不发；已经挡着的车继续挡着，不因为「这一次没看见」就放行。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AnIncompleteListingConcludesNothing()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        fixture.Riot.ListingComplete = false;

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.True(fixture.Riot.ListingReads >= 1);
        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Empty(await fixture.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));

        // 已经挡着的一张（取消发了、还没见终结）：列单又读不全时，照样挡着。
        fixture.Riot.ListingComplete = true;
        fixture.Riot.CancelTakesEffect = false;
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.CancelSent, (await RowAsync(fixture)).State);
        fixture.Riot.ListingComplete = false;
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Single(fixture.Riot.OrderCommands);
        Assert.Equal(ForeignRiotOrderStates.CancelSent, (await RowAsync(fixture)).State);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
    }

    // ---- 归属：只认 OrderIntent 与订单命令审计，upperId 只作辅助 ---------------------------------------------------------

    /// <summary>
    /// upperId 长得像本服务端的（<c>W2G-</c> 开头，大小写、前导空白都算），库里却没有它的建单意图：认不准，只阻断告警，不取消。
    /// 这就是「前缀不能单独作判据」的那一面：另一个用同一前缀的服务端实例建的单，不能被这里当成外来单取消。
    /// </summary>
    [Theory]
    [InlineData("W2G-99999999-9999-4999-8999-999999999999-PICKUP-1")]
    [InlineData("w2g-99999999-9999-4999-8999-999999999999-GATE-1")]
    [InlineData("  W2G-99999999-9999-4999-8999-999999999999-REBUILD-1-1")]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AnUpperIdShapedLikeOursWithoutAnIntentIsHeldAndAlarmedButNeverCancelled(string upperId)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.PlaceOrder(ForeignOrderId, upperId, RiotOrderState.Executing, fixture.Options.VehicleKey);

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        }

        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Empty(await AuditAsync(fixture));
        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal(
            (ForeignRiotOrderOwnership.Unproven, ForeignRiotOrderStates.HeldUnproven, (string?)null),
            (row.Ownership, row.State, row.CancelCommandAuditId));
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        (LogLevel level, string message) = Assert.Single(fixture.ForeignOrderLog.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains(ForeignOrderId, message, StringComparison.Ordinal);
        Assert.Contains(fixture.Options.AgvId, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 反过来：upperId 一点不像本服务端的，库里却有它的建单意图（按 upperId 或按 RIoT 的 orderId 对上）——那就是本服务端的单，
    /// 不记、不报、不动。归属只认意图与审计，不认长相。
    /// </summary>
    [Theory]
    [InlineData("by-upper-id")]
    [InlineData("by-riot-order-id")]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AnOrderThisServerHasAnIntentForIsItsOwnWhateverItsUpperIdLooksLike(string match)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        bool byUpperId = match == "by-upper-id";
        fixture.Context.OrderIntents.Add(new OrderIntentRow
        {
            MovementLegId = "leg-looks-foreign",
            DemandId = "D-LOOKS-FOREIGN",
            UpperId = byUpperId ? ForeignUpperId : "W2G-D-LOOKS-FOREIGN-PICKUP-1",
            Purpose = "TO_PICKUP",
            TargetStationId = "PICKUP-1",
            VehicleKey = fixture.Options.VehicleKey,
            MapId = fixture.Options.MapId,
            DestinationStationId = 12,
            CreatedAt = fixture.Clock.GetUtcNow(),
            Status = "CONFIRMED",
            OrderId = byUpperId ? null : ForeignOrderId,
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Riot.PlaceOrder(
            ForeignOrderId, byUpperId ? ForeignUpperId : null, RiotOrderState.Executing, fixture.Options.VehicleKey);

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.True(fixture.Riot.ListingReads >= 2);
        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Empty(await fixture.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(fixture.ForeignOrderLog.Entries);
    }

    /// <summary>
    /// 本服务端对它发过订单命令（审计里有），库里却没有它的建单意图：说明曾经把它当自己的单管过，归属证明不了，不取消，只阻断告警。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AnOrderThisServerHasCommandedButHasNoIntentForIsNotProvablyForeign()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Context.RiotOrderCommandAudit.Add(new RiotOrderCommandAuditRow
        {
            CommandAuditId = "audit-earlier-hold",
            CommandType = RiotCommandTypeNames.OrderHold,
            AgvId = fixture.Options.AgvId,
            TargetUpperId = ForeignUpperId,
            TargetOrderId = ForeignOrderId,
            AttemptNumber = 1,
            RequestSemanticSha256 = new string('a', 64),
            IssuedAt = fixture.Clock.GetUtcNow().AddHours(-1),
            Outcome = RiotOrderCommandOutcome.Confirmed,
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(fixture.Riot.OrderCommands);
        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal((ForeignRiotOrderOwnership.Unproven, ForeignRiotOrderStates.HeldUnproven), (row.Ownership, row.State));
    }

    /// <summary>
    /// 本服务端自己的在途单跑在自己的车上，永远不被当成外来单：受理、建单、确认，车载端因这张单未就绪，连跑几轮，
    /// 一条订单命令都没有、一条记录都没有。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164ThisServersOwnRunningOrderIsNeverTakenForAForeignOne()
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);

        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            await fixture.HearFromPeerAsync();
        }

        Assert.True(fixture.Riot.ListingReads >= 3);
        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Empty(await fixture.Context.ForeignRiotOrders.AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(fixture.ForeignOrderLog.Entries);
    }

    /// <summary>
    /// <see cref="ForeignRunningOrders.OwnUpperIdPrefix"/> 的前提：本服务端建的每一张单，upperId 都以它开头。这一条断了，
    /// 「像自己的」那一格就会把别的实例建的同前缀单判成外来单取消。
    /// </summary>
    [Fact]
    public async Task TheUpperIdsThisServerCreatesStartWithTheOwnPrefix()
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();

        string[] upperIds = await fixture.Context.OrderIntents.AsNoTracking().Select(row => row.UpperId).ToArrayAsync(Token);
        Assert.NotEmpty(upperIds);
        Assert.All(upperIds, upperId => Assert.StartsWith(ForeignRunningOrders.OwnUpperIdPrefix, upperId, StringComparison.Ordinal));
    }

    // ---- 防误伤：发前再读、只发一次、发出后仍在运行转人工 ------------------------------------------------------------------

    /// <summary>
    /// 发取消之前再读一次：认出它的那一次读之后、取消之前，它已经不在我们车上运行了（被人结束了、换到别的车、回到队列）——不发。
    /// 结束了的记 <c>ENDED</c>，离开的记 <c>LEFT_VEHICLE</c>，这辆车都不再为它挡着。
    /// </summary>
    [Theory]
    [InlineData("ended", ForeignRiotOrderStates.Ended)]
    [InlineData("moved-to-another-vehicle", ForeignRiotOrderStates.LeftVehicle)]
    [InlineData("back-in-the-queue", ForeignRiotOrderStates.LeftVehicle)]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164TheOrderIsReadAgainBeforeTheCancelAndLeftAloneWhenItNoLongerRunsOnOurVehicle(
        string change,
        string expectedState)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        fixture.Riot.BeforeListing = read =>
        {
            if (read != 2)
            {
                return;
            }
            switch (change)
            {
                case "ended":
                    fixture.Riot.EndPlacedOrder(ForeignOrderId, RiotOrderState.Cancelled);
                    break;
                case "moved-to-another-vehicle":
                    fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, OtherVehicleKey);
                    break;
                case "back-in-the-queue":
                    fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Queueing, null);
                    break;
            }
        };

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.True(fixture.Riot.ListingReads >= 2);
        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Empty(await AuditAsync(fixture));
        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal((expectedState, (string?)null), (row.State, row.CancelCommandAuditId));
        Assert.Empty(await HeldAsync(fixture));
    }

    /// <summary>
    /// 发前那一次再读没读全：这一轮不发，记录留在已认出、车照样挡着；下一轮读全了、它仍在我们车上跑，才发——而且只发一次。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AReReadThatDoesNotAnswerDefersTheCancelToARoundWhoseReReadDoes()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        fixture.Riot.BeforeListing = read => fixture.Riot.ListingComplete = read != 2;

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Equal(ForeignRiotOrderStates.Detected, (await RowAsync(fixture)).State);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Single(fixture.Riot.OrderCommands);
        Assert.Equal(ForeignRiotOrderStates.Ended, (await RowAsync(fixture)).State);
    }

    /// <summary>
    /// 取消发出去了，订单还在跑（RIoT 受理了却没动，或者拒了，或者不知道）：再也不发第二次。等过一段落定时间仍未终结，
    /// 记 <c>STILL_RUNNING_AFTER_CANCEL</c>、Error 级告警转人工，车照样挡着；之后它自己终结了（人处理了），回查读到终态才放行。
    /// </summary>
    /// <remarks>
    /// 时钟每一步都真的拨了，并断言拨过：落定时间之前那一轮没转人工、之后那一轮转了，判据测到的是时间，不是轮数。
    /// </remarks>
    [Theory]
    [InlineData(RiotCommandCallDisposition.Accepted)]
    [InlineData(RiotCommandCallDisposition.Failed)]
    [InlineData(RiotCommandCallDisposition.Unknown)]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164ACancelIsSentOnceAndAnOrderStillRunningAfterItIsHandedToAPerson(
        RiotCommandCallDisposition disposition)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.CancelTakesEffect = false;
        fixture.Riot.OrderCommandDisposition = disposition;
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);

        DateTimeOffset sentAt = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.CancelSent, (await RowAsync(fixture)).State);

        fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime - TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.CancelSent, (await RowAsync(fixture)).State);
        Assert.DoesNotContain(fixture.ForeignOrderLog.Entries, entry => entry.Level == LogLevel.Error);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ForeignRunningOrderSupervisor.CancelSettleTime, fixture.Clock.GetUtcNow() - sentAt);
        for (int round = 0; round < 3; round++)
        {
            await fixture.Engine.ExecuteOnceAsync(Token);
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Single(fixture.Riot.OrderCommands);
        Assert.Single(await AuditAsync(fixture));
        ForeignRiotOrderRow held = await RowAsync(fixture);
        Assert.Equal(
            (ForeignRiotOrderStates.StillRunningAfterCancel, ForeignRiotOrderCancelResults.StillRunning, disposition.ToString()),
            (held.State, held.CancelResult, held.CancelCallDisposition));
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        (LogLevel level, string message) = Assert.Single(fixture.ForeignOrderLog.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(ForeignOrderId, message, StringComparison.Ordinal);

        fixture.Riot.EndPlacedOrder(ForeignOrderId, RiotOrderState.Cancelled);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Single(fixture.Riot.OrderCommands);
        ForeignRiotOrderRow ended = await RowAsync(fixture);
        Assert.Equal((ForeignRiotOrderStates.Ended, (int?)RiotOrderState.Cancelled), (ended.State, ended.EndedOrderState));
        Assert.Empty(await HeldAsync(fixture));
    }

    /// <summary>
    /// 同一动作第二次发生：取消发出后没见效，订单换到别的车（不再挡我们的车），又回到我们车上跑——不再发第二次取消，直接转人工。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AnOrderCancelledOnceThatComesBackToOurVehicleIsNotCancelledAgain()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.CancelTakesEffect = false;
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Single(fixture.Riot.OrderCommands);

        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, OtherVehicleKey);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(ForeignRiotOrderStates.LeftVehicle, (await RowAsync(fixture)).State);
        Assert.Empty(await HeldAsync(fixture));

        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Single(fixture.Riot.OrderCommands);
        Assert.Equal(ForeignRiotOrderStates.StillRunningAfterCancel, (await RowAsync(fixture)).State);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
    }

    // ---- 0/1 门禁不放宽 ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 外来单还在跑（取消没见效，或归属认不准不取消）：这辆空闲车不接新派车——目录里有合格需求也不受理、不建单；回查读到它明确终结的
    /// 那一轮起才照常派车。
    /// </summary>
    [Theory]
    [InlineData("cancel-not-yet-effective")]
    [InlineData("ownership-unproven")]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AVehicleAForeignOrderRunsOnTakesNoNewDispatchUntilTheOrderIsReadBackEnded(string variant)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.CancelTakesEffect = false;
        fixture.Riot.PlaceOrder(
            ForeignOrderId,
            variant == "ownership-unproven" ? "W2G-99999999-9999-4999-8999-999999999999-PICKUP-1" : ForeignUpperId,
            RiotOrderState.Executing,
            fixture.Options.VehicleKey);

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);

        fixture.Riot.EndPlacedOrder(ForeignOrderId, RiotOrderState.Cancelled);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(ForeignRiotOrderStates.Ended, (await RowAsync(fixture)).State);
        Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    // ---- 会话因本车在途单未就绪、本车在途单与外来单同时存在 ------------------------------------------------------------------

    /// <summary>
    /// 真车载端行驶中整段未就绪（本车在途单所致，形状同 <see cref="PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync"/>），
    /// 同一辆车上又跑着一张外来单：外来单照样认出、取消一次；本车那张单不碰（没有对它的命令，意图仍是 CONFIRMED）；
    /// 外来单取消之后不触发 #318 的重建与「在 RIoT 被取消」告警（REQ-0360 末句）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    [Trait("Requirement", "REQ-0360")]
    public async Task Req0164AForeignOrderIsRecognisedWhileTheSessionIsNotReadyOnTheOwnOrderAndOnlyItIsCancelled()
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);

        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(
            [(RiotOrderCommandKind.Cancel, ForeignOrderId)],
            fixture.Riot.OrderCommands.Select(command => (command.Kind, command.OrderId)));
        Assert.Equal(ForeignRiotOrderStates.Ended, (await RowAsync(fixture)).State);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(
            (JourneyRuntimeStage.AwaitingPickupArrival, "ONBOARD_SESSION_NOT_READY"),
            (runtime.Stage, runtime.BlockReasonCode));
        Assert.Empty(await fixture.Context.OwnOrderRebuilds.AsNoTracking().ToArrayAsync(Token));
        Assert.DoesNotContain(fixture.EngineLog.Entries, entry => entry.Message.Contains(ForeignOrderId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 同一个现场，外来单的取消还没见效：会话的「未知」这时不能归给本服务端自己的在途单——#314 的放行不适用，派往取货站的计划不发；
    /// 外来单终结之后，放行照旧，计划恰好发一次。
    /// </summary>
    [Theory]
    [InlineData("cancel-not-yet-effective")]
    [InlineData("ownership-unproven")]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164AForeignOrderOnTheVehicleIsNotExplainedAsTheOwnOrderInFlight(string variant)
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();
        await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.CancelTakesEffect = false;
        fixture.Riot.PlaceOrder(
            ForeignOrderId,
            variant == "ownership-unproven" ? "W2G-99999999-9999-4999-8999-999999999999-PICKUP-1" : ForeignUpperId,
            RiotOrderState.Executing,
            fixture.Options.VehicleKey);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        Assert.Empty(PlanLinesSent(fixture));

        fixture.Riot.EndPlacedOrder(ForeignOrderId, RiotOrderState.Cancelled);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(await HeldAsync(fixture));
        Assert.Single(PlanLinesSent(fixture));
    }

    // ---- 告警与审计的内容 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 记录与告警里有订单、车、时间、取消结果：记录行按订单号落，带 upperId、agvId、deviceKey、认出时刻、发取消时刻、取消结果与
    /// 终结状态和时刻；审计行是那一次取消尝试（命令、车、目标订单、结果）；认出、发取消、终结各有一条告警日志，写着订单号与车。
    /// </summary>
    /// <remarks>三个时刻分别拨过钟再取，并断言它们不同：固定时钟下「记的是哪一刻」测不出来。</remarks>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164TheAlarmAndTheAuditRecordNameTheOrderTheVehicleTheTimeAndTheCancelResult()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.CancelTakesEffect = false;
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);

        DateTimeOffset detectedAt = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        fixture.Riot.EndPlacedOrder(ForeignOrderId, RiotOrderState.Cancelled);
        DateTimeOffset endedAt = fixture.Clock.GetUtcNow();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.NotEqual(detectedAt, endedAt);
        ForeignRiotOrderRow row = await RowAsync(fixture);
        Assert.Equal(
            (ForeignUpperId, fixture.Options.AgvId, fixture.Options.VehicleKey, (int?)RiotOrderState.Executing),
            (row.UpperId, row.AgvId, row.DeviceKey, row.OrderStateAtDetection));
        Assert.Equal(
            (detectedAt, (DateTimeOffset?)detectedAt, (DateTimeOffset?)detectedAt),
            (row.DetectedAt, row.CancelDecidedAt, row.CancelSentAt));
        Assert.Equal(
            (ForeignRiotOrderCancelResults.Cancelled, (DateTimeOffset?)endedAt, (int?)RiotOrderState.Cancelled, (DateTimeOffset?)endedAt),
            (row.CancelResult, row.CancelResultAt, row.EndedOrderState, row.EndedAt));

        RiotOrderCommandAuditRow audit = Assert.Single(await AuditAsync(fixture));
        Assert.Equal(
            (RiotCommandTypeNames.CancelOrder, fixture.Options.AgvId, ForeignUpperId, (string?)ForeignOrderId, 1, detectedAt),
            (audit.CommandType, audit.AgvId, audit.TargetUpperId, audit.TargetOrderId, audit.AttemptNumber, audit.IssuedAt));
        Assert.Equal((RiotOrderCommandOutcome.Confirmed, (DateTimeOffset?)endedAt), (audit.Outcome, audit.ReconciledAt));
        Assert.Contains(ForeignRunningOrders.CancelReason, audit.ReceiptJson, StringComparison.Ordinal);

        string[] warnings = [.. fixture.ForeignOrderLog.Entries
            .Where(entry => entry.Level >= LogLevel.Warning)
            .Select(entry => entry.Message)];
        Assert.True(warnings.Length >= 3, string.Join(Environment.NewLine, warnings));
        Assert.All(warnings, message =>
        {
            Assert.Contains(ForeignOrderId, message, StringComparison.Ordinal);
            Assert.Contains(fixture.Options.AgvId, message, StringComparison.Ordinal);
        });
        Assert.Contains(warnings, message => message.Contains(ForeignRiotOrderCancelResults.Cancelled, StringComparison.Ordinal));
    }

    // ---- 重启：「决定取消、未发出」与「已发出、未记账」 -------------------------------------------------------------------
    //
    // 崩溃用一次异常来代：保存失败（夹具的 SaveChanges 拦截），或假 RIoT 收下命令之后抛异常。引擎把监管的失败隔离在这一轮之内
    // （丢掉这次尝试留下的改动、记 2189、这一轮其余照常），崩溃点之后什么都没落库，与进程死掉在那里对库来说是同一件事；
    // 之后换一个新引擎（重启）接着跑。

    /// <summary>
    /// 进程崩在「决定取消」保存之后、审计尝试武装之前：取消一次都没发。重启后先再读一次，它仍在我们车上跑，发一次——不漏发。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164ACrashAfterTheCancelWasDecidedAndBeforeItWentOutSendsItOnceAfterTheRestart()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        fixture.SaveChanges.FailWhen = written => written.Contains("RiotOrderCommandAuditRow.CommandAuditId");

        await CrashingRoundAsync(fixture);

        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Equal(ForeignRiotOrderStates.CancelDecided, (await RowAsync(fixture)).State);
        Assert.Empty(await AuditAsync(fixture));

        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Single(fixture.Riot.OrderCommands);
        Assert.Equal(ForeignRiotOrderStates.Ended, (await RowAsync(fixture)).State);
        Assert.Single(await AuditAsync(fixture));
    }

    /// <summary>
    /// 同一个崩溃点，但重启前它已经不在我们车上跑了：重启后的再读挡住，一次都不发。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164ACancelDecidedBeforeACrashIsNotSentWhenTheOrderLeftOurVehicleMeanwhile()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        fixture.SaveChanges.FailWhen = written => written.Contains("RiotOrderCommandAuditRow.CommandAuditId");
        await CrashingRoundAsync(fixture);
        Assert.Equal(ForeignRiotOrderStates.CancelDecided, (await RowAsync(fixture)).State);

        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, OtherVehicleKey);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(fixture.Riot.OrderCommands);
        Assert.Equal(ForeignRiotOrderStates.LeftVehicle, (await RowAsync(fixture)).State);
    }

    /// <summary>
    /// 进程崩在取消已经到了 RIoT、应答还没记账之间：重启后不再发。回查读到 CANCELLED 就记终结、审计对账为 Confirmed；
    /// 读到仍在跑，按「发出后仍在运行」等过落定时间转人工——无论哪种，取消都只有那一次。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164ACrashAfterTheCancelWentOutAndBeforeItWasRecordedDoesNotSendItAgain(bool cancelTakesEffect)
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Riot.CancelTakesEffect = cancelTakesEffect;
        fixture.Riot.CrashAfterNextOrderCommand = true;
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);

        await CrashingRoundAsync(fixture);
        Assert.Single(fixture.Riot.OrderCommands);

        for (int round = 0; round < 3; round++)
        {
            fixture.Clock.Advance(ForeignRunningOrderSupervisor.CancelSettleTime);
            await fixture.Engine.ExecuteOnceAsync(Token);
        }

        Assert.Single(fixture.Riot.OrderCommands);
        RiotOrderCommandAuditRow audit = Assert.Single(await AuditAsync(fixture));
        ForeignRiotOrderRow row = await RowAsync(fixture);
        if (cancelTakesEffect)
        {
            Assert.Equal((ForeignRiotOrderStates.Ended, ForeignRiotOrderCancelResults.Cancelled), (row.State, row.CancelResult));
            Assert.Equal(RiotOrderCommandOutcome.Confirmed, audit.Outcome);
        }
        else
        {
            Assert.Equal(ForeignRiotOrderStates.StillRunningAfterCancel, row.State);
            Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        }
    }

    /// <summary>
    /// 监管自己出了缺陷、每一轮都抛：这一轮其余照常——在途旅程照样到站往下走；已经挡着的车照样挡着（挡车读的是库，不是这一次监管）。
    /// </summary>
    /// <remarks>
    /// 真网关对 RIoT 的失败从不抛异常，所以这里的异常代表一个代码缺陷。它不能让所有车的旅程一起停下：外来单在这一轮没认出来，
    /// 派车照样过车辆安全读取里的 <c>RIOT_NONFINAL_ORDER_PRESENT</c>（0/1 门禁本身）。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task AFailingSupervisionDoesNotStopTheRoundAndAHeldVehicleStaysHeld()
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();
        fixture.Riot.CancelTakesEffect = false;
        fixture.Riot.PlaceOrder(ForeignOrderId, ForeignUpperId, RiotOrderState.Executing, fixture.Options.VehicleKey);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));

        fixture.Riot.ListingThrows = new InvalidOperationException("A defect in the supervision of foreign orders.");
        JourneyRuntimeRow arrived = await fixture.AdvanceToSublotWaitAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        Assert.Equal([fixture.Options.AgvId], await HeldAsync(fixture));
        Assert.Single(fixture.Riot.OrderCommands);
        Assert.Contains(
            fixture.EngineLog.Entries,
            entry => entry.Level == LogLevel.Error &&
                     entry.Message.Contains("supervision of foreign orders", StringComparison.Ordinal));
    }

    // ---- 看板 ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 看板「车上的外来订单」只列此刻挡着车的那几种，每行带车、订单、upperId、专用原因码与说明、认出与发取消的时刻、取消结果；
    /// 已终结、已离开我们车的不列。卡片把车、订单与说明都画出来。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0164")]
    public async Task Req0164TheDashboardShowsEveryVehicleAForeignOrderHoldsWithItsDedicatedReason()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        fixture.Context.ForeignRiotOrders.AddRange(
            Row("order-cancelling", "AGV-A", ForeignRiotOrderOwnership.Foreign, ForeignRiotOrderStates.CancelSent, now),
            Row("order-still-running", "AGV-B", ForeignRiotOrderOwnership.Foreign, ForeignRiotOrderStates.StillRunningAfterCancel, now),
            Row("order-unproven", "AGV-C", ForeignRiotOrderOwnership.Unproven, ForeignRiotOrderStates.HeldUnproven, now),
            Row("order-ended", "AGV-D", ForeignRiotOrderOwnership.Foreign, ForeignRiotOrderStates.Ended, now),
            Row("order-left", "AGV-E", ForeignRiotOrderOwnership.Foreign, ForeignRiotOrderStates.LeftVehicle, now));
        await fixture.Context.SaveChangesAsync(Token);

        object result = await new ForeignRunningOrdersQueryEndpoint().ReadAsync(fixture.Context, Token);
        using JsonDocument fact = JsonDocument.Parse(JsonSerializer.Serialize(result));

        Dictionary<string, JsonElement> byOrder = fact.RootElement.EnumerateArray()
            .ToDictionary(item => item.GetProperty("riotOrderId").GetString()!, StringComparer.Ordinal);
        Assert.Equal(["order-cancelling", "order-still-running", "order-unproven"], byOrder.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(ForeignRunningOrders.CancellingReason, byOrder["order-cancelling"].GetProperty("reasonCode").GetString());
        Assert.Equal(
            ForeignRunningOrders.StillRunningAfterCancelReason, byOrder["order-still-running"].GetProperty("reasonCode").GetString());
        Assert.Equal(ForeignRunningOrders.OwnershipUnprovenReason, byOrder["order-unproven"].GetProperty("reasonCode").GetString());
        Assert.All(byOrder.Values, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("reasonDescription").GetString()));
            Assert.Equal("UPPER-" + item.GetProperty("riotOrderId").GetString(), item.GetProperty("upperId").GetString());
        });
        Assert.Equal("AGV-B", byOrder["order-still-running"].GetProperty("agvId").GetString());

        string html = new ForeignRunningOrderCard().RenderFact(fact.RootElement);
        Assert.Contains("AGV-B", html, StringComparison.Ordinal);
        Assert.Contains("order-still-running", html, StringComparison.Ordinal);
        Assert.Contains(ForeignRunningOrders.StillRunningAfterCancelReason, html, StringComparison.Ordinal);
        Assert.DoesNotContain("order-ended", html, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>The vehicles a foreign order holds right now, as the journey runtime reads them, in a fresh context.</summary>
    private static async Task<string[]> HeldAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return [.. (await ForeignRunningOrders.HeldAgvIdsAsync(reading, Token)).Order(StringComparer.Ordinal)];
    }

    private static ForeignRiotOrderRow Row(string orderId, string agvId, string ownership, string state, DateTimeOffset at) => new()
    {
        RiotOrderId = orderId,
        UpperId = "UPPER-" + orderId,
        AgvId = agvId,
        DeviceKey = "KEY-" + agvId,
        Ownership = ownership,
        OwnershipBasis = "TEST",
        State = state,
        OrderStateAtDetection = RiotOrderState.Executing,
        DetectedAt = at,
        LastSeenRunningAt = at,
        CancelSentAt = ownership == ForeignRiotOrderOwnership.Foreign ? at : null,
        CancelResult = state == ForeignRiotOrderStates.StillRunningAfterCancel ? ForeignRiotOrderCancelResults.StillRunning : null,
        UpdatedAt = at,
    };

    /// <summary>这张单在库里的那一行，由一个新上下文读，免得读到引擎上下文里还没落库的改动。</summary>
    private static async Task<ForeignRiotOrderRow> RowAsync(RuntimeFixture fixture, string orderId = ForeignOrderId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.ForeignRiotOrders.AsNoTracking().SingleAsync(row => row.RiotOrderId == orderId, Token);
    }

    private static async Task<RiotOrderCommandAuditRow[]> AuditAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token);
    }

    /// <summary>
    /// 跑一轮，其中外来订单监管在注入的崩溃点失败：这一轮本身不被拖垮，失败记为 2189 的 Error 日志，之后重启引擎。
    /// </summary>
    private static async Task CrashingRoundAsync(RuntimeFixture fixture)
    {
        int before = fixture.EngineLog.Entries.Count;
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Contains(
            fixture.EngineLog.Entries.Skip(before),
            entry => entry.Level == LogLevel.Error &&
                     entry.Message.Contains("supervision of foreign orders", StringComparison.Ordinal));
        fixture.SaveChanges.FailWhen = null;
        await fixture.RecreateEngineAsync();
    }

    /// <summary>
    /// 受理那一轮：建单、确认，不推进（同 <see cref="PickupDispatchPlanPastOwnOrderTests"/> 的起点）。前置条件断言在这里。
    /// </summary>
    private static async Task<RuntimeFixture> AcceptedWithOrderConfirmedAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        return fixture;
    }

    private static JsonElement[] PlanLinesSent(RuntimeFixture fixture) =>
        [.. fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)).RootElement.Clone())
            .Where(root => root.GetProperty("messageType").GetString() == "UpcomingStopPlanSnapshot")];
}
