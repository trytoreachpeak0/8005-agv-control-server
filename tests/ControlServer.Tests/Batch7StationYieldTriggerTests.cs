using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 让站的触发写在承诺方的事务里（批次7-08，control-server#213；REQ-0355）：谁会被触发、谁不会，以及触发与承诺同生共死。
/// </summary>
/// <remarks>
/// <para>
/// <b>「别的车」是确定性造出来的，不靠时序。</b>每条用例用真实受理路径（<see cref="WireToGateStore"/>）种两三趟旅程，
/// 各属一辆车，再把其中一趟的阶段与装货阶段直接写成「停在站上持货等单」。被让的车怎么走到等单，是
/// <c>Batch7CargoHoldingTests</c> 的事；这里只关心承诺那一刻的判定。
/// </para>
/// <para>
/// <b>判据断两列都写了、写的是谁、写在什么时刻</b>，不只断「有值」：<c>YieldTriggeredByVehicleKey</c> 空着等于白建一列
/// （票面「来源」一节），而时刻不早于受理是 L2 场景要比的那个量。
/// </para>
/// </remarks>
public sealed class Batch7StationYieldTriggerTests
{
    private const string HolderAgv = "AGV-HOLDER";
    private const string HolderKey = "KEY-HOLDER";
    private const string ComerAgv = "AGV-COMER";
    private const string ComerKey = "KEY-COMER";
    private const string ThirdAgv = "AGV-THIRD";
    private const string ThirdKey = "KEY-THIRD";
    private const string HolderDemand = "30000000-0000-4000-8000-000000000001";
    private const string ComerDemand = "30000000-0000-4000-8000-000000000002";
    private const string ThirdDemand = "30000000-0000-4000-8000-000000000003";
    private const string AppendedDemand = "30000000-0000-4000-8000-000000000004";

    /// <summary><see cref="Batch7JourneyFixture.Plan"/> 的取货站。</summary>
    private const int HoldingStation = 101;

    private const int ElsewhereStation = 103;

