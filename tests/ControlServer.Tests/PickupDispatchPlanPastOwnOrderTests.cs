using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 派往取货站的那一版计划，在车载端因本服务端自己的在途单变为未就绪之后，仍恰好送达一次（control-server#314）。
/// </summary>
/// <remarks>
/// <para>
/// 失败现场是批次 7 出口 G3 的 <c>g3-task-type-admission-fail-closed</c>：取货那一段的 RIoT 单在受理那一轮就建成、确认，
/// 这版计划却要等下一轮推进、而且在就绪闸门之后才发。车载端在两轮之间读到车辆安全接口——车上挂着本服务端的未结束单，
/// 按 control-server#138 的设计回 <c>motionState=Unknown</c>——报 <c>VEHICLE_NOT_READY</c>，会话落到
/// <c>DEPARTURE_SAFETY_NOT_READY</c>；那张单结束之前它回不到 <c>Ready</c>，计划于是一直发不出去，发件箱 0 行。
/// </para>
/// <para>
/// <b>窗口是构造出来的，不是碰出来的。</b>引擎「这一轮建的旅程下一轮才推进」（<c>ExecuteOnceAsync</c>），所以第一轮之后、
/// 第二轮之前改会话行，就是现场那 200 毫秒的确定性版本；夹具的时钟不走，没有任何一条依赖真实计时器。
/// </para>
/// <para>
/// 放行的条件在 <c>JourneyRuntimeEngine.PublishPickupDispatchPlanPastOwnOrderAsync</c>。这里的负向用例一条对一个条件：
/// 任一条不成立都不发，因为那时「未就绪」可能不是自己的单造成的，而就绪闸门挡的正是那种情形。
/// </para>
/// </remarks>
public sealed class PickupDispatchPlanPastOwnOrderTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PlanMessageId => JourneyPlanBuilder.StableGuid(DemandId, "pickup-dispatch-plan");

    /// <summary>
    /// 失败现场本身：受理那一轮建单并确认，下一轮推进之前会话因这张单变为未就绪。修前发件箱 0 行、车一行都没收到。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task ThePickupPlanReachesTheVehicleWhoseSessionDroppedOnTheOwnOrderBeforeTheFirstAdvance()
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();

        await DropSessionOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        // 闸门照旧关着：本票放行的只有这一版计划，旅程仍然按会话未就绪记。
        Assert.Equal("ONBOARD_SESSION_NOT_READY", runtime.BlockReasonCode);
        ProtocolOutboxRow plan = Assert.Single(await PlanRowsAsync(fixture));
        Assert.Equal(PlanMessageId, plan.MessageId);
        JsonElement sent = Assert.Single(PlanLinesSent(fixture));
        Assert.Equal(1, sent.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(runtime.PlanRevision, sent.GetProperty("payload").GetProperty("planRevision").GetInt64());
        Assert.Equal(
            [("TO_PICKUP", "ACTIVE"), ("TO_DROPOFF", "PLANNED")],
            sent.GetProperty("payload").GetProperty("legs").EnumerateArray()
                .Select(leg => (leg.GetProperty("legType").GetString()!, leg.GetProperty("state").GetString()!))
                .ToArray());
    }

    /// <summary>
    /// 恰好一次：未就绪时发出、车确认之后，会话回到 <c>Ready</c> 的第一轮推进不再发它——
    /// <c>PublishPickupDispatchPlanOnceAsync</c> 按同一个 messageId 认出发件箱里已有那一行，补发只补没确认的行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task TheFirstReadyAdvanceDoesNotSendTheAcknowledgedPlanAgain()
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();
        AdoptingPeer peer = new(fixture.Context, fixture.Clock);
        fixture.Peer.OnMessageSent = line =>
        {
            peer.Receive(line);
            return Task.CompletedTask;
        };

        await DropSessionOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Single(PlanLinesSent(fixture));
        await peer.DeliverBufferedAcksAsync();
        Assert.NotNull(Assert.Single(await PlanRowsAsync(fixture)).AcknowledgedAt);

        await fixture.RestoreSessionReadyAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Single(PlanLinesSent(fixture));
        Assert.Single(await PlanRowsAsync(fixture));
        Assert.Equal(
            [(await fixture.RuntimeAsync()).PlanRevision],
            peer.Adopted.Where(item => item.MessageType == "UpcomingStopPlanSnapshot").Select(item => item.Revision));
    }

    /// <summary>
    /// 重连：车在收到之前断了（没有确认），重连后仍因自己的单未就绪。握手完成之前一行都不发（control-server#259）；
    /// 握手完成之后补发那一行，代次改写成新的一代（<c>ReplayPendingForSessionAsync</c>），发件箱仍然只有一行——不丢、不重复成两行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task AReconnectStillOnTheOwnOrderGetsThePlanReplayedOnlyOnceItsHandshakeIsDone()
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();
        await DropSessionOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Single(PlanLinesSent(fixture));

        // 新的一代正在握手：恢复报告还没到，会话行的原因码是 RECOVERY_REPORT_REQUIRED（GetRecoveryReason 的顺序）。
        await ReconnectMidHandshakeAsync(fixture, generation: 2);
        int linesBefore = fixture.Peer.Lines.Count;
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal(linesBefore, fixture.Peer.Lines.Count);

        await FinishHandshakeStillOnOwnOrderAsync(fixture);
        await fixture.Engine.ExecuteOnceAsync(Token);

        JsonElement[] sent = PlanLinesSent(fixture);
        Assert.Equal([1L, 2L], sent.Select(line => line.GetProperty("sessionGeneration").GetInt64()).ToArray());
        Assert.All(sent, line => Assert.Equal(PlanMessageId, line.GetProperty("messageId").GetString()));
        ProtocolOutboxRow plan = Assert.Single(await PlanRowsAsync(fixture));
        using JsonDocument stored = JsonDocument.Parse(plan.PayloadJson);
        Assert.Equal(2, stored.RootElement.GetProperty("sessionGeneration").GetInt64());
    }

    /// <summary>
    /// 放行只给「未就绪完全由本服务端自己的在途单解释」这一种。每一格拿掉一个条件，都一行不发、发件箱 0 行——
    /// 与修前一样，因为那时的未就绪可能不是自己的单造成的。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    [InlineData("slot-side-reason")]
    [InlineData("evidence-not-unknown")]
    [InlineData("handshake-not-done")]
    [InlineData("operation-recovery")]
    [InlineData("vehicle-faulted")]
    [InlineData("peer-silent")]
    [InlineData("order-not-confirmed")]
    public async Task NoPlanGoesOutWhenTheUnreadinessIsNotExplainedByTheOwnOrder(string variant)
    {
        await using RuntimeFixture fixture = await AcceptedWithOrderConfirmedAsync();
        await DropSessionOnOwnOrderAsync(fixture);
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        switch (variant)
        {
            case "slot-side-reason":
                session.SafetyReasonCodesJson = """["SLOT_LOCK_UNKNOWN","VEHICLE_NOT_READY"]""";
                break;
            case "evidence-not-unknown":
                session.SafetyUnknownPresent = false;
                break;
            case "handshake-not-done":
                session.RecoveryReportId = null;
                session.ReasonCode = "RECOVERY_REPORT_REQUIRED";
                break;
            case "operation-recovery":
                session.ReasonCode = "OPERATION_RECOVERY_REQUIRED";
                break;
            case "vehicle-faulted":
                fixture.Context.VehicleFaultStates.Add(new VehicleFaultStateRow
                {
                    AgvId = fixture.Options.AgvId,
                    Level = VehicleFaultLevel.SuspectedBlocked,
                    FaultGeneration = 1,
                    EvidenceCode = "RIOT_ALARM",
                    EnteredAt = fixture.Clock.GetUtcNow()
                });
                break;
            case "order-not-confirmed":
                // 单还没确认（例如建单结果未知、等下一轮对账）：计划不能指向一段没人派出去的移动。
                OrderIntentRow intent = await fixture.Context.OrderIntents.SingleAsync(
                    row => row.Purpose == "TO_PICKUP", Token);
                intent.Status = "RESULT_UNKNOWN";
                break;
            case "peer-silent":
                fixture.Clock.Advance(SessionLiveness.Timeout + TimeSpan.FromSeconds(1));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }
        await fixture.Context.SaveChangesAsync(Token);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Empty(PlanLinesSent(fixture));
        Assert.Empty(await PlanRowsAsync(fixture));
    }

    /// <summary>
    /// 受理那一轮：建单、确认，但不推进——这正是窗口的起点。前置条件断言在这里，免得后面的红其实是没走到窗口。
    /// </summary>
    private static async Task<RuntimeFixture> AcceptedWithOrderConfirmedAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));
        Assert.Empty(PlanLinesSent(fixture));
        Assert.Empty(await PlanRowsAsync(fixture));
        return fixture;
    }

    /// <summary>
    /// 车载端读到车辆安全接口之后会话行变成的样子（失败现场 <c>SessionRecoveries</c>）：
    /// <c>RecoveryRequired</c> / <c>DEPARTURE_SAFETY_NOT_READY</c>，安全原因只有 <c>VEHICLE_NOT_READY</c>，证据里有未知。
    /// </summary>
    private static async Task DropSessionOnOwnOrderAsync(RuntimeFixture fixture)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
    }

    private static async Task ReconnectMidHandshakeAsync(RuntimeFixture fixture, long generation)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.SessionGeneration = generation;
        session.RecoveryReportId = null;
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "RECOVERY_REPORT_REQUIRED";
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        await fixture.HearFromPeerAsync();
    }

    private static async Task FinishHandshakeStillOnOwnOrderAsync(RuntimeFixture fixture)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(Token);
        session.RecoveryReportId = Guid.NewGuid().ToString("D");
        await fixture.Context.SaveChangesAsync(Token);
        await DropSessionOnOwnOrderAsync(fixture);
    }

    private static Task<ProtocolOutboxRow[]> PlanRowsAsync(RuntimeFixture fixture) =>
        fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
            .ToArrayAsync(Token);

    /// <summary>车收到的每一行计划，按收到的顺序，补发重复的也算——「恰好一次」数的就是这个。</summary>
    private static JsonElement[] PlanLinesSent(RuntimeFixture fixture) =>
        [.. fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)).RootElement.Clone())
            .Where(root => root.GetProperty("messageType").GetString() == "UpcomingStopPlanSnapshot")];
}
