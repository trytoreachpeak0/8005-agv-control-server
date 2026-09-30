using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 下发给车载端的 <c>VehicleBusinessStateSnapshot.batteryState</c> 按实况投影（批次9-05，control-server#403）：随修订号冻结、同一个消息 id
/// 重发逐字相同；搬运途中越过强制充电线，这一趟后续停靠照常装卸（REQ-0281），做完下一轮该车进入强制充电。
/// </summary>
/// <remarks>
/// 夹具的测试策略两道线都是 40（版本 1），合成 RIoT 报 80。车载端那一半（录入门不再看 <c>batteryState</c>）在 onboard-hmi#220。
/// </remarks>
public sealed class BatteryStateProjectionTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 同一修订号重发两次、中间电量变了，两次载荷逐字相同：到站那一段被断线打断（车辆业务状态与清单已确认、计划没送到），重连之前车的电量
    /// 从 80 掉到 10。重跑这一段时到站那张快照按同一个消息 id 再发，<c>batteryState</c> 仍是车已确认的 <c>SUFFICIENT</c>——不在重发时现读电量。
    /// 现读的写法在这里被重放校验拒掉（同一 id 内容变了），这一段每轮失败、录入请求永远发不出去。
    /// </summary>
    [Fact]
    public async Task AReSentArrivalSnapshotCarriesTheBatteryStateItWasFirstSentWithEvenAfterTheBatteryFell()
    {
        (RuntimeFixture fixture, ArrivalPublishInterruptedThenReconnectedTests.ConnectionCut _) =
            await ArrivalPublishInterruptedThenReconnectedTests.ArrivalPublishCutAfterTheWorklistAsync();
        await using RuntimeFixture disposing = fixture;
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        string acknowledged = await VehicleStatePayloadTextAsync(fixture, before.VehicleBusinessMessageId);
        Assert.Equal(BatteryStates.Sufficient, JsonDocument.Parse(acknowledged).RootElement.GetProperty("batteryState").GetString());

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
        await ArrivalPublishInterruptedThenReconnectedTests.ReconnectAtGenerationAsync(fixture, generation: 2);
        await fixture.Engine.ExecuteOnceAsync(Token);

        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, after.Stage);
        Assert.Null(after.BlockReasonCode);
        Assert.Equal(acknowledged, await VehicleStatePayloadTextAsync(fixture, before.VehicleBusinessMessageId));
        Assert.Equal(BatteryStates.Sufficient, after.PublishedBatteryState);
        Assert.Contains("SublotEntryRequested", await fixture.OutboxTypesAsync());
    }

    /// <summary>
    /// 搬运途中越过入口线，这一趟照常做完（REQ-0281）：派车时 80（<c>SUFFICIENT</c>，版本 1 记在旅程上），派出后掉到 10。取货站到站快照换成
    /// 下一版、投出 <c>MANDATORY_CHARGE</c>，服务端照常发录入请求、收下录入、下装货命令；卸货站同样，旅程完成，收尾快照带着同一个值。
    /// 完成之后的下一轮，这辆车不再接新搬运，积压的原因码是强制充电。名册里登记了一台此刻分不到的桩（批次9-06，control-server#404）：
    /// 名册为空时它会被置人工充电等待，挡住搬运的就换成等待那条判据了。
    /// </summary>
    [Fact]
    public async Task AVehicleThatFallsBelowItsLineOnTheWayFinishesItsJourneyAndThenTakesNoNewWork()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await ChargingTestKit.WriteRosterWithAChargerNobodyIsSentToAsync(fixture.Context, fixture.Options.MapId, Now);
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow dispatched = await fixture.RuntimeAsync();
        Assert.Equal((1L, BatteryStates.Sufficient), (dispatched.ChargingPolicyVersion, dispatched.PublishedBatteryState));

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
        JourneyRuntimeRow completed = await fixture.RunToCompletionAsync();

        Assert.Equal(JourneyRuntimeStage.Completed, completed.Stage);
        Assert.Equal(BatteryStates.MandatoryCharge, completed.PublishedBatteryState);
        string[] states = await VehicleStatesInRevisionOrderAsync(fixture);
        Assert.True(states.Length >= 3, string.Join(",", states));
        Assert.All(states, state => Assert.Equal(BatteryStates.MandatoryCharge, state));
        // 取货站照常出录入请求、照常装货，卸货站照常卸：两条槽位操作都有、都已终结。
        Assert.NotNull(await fixture.OperationAsync(SlotOperationType.Load));
        Assert.Contains("SublotEntryRequested", await fixture.OutboxTypesAsync());
        Assert.NotNull(await fixture.OperationAsync(SlotOperationType.Unload));

        const string next = "10000000-0000-4000-8000-000000000002";
        fixture.Catalog.Set(fixture.Demand(next, "SUBLOT-002", Now.AddMinutes(-5)));
        fixture.BoxCounts.Set("SUBLOT-002", 4);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(DispatchReasonCodes.MandatoryChargeRequired, (await fixture.BacklogAsync(next)).ReasonCode);
        Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 本票上线前派出的旅程（旅程行上两列都为空）照旧发 <c>SUFFICIENT</c>：它们排给车的快照里就是这个值，也没有冻结的策略版本可据以投影。
    /// </summary>
    [Fact]
    public async Task AJourneyDispatchedBeforeTheProjectionKeepsSendingTheOldConstant()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(Token);
        await fixture.Context.JourneyRuntimes.ExecuteUpdateAsync(
            set => set.SetProperty(row => row.ChargingPolicyVersion, (long?)null).SetProperty(row => row.PublishedBatteryState, (string?)null),
            Token);
        await fixture.RecreateEngineAsync();

        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { BatteryPercent = 10 };
        JourneyRuntimeRow waiting = await fixture.AdvanceToSublotWaitAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, waiting.Stage);
        Assert.Null(waiting.PublishedBatteryState);
        Assert.Equal([BatteryStates.Sufficient], await VehicleStatesInRevisionOrderAsync(fixture));
    }

    private static async Task<string> VehicleStatePayloadTextAsync(RuntimeFixture fixture, string messageId)
    {
        JsonElement payload = await fixture.OutboxPayloadAsync(messageId);
        // observedAt is the envelope's frozen sentAt; everything else is the projection.
        return payload.GetRawText();
    }

    private static async Task<string[]> VehicleStatesInRevisionOrderAsync(RuntimeFixture fixture)
    {
        ProtocolOutboxRow[] rows = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "VehicleBusinessStateSnapshot")
            .ToArrayAsync(Token);
        return
        [
            .. rows
                .Select(row => JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("payload"))
                .OrderBy(payload => payload.GetProperty("vehicleBusinessStateRevision").GetInt64())
                .Select(payload => payload.GetProperty("batteryState").GetString()!)
        ];
    }
}
