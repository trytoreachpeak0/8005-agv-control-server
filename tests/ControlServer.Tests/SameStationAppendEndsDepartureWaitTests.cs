using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 车装完停在取货站等离站时，同一站追加了一条需求：不等原停靠的倒计时，这一轮就走原地的离站核验与「到站」，新停靠到站时拿到
/// 完整等待时长（control-server#646，用户 2026-10-12 定「提前结束等待」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前。</b>追加不并入当前停靠（REQ-0196），同站的新需求成为当前停靠之后的新停靠；车在原停靠一直等到原期限才发离站核验，
/// 这期间新需求不在车上的清单里，录不进去（cs#643 的探针 <c>ProbeAnAppendAtTheStationTheVehicleStandsAt</c>，提交 75f2593e4）。
/// </para>
/// <para>
/// <b>每条用例都显式打开等待。</b>夹具默认等待为零，零等于装完当轮离站，在那个设置下提前不提前看不出来。
/// 不放行的几条都把钟拨到原期限之前一点再跑：只跑一轮、钟不走，「没发离站核验」对修前修后都成立，什么也说明不了。
/// </para>
/// </remarks>
public sealed class SameStationAppendEndsDepartureWaitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    /// <summary>装完到追加之间钟走过的时间：比期限短得多，放行不是因为原期限到了。</summary>
    private static readonly TimeSpan BeforeAppend = TimeSpan.FromSeconds(20);

    private const string OtherPickupArea = "N1-2";
    private const int OtherPickupStationRiotId = 13;
    private const string PendingDemandId = "10000000-0000-4000-8000-000000000646";
    private const string PendingSublot = "SUBLOT-646";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 同站追加：下一轮就发离站核验（原期限还远）；原地「到站」后新停靠从到站那一轮起算完整时长，车上那一版清单列着新需求、带着这个期限。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendSendsTheVehicleOnAtOnceAndTheNewStopGetsTheFullWait()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        DateTimeOffset originalDeadline = loadedAt + Wait;

        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);
        await RoundAsync(fixture);

        JourneyRuntimeRow leaving = await JourneyOfAsync(fixture, FirstDemandId);
        Assert.True(fixture.Clock.GetUtcNow() < originalDeadline - TimeSpan.FromSeconds(30));
        Assert.True(
            await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId),
            $"The stop is done and the next one is at the same station, yet the vehicle was not asked to leave. " +
            $"stage={leaving.Stage} phase={leaving.LoadingPhaseState} block={leaving.BlockReasonCode}");
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, leaving.Stage);

        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        JourneyRuntimeRow arrived = await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        DateTimeOffset arrivedAt = fixture.Clock.GetUtcNow();
        JourneyStopRow appended = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.NotEqual(firstPickup.StopId, appended.StopId);
        Assert.Equal(firstPickup.StationId, appended.StationId);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, arrived.Stage);
        Assert.Equal(arrivedAt, arrived.StationDepartureWaitStartedAt);

        (string[] demands, DateTimeOffset? deadline) = await LatestWorklistAsync(fixture);
        Assert.Equal([SecondDemandId], demands);
        Assert.Equal(arrivedAt + Wait, deadline);
    }

    /// <summary>
    /// 追加在另一站：照旧等满原期限才离站（期限之前一轮不发离站核验，到期那一轮发）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task AnAppendAtAnotherStationStillWaitsOutTheStationDeadline()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);

        await AppendAtAsync(fixture, OtherPickupArea, OtherPickupStationRiotId);
        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait);

        await RunAtAsync(fixture, loadedAt + Wait);
        Assert.True(await DepartureCheckOfStopSentAsync(fixture, firstPickup.StopId), "The deadline passed and the vehicle was not sent on.");
    }

    /// <summary>
    /// 同站追加，但当前停靠上还有一条待装：不提前放行（当前停靠的活没做完）。
    /// </summary>
    /// <remarks>
    /// 这个状态正常流程走不到——还有待装时旅程在等录入，不在离站等待里——所以直接在库里给当前停靠挂上一条待装，钉住的是条件里
    /// 「已无待装」那一项本身。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendDoesNotEndTheWaitWhileTheCurrentStopHasALoadOutstanding()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);
        fixture.BoxCounts.Set(PendingSublot, 4);
        await Batch7MultiDemandAdvanceTests.AddDemandToJourneyAsync(
            fixture, await JourneyOfAsync(fixture, FirstDemandId), PendingDemandId, PendingSublot, slotBlock: 2);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(
            firstPickup.StopId,
            (await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
                .SingleAsync(row => row.DemandId == PendingDemandId, Token)).PickupStopId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);

        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait);
    }

    /// <summary>
    /// 同站追加，但本旅程有一条纠错开着：纠错闸照样挡住，不提前放行，阻断原因是纠错进行中。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendDoesNotEndTheWaitWhileACorrectionIsOpen()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        await OpenCorrectionOnTheFirstDemandAsync(fixture);
        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);

        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait);
        Assert.Equal(
            JourneyRuntimeEngine.LoadCorrectionInProgressReason, (await JourneyOfAsync(fixture, FirstDemandId)).BlockReasonCode);
    }

    /// <summary>
    /// 同站追加，但车被仓门扣着（REQ-0364）：不提前放行，原期限过了也不发离站核验。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationAppendDoesNotSendAVehicleHeldForItsSlotDoorOn()
    {
        await using RuntimeFixture fixture = await Batch7CargoHoldingTests.HoldingFixtureAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        DateTimeOffset loadedAt = await Batch7CargoHoldingTests.LoadTheFirstDemandAsync(fixture);
        JourneyStopRow firstPickup = await CurrentStopAsync(fixture, FirstDemandId);
        fixture.Context.SlotDoorHolds.Add(new SlotDoorHoldRow
        {
            HoldId = "70000000-0000-4000-8000-000000000646",
            AgvId = fixture.Options.AgvId,
            DemandId = FirstDemandId,
            SlotsJson = "[1]",
            HeldAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await AppendAtAsync(fixture, firstPickup.StationId, firstPickup.StationRiotId);

        await AssertHeldUntilAsync(fixture, firstPickup.StopId, loadedAt + Wait + TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 下一个停靠同站但是卸货（规划器落下的先装后卸）：不在本票的条件里，照旧等满期限。那个顺序归 control-server#647。
    /// </summary>
    /// <remarks>
    /// 计划形状 P1(N1-1，第一条装) → P2(N1-2，第二条装) → U1(N1-2，第一条卸) → U2(关卡，第二条卸)，直接在库里排出来；车停在 P2 装完时，
    /// 下一个停靠就是同站的卸货停靠。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    public async Task ASameStationUnloadNextDoesNotEndThePickupWait()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = Wait;
        fixture.Catalog.Set(
            fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)),
            fixture.Demand(SecondDemandId, SecondSublot, Now.AddMinutes(-9), area: OtherPickupArea));
        fixture.BoxCounts.Set(FirstSublot, 7);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await TickAndRunAsync(fixture);
        await Batch7ThreeStopJourneyTests.AppendSecondDemandAsync(fixture);
        await UnloadTheFirstDemandRightAfterTheSecondPickupAsync(fixture);

        await ArriveAtPickupAsync(fixture, FirstDemandId);
        await EnterSublotAsync(fixture, FirstDemandId, FirstSublot, FirstSubmissionId);
        await SettleLoadAsync(fixture, FirstDemandId);
        await RunAtAsync(fixture, (await JourneyOfAsync(fixture, FirstDemandId)).StationDepartureWaitStartedAt!.Value + Wait);
        await AnswerDepartureSafetyAsync(fixture, FirstDemandId, FirstSafetyResultId);
        await ArriveAtCurrentStopAsync(fixture, SecondDemandId, "TO_GATE");
        JourneyStopRow secondPickup = await CurrentStopAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyStopRoles.Pickup, secondPickup.StopRole);
        await EnterSublotAsync(fixture, SecondDemandId, SecondSublot, SecondSubmissionId);
        JourneyRuntimeRow settled = await SettleLoadAsync(fixture, SecondDemandId);
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, settled.Stage);

        await AssertHeldUntilAsync(fixture, secondPickup.StopId, settled.StationDepartureWaitStartedAt!.Value + Wait);
    }

    /// <summary>车停在原停靠等离站，钟走 <see cref="BeforeAppend"/> 后在给定的站追加第二条需求。</summary>
    private static async Task AppendAtAsync(RuntimeFixture fixture, string area, int stationRiotId)
    {
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, (await JourneyOfAsync(fixture, FirstDemandId)).Stage);
        fixture.Clock.Advance(BeforeAppend);
        fixture.BoxCounts.Set(SecondSublot, 7);
        await Batch7ThreeStopJourneyTests.AppendDemandAsync(fixture, SecondDemandId, SecondSublot, area, stationRiotId);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 追加之后连跑两轮、再在期限之前两秒跑一轮：这个停靠的离站核验一直没发，旅程还停在离站等待。
    /// </summary>
    private static async Task AssertHeldUntilAsync(RuntimeFixture fixture, string stopId, DateTimeOffset deadline)
    {
        await RoundAsync(fixture);
        await RoundAsync(fixture);
        await RunAtAsync(fixture, deadline - TimeSpan.FromSeconds(2));
        JourneyRuntimeRow held = await fixture.Context.JourneyRuntimes.AsNoTracking().SingleAsync(Token);
        Assert.False(
            await DepartureCheckOfStopSentAsync(fixture, stopId),
            $"The vehicle was asked to leave before {deadline:O}. stage={held.Stage} block={held.BlockReasonCode}");
        Assert.Equal(JourneyRuntimeStage.AwaitingStationDeparture, held.Stage);
    }

    private static async Task RoundAsync(RuntimeFixture fixture)
    {
        await fixture.HearFromPeerAsync();
        await TickAndRunAsync(fixture);
    }

    private static async Task RunAtAsync(RuntimeFixture fixture, DateTimeOffset at)
    {
        fixture.Clock.Advance(at - fixture.Clock.GetUtcNow());
        await fixture.HearFromPeerAsync();
        await fixture.Engine.ExecuteOnceAsync(Token);
    }

    /// <summary>这个停靠的离站核验有没有写进发件箱。按停靠认，不按条数：后面停靠的核验不算。</summary>
    private static async Task<bool> DepartureCheckOfStopSentAsync(RuntimeFixture fixture, string stopId)
    {
        JourneyStopRow stop = await fixture.Context.Set<JourneyStopRow>().AsNoTracking()
            .SingleAsync(row => row.StopId == stopId, Token);
        return stop.DepartureSafetyCheckMessageId is { } messageId &&
               await fixture.Context.ProtocolOutbox.AsNoTracking().AnyAsync(row => row.MessageId == messageId, Token);
    }

    /// <summary>第一条需求上开着一条本车、本旅程的纠错（已发命令、未闭环）。</summary>
    private static async Task OpenCorrectionOnTheFirstDemandAsync(RuntimeFixture fixture)
    {
        JourneyDemandRow membership = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token);
        fixture.Context.RecoveryWorkflows.Add(new RecoveryWorkflowRow
        {
            WorkflowId = "70000000-0000-4000-8000-000000000647",
            WorkflowType = "LOAD_CORRECTION",
            AgvId = fixture.Options.AgvId,
            DemandId = FirstDemandId,
            SlotOperationAttemptId = membership.LoadSlotOperationAttemptId,
            SlotsJson = membership.TargetSlotsJson,
            State = RecoveryWorkflowState.AwaitingResult,
            RequestMessageId = "70000000-0000-4000-8000-000000000648",
            RequestContentHash = new string('c', 64),
            CreatedAt = fixture.Clock.GetUtcNow(),
            UpdatedAt = fixture.Clock.GetUtcNow()
        });
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 把 P1, P2, U 排成 P1, P2, U1, U：第一条改在第二个取货站卸，卸货停靠新开一个、紧跟在 P2 之后，第二条仍在原卸货停靠卸。
    /// </summary>
    private static async Task UnloadTheFirstDemandRightAfterTheSecondPickupAsync(RuntimeFixture fixture)
    {
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync(FirstDemandId);
        JourneyDemandRow first = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == FirstDemandId, Token);
        JourneyDemandRow second = await fixture.Context.Set<JourneyDemandRow>()
            .SingleAsync(row => row.DemandId == SecondDemandId, Token);
        JourneyStopRow secondPickup = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == second.PickupStopId, Token);
        JourneyStopRow unload = await fixture.Context.Set<JourneyStopRow>()
            .SingleAsync(row => row.StopId == second.UnloadStopId, Token);
        Assert.Equal(first.UnloadStopId, unload.StopId);
        Assert.Equal(secondPickup.Sequence + 1, unload.Sequence);

        string unloadHalfId = $"{runtime.JourneyId}|MIXED-UNLOAD-AFTER";
        fixture.Context.Set<JourneyStopRow>().Add(new JourneyStopRow
        {
            StopId = unloadHalfId,
            JourneyId = runtime.JourneyId,
            Sequence = unload.Sequence,
            StopRole = JourneyStopRoles.Unload,
            StationId = secondPickup.StationId,
            StationRiotId = secondPickup.StationRiotId,
            DispatchZone = secondPickup.DispatchZone,
            OperationSessionId = JourneyPlanBuilder.StableGuid(unloadHalfId, "session"),
            MovementLegId = JourneyPlanBuilder.StableGuid(unloadHalfId, "leg"),
            UpperId = $"W2G-{unloadHalfId}",
            VehicleBusinessMessageId = JourneyPlanBuilder.StableGuid(unloadHalfId, "vehicle-state"),
            WorklistMessageId = JourneyPlanBuilder.StableGuid(unloadHalfId, "worklist"),
            PlanMessageId = JourneyPlanBuilder.StableGuid(unloadHalfId, "plan"),
            Status = JourneyStopStatuses.Pending,
            CreatedAt = runtime.CreatedAt
        });
        unload.Sequence += 1;
        first.UnloadStopId = unloadHalfId;
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>修订号最高的那一版清单（此刻排给车的那一版）列着哪些需求、带的期限。</summary>
    private static async Task<(string[] Demands, DateTimeOffset? Deadline)> LatestWorklistAsync(RuntimeFixture fixture)
    {
        string[] payloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "CurrentStopWorklistSnapshot")
            .Select(row => row.PayloadJson)
            .ToArrayAsync(Token);
        long best = long.MinValue;
        string[] demands = [];
        DateTimeOffset? deadline = null;
        foreach (string json in payloads)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            long revision = payload.GetProperty("worklistRevision").GetInt64();
            if (revision <= best) continue;
            best = revision;
            demands = [.. payload.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("demandId").GetString()!)];
            JsonElement at = payload.GetProperty("stationDepartureDeadlineAt");
            deadline = at.ValueKind == JsonValueKind.Null ? null : at.GetDateTimeOffset();
        }
        return (demands, deadline);
    }
}
