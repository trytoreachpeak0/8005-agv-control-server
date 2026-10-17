using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.IdleReturn;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// 只做 <c>STAGING_TO_WIRE</c> 的车开回待送固定起点的执行（control-server#752）：承诺之后照等待点那一趟同一条路物化、建单、到站、收敛，
/// 只是那个站以 <c>FIXED_TASK_STATION</c>（<c>REQ-0204</c>）预占与占用，不查等待点登记。
/// </summary>
/// <remarks>
/// 待送点绑在 215（路网上比 214 远：从车位 300 出发 8000 对 5000 毫米），登记里只有 214。车队只留一辆、只做 <c>STAGING_TO_WIRE</c>。
/// </remarks>
public sealed partial class IdleReturnExecutionTests
{
    private const int StagingOrigin = 215;
    private const string StagingOriginName = "等待点215";

    /// <summary>
    /// 承诺、物化、建单都对着待送点；到站证据满足时预占转占用、用途释放、旅程收尾，同一轮。之后车停在待送点上，不再做空闲返回（control-server#712），
    /// 也不被当成占着等待点。
    /// </summary>
    [Fact]
    public async Task AStagingOnlyVehicleIsSentToTheStagingOriginAndConvergesThereAsItsFixedTaskStation()
    {
        await using FleetFixture fleet = await StagingOnlyFleetAsync();

        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);

        Assert.Equal(StagingOrigin, journey.PickupStationRiotId);
        var create = Assert.Single(fleet.Riot.Creates);
        Assert.Equal((KeyA, StagingOrigin), (create.VehicleKey, create.DestinationStationId));
        StationExclusivityRow reserved = (await StationAsync(fleet, StagingOrigin))!;
        Assert.Equal(
            (StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Reserved, KeyA, journey.JourneyId),
            (reserved.StationKind, reserved.State, reserved.VehicleKey, reserved.JourneyId));
        Assert.Null(await StationAsync(fleet, Near.StationId));

        await ArriveAsync(fleet, journey, KeyA, StagingOrigin);

        JourneyRuntimeRow closed = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal((JourneyRuntimeStage.Completed, (string?)null), (closed.Stage, closed.BlockReasonCode));
        Assert.Null(await ClaimOfAsync(fleet, KeyA));
        StationExclusivityRow occupied = (await StationAsync(fleet, StagingOrigin))!;
        Assert.Equal(
            (StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, KeyA, journey.JourneyId),
            (occupied.StationKind, occupied.State, occupied.VehicleKey, occupied.JourneyId));

        await RoundAsync(fleet);
        await RoundAsync(fleet);

        Assert.Equal(IdleReturnReasons.HeldAtStagingOrigin, fleet.IdleReturnBoard.Reasons[AgvA]);
        Assert.Single(fleet.Riot.Creates);
        Assert.Equal(journey.JourneyId, (await IdleJourneyAsync(fleet, AgvA))!.JourneyId);
    }

    /// <summary>
    /// 重启在承诺与物化之间：重启后物化出来的仍是开往待送点的那一趟，不被当成没有登记的等待点释放掉。
    /// </summary>
    [Fact]
    public async Task ARestartBetweenTheCommitmentAndItsMaterializationStillSendsTheVehicleToTheStagingOrigin()
    {
        await using FleetFixture fleet = await StagingOnlyFleetAsync();
        await RoundAsync(fleet);
        string committed = (await ClaimOfAsync(fleet, KeyA))!.Value.JourneyId;

        await fleet.RecreateEngineAsync();
        await RoundAsync(fleet);

        JourneyRuntimeRow journey = (await IdleJourneyAsync(fleet, AgvA))!;
        Assert.Equal((committed, StagingOrigin, JourneyRuntimeStage.AwaitingPickupArrival), (journey.JourneyId, journey.PickupStationRiotId, journey.Stage));
        Assert.Equal((KeyA, StagingOrigin), (Assert.Single(fleet.Riot.Creates).VehicleKey, fleet.Riot.Creates[0].DestinationStationId));
    }

    /// <summary>
    /// 开往待送点那一趟已确认失败（单被人在 RIoT 删了、车停稳）：冷却过后这一次回落到等待点（214），车不停在原地；
    /// 等待点那一趟收敛之后，它停在等待点上，下一趟空闲返回在它做了别的旅程之后才有。
    /// </summary>
    [Fact]
    public async Task AfterTheIdleReturnToTheStagingOriginFailsTheNextOneFallsBackToAWaitingPoint()
    {
        await using FleetFixture fleet = await StagingOnlyFleetAsync();
        JourneyRuntimeRow journey = await CommittedAndSentAsync(fleet);
        fleet.Riot.DeleteOrder(journey.PickupUpperId);

        await RoundAsync(fleet);
        await RoundAsync(fleet);
        await fleet.HearFromEveryVehicleAsync();
        await fleet.RunRoundAsync(fleet.Options.OwnOrderRebuildDelay);

        Assert.Equal(
            IdleReturnExecutionReasons.OrderEnded,
            (await fleet.Context.JourneyRuntimes.AsNoTracking().SingleAsync(row => row.JourneyId == journey.JourneyId, Token)).BlockReasonCode);
        (string Purpose, string JourneyId)? next = await ClaimOfAsync(fleet, KeyA);
        Assert.NotNull(next);
        StationExclusivityRow fallback = (await StationAsync(fleet, Near.StationId))!;
        Assert.Equal((StationExclusivityKinds.WaitingPoint, next.Value.JourneyId), (fallback.StationKind, fallback.JourneyId));
        Assert.Null(await StationAsync(fleet, StagingOrigin));
    }

    /// <summary>车队只留一辆、只做 <c>STAGING_TO_WIRE</c>；待送点绑在 215，登记里只有 214。</summary>
    private static async Task<FleetFixture> StagingOnlyFleetAsync()
    {
        FleetFixture fleet = await FleetFixture.CreateAsync(
            configure: options =>
            {
                options.Fleet = options.Fleet[..1];
                options.Fleet[0].AllowedTaskTypes = [TransportTaskTypes.StagingToWire];
            },
            withRouteGraph: true,
            commands: RecordIdleReturnCodes(null));
        await fleet.ReplaceRouteGraphAsync(Edges(), Stations());
        await TaskTypeStationRuntimeSeed.ActivateAsync(
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(fleet.Context.Database.GetDbConnection()).Options,
            fleet.Clock.GetUtcNowWithoutTick(),
            requiredTaskTypes: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings:
            [
                TaskTypeStationRuntimeSeed.GateBinding,
                new TaskTypeStationBinding(TransportTaskTypes.StagingToWire, StagingOrigin, StagingOriginName, "MAP-25-TEST"),
            ]);
        fleet.Riot.ExtraStations.Add(new RiotMapStation(StagingOrigin, StagingOriginName));
        await fleet.EnableIdleReturnAsync(Near);
        fleet.Catalog.Set([]);
        return fleet;
    }
}
