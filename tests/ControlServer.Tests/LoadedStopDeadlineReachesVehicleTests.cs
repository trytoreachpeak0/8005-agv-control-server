using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 取货停靠装完之后、车还停着等离站时，服务端挪过的离站期限要送到车上（control-server#633）。
/// </summary>
/// <remarks>
/// <para>
/// <b>两处挪期限。</b>最后一批落定时起点重置为此刻（ADR-cross-0054 的纠正时间），纠错闭环时起点挪到闭环那一刻（ADR-cross-0055）。
/// 修之前两处都不发清单：本站已无待做项，<c>PublishStopWorklistAsync</c> 什么也不发，车上留着装最后一条之前那一版，期限是到站时算的。
/// 2026-10-11 cs#566 现场：车上期限约 21:21:43 已过、显示「已到期」，服务端真实期限约 21:30:36。
/// </para>
/// <para>
/// <b>钟要真的走。</b>装货结果回来之前拨 <see cref="Loading"/>，并断言落定后的期限确实与到站时的不同——钟不走时两者相等，
/// 「车上与服务端一致」恒真。车用 <see cref="AdoptingPeer"/>，同号不同内容按车载端的规则拒收。
/// </para>
/// </remarks>
public sealed class LoadedStopDeadlineReachesVehicleTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>录入到装货结果回来之间钟走过的时间。比期限短，这一站不会在装货途中到期。</summary>
    private static readonly TimeSpan Loading = TimeSpan.FromSeconds(20);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 最后一批落定：重置出来的期限随一张空清单送到车上——没有项、原因 <c>COMPLETED</c>、作业会话仍是本站那一个；之后几轮不再重发。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheLastLoadsRestartedDeadlineReachesTheVehicle()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        DateTimeOffset atArrival = await LoadTheOnlyDemandAsync(fixture, vehicle);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        AssertTheVehicleHoldsTheServersDeadline(runtime, fixture, vehicle, atArrival);
        JsonElement held = vehicle.HeldPayload("CurrentStopWorklistSnapshot")!.Value;
        Assert.Equal(0, held.GetProperty("items").GetArrayLength());
        Assert.Equal("COMPLETED", held.GetProperty("stopEndedReason").GetString());
        Assert.Equal(runtime.OperationSessionId, held.GetProperty("operationSessionId").GetString());

        int worklistsSent = SentLines(fixture, "CurrentStopWorklistSnapshot").Length;
        for (int round = 0; round < 2; round++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.HearFromPeerAsync();
            await fixture.Engine.ExecuteOnceAsync(Token);
            await vehicle.DeliverBufferedAcksAsync();
        }
        Assert.Equal(worklistsSent, SentLines(fixture, "CurrentStopWorklistSnapshot").Length);
    }

    /// <summary>
    /// 纠错闭环把期限往后挪：新期限再升一版送到车上，上一张空清单退役。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AClosedCorrectionsDeadlineReachesTheVehicle()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        await LoadTheOnlyDemandAsync(fixture, vehicle);
        DateTimeOffset afterLoad = VehicleDeadline(vehicle);
        long emptyRevision = AdoptedWorklists(vehicle).Max();

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        RecoveryWorkflowRow correction = new()
        {
            WorkflowId = "70000000-0000-4000-8000-000000000001",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = runtime.AgvId,
            DemandId = runtime.DemandId,
            SlotOperationAttemptId = load.SlotOperationAttemptId,
            SlotsJson = load.TargetSlotsJson,
            State = RecoveryWorkflowState.AwaitingResult,
            RequestMessageId = "70000000-0000-4000-8000-000000000002",
            RequestContentHash = new string('c', 64),
            CreatedAt = fixture.Clock.GetUtcNow(),
            UpdatedAt = fixture.Clock.GetUtcNow()
        };
        fixture.Context.RecoveryWorkflows.Add(correction);
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        Assert.Equal("LOAD_CORRECTION_IN_PROGRESS", (await fixture.RuntimeAsync()).BlockReasonCode);

        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        correction.State = RecoveryWorkflowState.Reconciled;
        correction.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(Token);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, runtime.Stage);
        Assert.Equal(correction.UpdatedAt + Wait, ServerDeadline(runtime, fixture));
        AssertTheVehicleHoldsTheServersDeadline(runtime, fixture, vehicle, afterLoad);
        Assert.Equal(emptyRevision + 1, AdoptedWorklists(vehicle).Max());
    }

    /// <summary>
    /// 期限过了照常离站，卸货站那一版清单的号在本停靠多占的那一版之上，车照常采纳——号的顺延没有让车拒收任何一张。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheVehicleDepartsAndAdoptsTheGateWorklistAfterTheRefreshedOne()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        AdoptingPeer vehicle = Attach(fixture);
        await LoadTheOnlyDemandAsync(fixture, vehicle);
        long pickupLast = AdoptedWorklists(vehicle).Max();

        fixture.Clock.Advance(Wait);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                checkPurpose = "DEPARTURE",
                outcome = "SAFE",
                observedAt = fixture.Clock.GetUtcNow(),
                safetyStateVersion = 7,
                validUntil = fixture.Clock.GetUtcNow().AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = false,
                    reasonCodes = Array.Empty<string>()
                }
            },
            runtime.PreDepartureSafetyCheckMessageId);
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Riot.SetSuccessfulArrival("TO_GATE", TaskTypeStationRuntimeSeed.GateStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = TaskTypeStationRuntimeSeed.GateStationRiotId };
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingUnloadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Empty(vehicle.Conflicts);
        Assert.Empty(vehicle.Regressions);
        Assert.True(AdoptedWorklists(vehicle).Max() > pickupLast);
    }

    /// <summary>
    /// 离站等待关着：车上与服务端的期限都是空，一版也不多发——修订号序列与之前逐字相同（WirePin 那几份钉的正是这一种）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task WithTheWaitOffNoWorklistIsAdded()
    {
        await using RuntimeFixture fixture = await DeadlineFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.Zero;
        AdoptingPeer vehicle = Attach(fixture);
        await fixture.AdvanceToLoadResultAsync();
        await vehicle.DeliverBufferedAcksAsync();
        int worklistsBefore = SentLines(fixture, "CurrentStopWorklistSnapshot").Length;

        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(worklistsBefore, SentLines(fixture, "CurrentStopWorklistSnapshot").Length);
        Assert.Equal(0, (await PickupStopAsync(fixture)).WorklistRefills);
    }

    /// <summary>到站、录入、装货命令已发；钟走 <see cref="Loading"/> 后装货安全闭环、引擎跑一轮。返回车在到站时拿到的期限。</summary>
    private static async Task<DateTimeOffset> LoadTheOnlyDemandAsync(RuntimeFixture fixture, AdoptingPeer vehicle)
    {
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.AdvanceToLoadResultAsync()).Stage);
        await vehicle.DeliverBufferedAcksAsync();
        DateTimeOffset atArrival = VehicleDeadline(vehicle);

        fixture.Clock.Advance(Loading);
        StationOperationRow load = await fixture.OperationAsync(SlotOperationType.Load);
        await fixture.ApplySafeResultAsync(load, SlotOperationType.Load, SlotBusinessState.Occupied);
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
        await vehicle.DeliverBufferedAcksAsync();
        return atArrival;
    }

    private static async Task<RuntimeFixture> DeadlineFixtureAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        return fixture;
    }

    /// <summary>车：收到的快照按车载端的规则采纳，确认由用例决定何时送回。</summary>
    private static AdoptingPeer Attach(RuntimeFixture fixture)
    {
        AdoptingPeer vehicle = new(fixture.Context, fixture.Clock);
        fixture.Peer.OnMessageSent = line =>
        {
            vehicle.Receive(line);
            return Task.CompletedTask;
        };
        return vehicle;
    }

    /// <summary>
    /// 车手上那一版清单的期限等于服务端此刻判定用的期限，且与 <paramref name="before"/> 不同（钟确实走了）；车没有拒收任何一张。
    /// </summary>
    private static void AssertTheVehicleHoldsTheServersDeadline(
        JourneyRuntimeRow runtime,
        RuntimeFixture fixture,
        AdoptingPeer vehicle,
        DateTimeOffset before)
    {
        DateTimeOffset server = ServerDeadline(runtime, fixture);
        DateTimeOffset held = VehicleDeadline(vehicle);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"before {before:O}; server now {server:O}; vehicle holds {held:O}; " +
            $"vehicle refused {string.Join(",", vehicle.Conflicts)}, regressions {vehicle.Regressions.Count}");
        Assert.NotEqual(before, server);
        Assert.Empty(vehicle.Conflicts);
        Assert.Empty(vehicle.Regressions);
        Assert.Equal(server, held);
    }

    private static async Task<JourneyStopRow> PickupStopAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        return await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.JourneyId == runtime.JourneyId && row.StopRole == JourneyStopRoles.Pickup, Token);
    }

    private static long[] AdoptedWorklists(AdoptingPeer vehicle) =>
        [.. vehicle.Adopted.Where(item => item.MessageType == "CurrentStopWorklistSnapshot").Select(item => item.Revision)];

    private static DateTimeOffset ServerDeadline(JourneyRuntimeRow runtime, RuntimeFixture fixture) =>
        JourneyRuntimeEngine.StationDepartureDeadline(runtime, fixture.Options.StationDepartureWaitTimeout)
        ?? throw new InvalidOperationException("The server holds no station departure deadline.");

    private static DateTimeOffset VehicleDeadline(AdoptingPeer vehicle) =>
        (vehicle.HeldPayload("CurrentStopWorklistSnapshot")
         ?? throw new InvalidOperationException("The vehicle holds no worklist."))
        .GetProperty("stationDepartureDeadlineAt").GetDateTimeOffset();

    /// <summary>车收到的这一类报文，按发出的顺序。</summary>
    private static JsonElement[] SentLines(RuntimeFixture fixture, string messageType) =>
        [.. fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(Encoding.UTF8.GetString(line)).RootElement.Clone())
            .Where(root => root.GetProperty("messageType").GetString() == messageType)];
}