    /// <summary>
    /// 别的车受理、下一停靠就是等单车所在的站：同一次提交里等单车被标记，记下的是受理的那一刻与那辆车。
    /// 持货等单的两个状态、车站在停靠上的四个阶段逐一过一遍——「站在停靠上」包括离站安全核验已发出，那时还没离开。
    /// </summary>
    [Theory]
    [MemberData(nameof(HoldingCases))]
    public async Task AVehicleAcceptedForTheStationWhereAnotherHoldsMakesItYield(JourneyRuntimeStage stage, string state)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, stage, state);

        DateTimeOffset acceptedAt = Batch7JourneyFixture.Now.AddMinutes(3);
        await AcceptAsync(fixture, ComerDemand, ComerAgv, ComerKey, HoldingStation, acceptedAt);

        JourneyRuntimeRow holder = await RowAsync(fixture, HolderDemand);
        Assert.Equal(acceptedAt, holder.YieldTriggeredAt);
        Assert.Equal(ComerKey, holder.YieldTriggeredByVehicleKey);
    }

    public static TheoryData<JourneyRuntimeStage, string> HoldingCases()
    {
        TheoryData<JourneyRuntimeStage, string> cases = [];
        foreach (string state in new[] { LoadingPhaseStates.CargoHoldingWait, LoadingPhaseStates.VehicleFull })
        {
            foreach (JourneyRuntimeStage stage in new[]
                     {
                         JourneyRuntimeStage.AwaitingSublot, JourneyRuntimeStage.AwaitingLoadResult,
                         JourneyRuntimeStage.AwaitingStationDeparture, JourneyRuntimeStage.AwaitingDepartureSafety,
                     })
            {
                cases.Add(stage, state);
            }
        }
        return cases;
    }

    /// <summary>
    /// 不触发的四种：别的车去的是另一个站；等在那里的车其实还在装（<c>LOADING</c>）；它还在去那个站的路上
    /// （满了可以发生在路上，满是按预留算的）；它被阻断了，说不出它在哪。
    /// </summary>
    /// <remarks>
    /// 每一格只有一处与上面那条正例不同，所以它不触发只能是因为那一处——上面那条同时断了正向，两条合起来才证明判据
    /// 看的是这几样。
    /// </remarks>
    [Theory]
    [InlineData("another station", JourneyRuntimeStage.AwaitingStationDeparture, LoadingPhaseStates.CargoHoldingWait, ElsewhereStation)]
    [InlineData("still loading", JourneyRuntimeStage.AwaitingSublot, LoadingPhaseStates.Loading, HoldingStation)]
    [InlineData("still on its way", JourneyRuntimeStage.AwaitingPickupArrival, LoadingPhaseStates.VehicleFull, HoldingStation)]
    [InlineData("blocked", JourneyRuntimeStage.Blocked, LoadingPhaseStates.CargoHoldingWait, HoldingStation)]
    public async Task NothingIsTriggeredUnlessAVehicleHoldsAtThatVeryStation(
        string why, JourneyRuntimeStage stage, string state, int comerStation)
    {
        _ = why;
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, stage, state);

        await AcceptAsync(fixture, ComerDemand, ComerAgv, ComerKey, comerStation, Batch7JourneyFixture.Now.AddMinutes(3));

        JourneyRuntimeRow holder = await RowAsync(fixture, HolderDemand);
        Assert.Null(holder.YieldTriggeredAt);
        Assert.Null(holder.YieldTriggeredByVehicleKey);
    }

    /// <summary>
    /// 受理在提交那一刻失败：别的车没受理成，等单车也没被标记——两样要么都在、要么都不在（票面「崩溃点」）。
    /// 读用另一个上下文，看的是库；同一个上下文里的变更跟踪器也还原了，否则下一次保存会把半个触发带进去。
    /// </summary>
    /// <remarks>
    /// <b>只让带着受理行的那一次保存失败</b>，别的保存照常。让每一次保存都失败的话，一个在受理之前单独把触发存掉的实现
    /// 也过得了——它那一次单独的保存同样会失败，库里同样什么都没有。
    /// </remarks>
    [Fact]
    public async Task AnAcceptanceThatFailsAtTheCommitLeavesNoTriggerBehind()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, JourneyRuntimeStage.AwaitingStationDeparture, LoadingPhaseStates.CargoHoldingWait);

        ControlServerDbContext dying = fixture.NewContext();
        JourneyRuntimeRow trackedHolder = await dying.JourneyRuntimes
            .SingleAsync(row => row.DemandId == HolderDemand, TestContext.Current.CancellationToken);
        FailTheSaveThatCarriesANewDemand(dying, "The acceptance could not be committed.");
        await Assert.ThrowsAsync<HttpRequestException>(() => AcceptAsync(
            dying, ComerDemand, ComerAgv, ComerKey, HoldingStation, Batch7JourneyFixture.Now.AddMinutes(3)));

        Assert.Null(trackedHolder.YieldTriggeredAt);
        Assert.Equal(EntityState.Unchanged, dying.Entry(trackedHolder).State);
        JourneyRuntimeRow holder = await RowAsync(fixture, HolderDemand);
        Assert.Null(holder.YieldTriggeredAt);
        Assert.Null(holder.YieldTriggeredByVehicleKey);
        Assert.False(await fixture.NewContext().AcceptedDemands
            .AnyAsync(row => row.DemandId == ComerDemand, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 第一个让它让站的是谁就记谁：又一辆车被承诺去同一个站，两列都不动。这也是「不可撤回」在存储这一侧的样子——
    /// 触发之后发生的任何承诺都不改写它。
    /// </summary>
    [Fact]
    public async Task TheFirstVehicleToTriggerTheYieldIsTheOneRecorded()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, JourneyRuntimeStage.AwaitingStationDeparture, LoadingPhaseStates.CargoHoldingWait);
        DateTimeOffset firstAt = Batch7JourneyFixture.Now.AddMinutes(3);
        await AcceptAsync(fixture, ComerDemand, ComerAgv, ComerKey, HoldingStation, firstAt);

        await AcceptAsync(fixture, ThirdDemand, ThirdAgv, ThirdKey, HoldingStation, firstAt.AddMinutes(1));

        JourneyRuntimeRow holder = await RowAsync(fixture, HolderDemand);
        Assert.Equal(firstAt, holder.YieldTriggeredAt);
        Assert.Equal(ComerKey, holder.YieldTriggeredByVehicleKey);
    }

    /// <summary>
    /// 途中追加把等单车所在的站排成了别的车的下一停靠：追加的那次提交里触发。别的车此刻站在它自己的取货停靠上（另一个站），
    /// 新取货停靠紧接在它后面。
    /// </summary>
    [Fact]
    public async Task AnAppendThatMakesTheStationAnotherVehiclesNextStopMakesTheHolderYield()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, JourneyRuntimeStage.AwaitingStationDeparture, LoadingPhaseStates.CargoHoldingWait);
        JourneyExecutionPlan comer = await AcceptAsync(
            fixture, ComerDemand, ComerAgv, ComerKey, ElsewhereStation, Batch7JourneyFixture.Now.AddMinutes(1));
        await HoldAsync(fixture, ComerDemand, JourneyRuntimeStage.AwaitingSublot, LoadingPhaseStates.Loading);

        DateTimeOffset appendedAt = Batch7JourneyFixture.Now.AddMinutes(4);
        await new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
            Batch7JourneyFixture.Snapshot(AppendedDemand, appendedAt),
            AppendPlan(comer, newPickupSequence: 2, unloadSequence: 3, appendedAt),
            TestContext.Current.CancellationToken);

        JourneyRuntimeRow holder = await RowAsync(fixture, HolderDemand);
        Assert.Equal(appendedAt, holder.YieldTriggeredAt);
        Assert.Equal(ComerKey, holder.YieldTriggeredByVehicleKey);
    }

    /// <summary>
    /// 等单车的站只是别的车的<b>后续</b>停靠（排在它的卸货之后），不是下一停靠：不触发。与上一条只差新取货停靠的序位。
    /// </summary>
    [Fact]
    public async Task AnAppendThatPutsTheStationLaterThanTheNextStopTriggersNothing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, JourneyRuntimeStage.AwaitingStationDeparture, LoadingPhaseStates.CargoHoldingWait);
        JourneyExecutionPlan comer = await AcceptAsync(
            fixture, ComerDemand, ComerAgv, ComerKey, ElsewhereStation, Batch7JourneyFixture.Now.AddMinutes(1));
        await HoldAsync(fixture, ComerDemand, JourneyRuntimeStage.AwaitingSublot, LoadingPhaseStates.Loading);

        DateTimeOffset appendedAt = Batch7JourneyFixture.Now.AddMinutes(4);
        await new WireToGateStore(fixture.NewContext()).AppendToJourneyAsync(
            Batch7JourneyFixture.Snapshot(AppendedDemand, appendedAt),
            AppendPlan(comer, newPickupSequence: 3, unloadSequence: 2, appendedAt),
            TestContext.Current.CancellationToken);

        Assert.Null((await RowAsync(fixture, HolderDemand)).YieldTriggeredAt);
    }

    /// <summary>追加在提交那一刻失败：追加没落，触发也没落。</summary>
    [Fact]
    public async Task AnAppendThatFailsAtTheCommitLeavesNoTriggerBehind()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, JourneyRuntimeStage.AwaitingStationDeparture, LoadingPhaseStates.CargoHoldingWait);
        JourneyExecutionPlan comer = await AcceptAsync(
            fixture, ComerDemand, ComerAgv, ComerKey, ElsewhereStation, Batch7JourneyFixture.Now.AddMinutes(1));
        await HoldAsync(fixture, ComerDemand, JourneyRuntimeStage.AwaitingSublot, LoadingPhaseStates.Loading);

        ControlServerDbContext dying = fixture.NewContext();
        FailTheSaveThatCarriesANewDemand(dying, "The append could not be committed.");
        DateTimeOffset appendedAt = Batch7JourneyFixture.Now.AddMinutes(4);
        await Assert.ThrowsAsync<HttpRequestException>(() => new WireToGateStore(dying).AppendToJourneyAsync(
            Batch7JourneyFixture.Snapshot(AppendedDemand, appendedAt),
            AppendPlan(comer, newPickupSequence: 2, unloadSequence: 3, appendedAt),
            TestContext.Current.CancellationToken));

        Assert.Null((await RowAsync(fixture, HolderDemand)).YieldTriggeredAt);
        Assert.False(await fixture.NewContext().AcceptedDemands
            .AnyAsync(row => row.DemandId == AppendedDemand, TestContext.Current.CancellationToken));
    }

    /// <summary>车自己的计划不触发自己：承诺方就是等单的那辆车时，它不被标记。</summary>
    [Fact]
    public async Task AVehiclesOwnPlanNeverMakesItYield()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptHolderAsync(fixture);
        await HoldAsync(fixture, HolderDemand, JourneyRuntimeStage.AwaitingStationDeparture, LoadingPhaseStates.CargoHoldingWait);

        ControlServerDbContext context = fixture.NewContext();
        IReadOnlyList<string> triggered = await StationYield.StageTriggerAsync(
            context, HolderKey, HoldingStation, Batch7JourneyFixture.Now.AddMinutes(3), TestContext.Current.CancellationToken);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(triggered);
        Assert.Null((await RowAsync(fixture, HolderDemand)).YieldTriggeredAt);
    }

    /// <summary>
    /// 「下一停靠」：车在路上是它正开往的那一个，站在停靠上是紧接着的下一个开着的；最后一个停靠上、阻断、完成时说不出。
    /// 已完成与已移除的停靠跳过，序位按数值排，不按行的先后。
    /// </summary>
    [Theory]
    [InlineData(JourneyRuntimeStage.AwaitingPickupArrival, "B")]
    [InlineData(JourneyRuntimeStage.AwaitingGateArrival, "B")]
    [InlineData(JourneyRuntimeStage.AwaitingSublot, "D")]
    [InlineData(JourneyRuntimeStage.AwaitingLoadResult, "D")]
    [InlineData(JourneyRuntimeStage.AwaitingStationDeparture, "D")]
    [InlineData(JourneyRuntimeStage.AwaitingDepartureSafety, "D")]
    [InlineData(JourneyRuntimeStage.AwaitingUnloadResult, "D")]
    [InlineData(JourneyRuntimeStage.Blocked, null)]
    [InlineData(JourneyRuntimeStage.Completed, null)]
    public void TheNextStopIsWhereTheVehicleGoesNext(JourneyRuntimeStage stage, string? expected)
    {
        JourneyStopRow[] stops =
        [
            Stop("D", 4, JourneyStopStatuses.Pending),
            Stop("A", 1, JourneyStopStatuses.Completed),
            Stop("C", 3, JourneyStopStatuses.Removed),
            Stop("B", 2, JourneyStopStatuses.Pending),
        ];

        Assert.Equal(expected, StationYield.NextStop(stage, stops)?.StopId);
    }

    /// <summary>站在最后一个停靠上，没有下一停靠。</summary>
    [Fact]
    public void OnItsLastStopAVehicleHasNoNextStop() =>
        Assert.Null(StationYield.NextStop(
            JourneyRuntimeStage.AwaitingUnloadResult,
            [Stop("A", 1, JourneyStopStatuses.Completed), Stop("B", 2, JourneyStopStatuses.Pending)]));

    private static void FailTheSaveThatCarriesANewDemand(ControlServerDbContext context, string message) =>
        context.SavingChanges += (sender, _) =>
        {
            if (((DbContext)sender!).ChangeTracker.Entries<AcceptedDemandRow>().Any(entry => entry.State == EntityState.Added))
            {
                throw new HttpRequestException(message);
            }
        };

    private static JourneyStopRow Stop(string stopId, int sequence, string status) => new()
    {
        StopId = stopId,
        JourneyId = "journey",
        Sequence = sequence,
        StopRole = JourneyStopRoles.Pickup,
        StationId = $"ST-{stopId}",
        StationRiotId = sequence,
        DispatchZone = "ZONE",
        OperationSessionId = "session",
        MovementLegId = "leg",
        UpperId = "upper",
        VehicleBusinessMessageId = "vbs",
        WorklistMessageId = "worklist",
        PlanMessageId = "plan",
        Status = status,
    };

    private static Task<JourneyExecutionPlan> AcceptHolderAsync(Batch7JourneyFixture fixture) =>
        AcceptAsync(fixture, HolderDemand, HolderAgv, HolderKey, HoldingStation, Batch7JourneyFixture.Now);

    private static Task<JourneyExecutionPlan> AcceptAsync(
        Batch7JourneyFixture fixture, string demandId, string agvId, string vehicleKey, int pickupStation, DateTimeOffset at) =>
        AcceptAsync(fixture.NewContext(), demandId, agvId, vehicleKey, pickupStation, at);

    private static async Task<JourneyExecutionPlan> AcceptAsync(
        ControlServerDbContext context, string demandId, string agvId, string vehicleKey, int pickupStation, DateTimeOffset at)
    {
        JourneyExecutionPlan plan = Batch7JourneyFixture.Plan(demandId, agvId, vehicleKey, at) with
        {
            PickupStationId = $"ST-{pickupStation}",
            PickupStationRiotId = pickupStation,
        };
        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            Batch7JourneyFixture.Snapshot(demandId, at),
            JourneyPlanBuilder.PickupIntent(plan, demandId, at),
            plan,
            TestContext.Current.CancellationToken);
        return plan;
    }

    /// <summary>把一趟旅程写成「车站在它的第一个停靠上，处于这个阶段与这个装货阶段」。</summary>
    private static async Task HoldAsync(
        Batch7JourneyFixture fixture, string demandId, JourneyRuntimeStage stage, string state)
    {
        await using ControlServerDbContext context = fixture.NewContext();
        JourneyRuntimeRow row = await context.JourneyRuntimes
            .SingleAsync(item => item.DemandId == demandId, TestContext.Current.CancellationToken);
        row.Stage = stage;
        row.LoadingPhaseState = state;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JourneyRuntimeRow> RowAsync(Batch7JourneyFixture fixture, string demandId)
    {
        await using ControlServerDbContext context = fixture.NewContext();
        return await context.JourneyRuntimes.AsNoTracking()
            .SingleAsync(row => row.DemandId == demandId, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 把一条取货在 <see cref="HoldingStation"/> 的需求追加进 <paramref name="comer"/> 那趟旅程：它自己的取货停靠留在 1，
    /// 新取货停靠与既有卸货停靠按给定的序位排。
    /// </summary>
    private static JourneyAppendPlan AppendPlan(
        JourneyExecutionPlan comer, int newPickupSequence, int unloadSequence, DateTimeOffset at)
    {
        string journeyId = JourneyIdentity.ForAnchorDemand(ComerDemand);
        return new JourneyAppendPlan(
            journeyId,
            AppendedDemand,
            Batch7JourneyFixture.Plan(AppendedDemand, ComerAgv, ComerKey, at) with
            {
                PickupStationId = $"ST-{HoldingStation}",
                PickupStationRiotId = HoldingStation,
            },
            JourneyIdentity.AppendedPickupStopId(AppendedDemand),
            JourneyIdentity.UnloadStopId(journeyId),
            comer.DispatchZone,
            DispatchZoneParameterVersion: 7,
            [
                new JourneyStopSequenceChange(JourneyIdentity.PickupStopId(journeyId), 1),
                new JourneyStopSequenceChange(JourneyIdentity.AppendedPickupStopId(AppendedDemand), newPickupSequence),
                new JourneyStopSequenceChange(JourneyIdentity.UnloadStopId(journeyId), unloadSequence),
            ],
            at);
    }
}
