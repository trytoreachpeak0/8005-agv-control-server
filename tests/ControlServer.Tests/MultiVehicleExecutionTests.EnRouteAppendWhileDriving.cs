using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 途中追加只在停站时、行驶中不接（用户 2026-09-22 定；批次10-09，control-server#577）：车在路上时第二条需求不进它的旅程，
/// 原因码 <c>ONBOARD_FACTS_NOT_READY</c>，原有那一单照常。
/// </summary>
/// <remarks>
/// <para>
/// <b>这两条用例守的是一个前提，不是一道判据：服务端靠车载端未就绪挡住行驶中的追加。</b>在途链
/// （<see cref="Host.Runtime.Dispatch.Criteria.InTransitVehicleFactsCriterion"/>）刻意不要求车停着；它只在读不到车载端事实时
/// 拒绝。真车载端在车跑本服务端的在途单时整段报未就绪——RIoT 安全读数带 <c>RIOT_NONFINAL_ORDER_PRESENT</c>、
/// <c>motionState=Unknown</c>，车载端据此报 <c>VEHICLE_NOT_READY</c>，会话落到 <c>RecoveryRequired / DEPARTURE_SAFETY_NOT_READY</c>
/// ——于是 <see cref="OnboardDispatchFactsReader.ReadOnboardFactsAsync"/> 读出空，在途链判 <c>ONBOARD_FACTS_NOT_READY</c>。
/// 机理与出处写在 control-server#577 的 PR 里。
/// </para>
/// <para>
/// <b>以后改真车载端（例如行驶中也报就绪），或者改合成车载端（<c>tools/ControlServer.FakeOnboard</c> 永远报就绪），
/// 这条规则在服务端就没有东西守了。</b>第二条用例把这一点写成可执行的：车在跑、车载端仍就绪时追加今天会被受理。
/// 若服务端以后自己加了「车在行驶不接追加」的判据（PR 里的选项乙），第二条会红，那时改它的期望，而不是删掉它。
/// </para>
/// <para>
/// 车队裁成一辆、装路网，与 <see cref="AnInTransitVehicleTakesAnAppendedDemandIntoItsExistingJourney"/> 同一个夹具、同样两条需求：
/// 三辆车时第二条需求会被空闲车接走，测不到追加；没有路网，在途追加那条判据不进链。
/// </para>
/// </remarks>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 车在跑本服务端的在途单、真车载端照真实形状报未就绪：第二条需求不进这趟旅程，积压行原因码 <c>ONBOARD_FACTS_NOT_READY</c>，
    /// 原有那一单照常——同一趟旅程、没被阻断、在途单没被取消、没有建第二张单。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0197")]
    public async Task AnAppendWhileTheVehicleIsDrivingIsRefusedBecauseTheOnboardIsNotReady()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await DriveFirstDemandAsync();
        JourneyRuntimeRow first = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);
        int ordersCreated = fixture.Riot.Creates.Count;

        await ReportOnboardNotReadyWhileDrivingAsync(fixture, FleetFixture.AgvIds[0]);
        AcceptedDemandSnapshot second = FleetFixture.Demand(1, "N1-2", 1);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), second]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        // 追加被拒，理由是读不到车载端事实——不是别的判据碰巧先挡住。
        Assert.Equal(
            "ONBOARD_FACTS_NOT_READY",
            (await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(row => row.DemandId == second.DemandId, token))
            .ReasonCode);
        JourneyDemandRow membership = Assert.Single(
            await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(token));
        Assert.Equal(FleetFixture.Demand(0, "N1-1", 0).DemandId, membership.DemandId);

        // 原有那一单照常：还是那一趟旅程，没被阻断，在途单仍未终结，没有为第二条需求建任何单。
        JourneyRuntimeRow only = Assert.Single(await fixture.Context.JourneyRuntimes.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(first.JourneyId, only.JourneyId);
        Assert.NotEqual(JourneyRuntimeStage.Blocked, only.Stage);
        Assert.Equal(RiotOrderObservationKind.Active, fixture.Riot.OrderOf(first.PickupUpperId)!.Kind);
        Assert.Equal(ordersCreated, fixture.Riot.Creates.Count);
    }

    /// <summary>
    /// 前提的另一面：车同样在跑，车载端却仍报就绪（合成车载端的形状），第二条需求今天会被追加进这趟旅程——服务端自己没有
    /// 「车在行驶」这一道。
    /// </summary>
    /// <remarks>
    /// 这不是在认可行驶中追加，是把「规则只靠车载端守」写成会响的东西：上一条用例的判别力就是这一条，两者只差会话就绪与否。
    /// 服务端加了自己的行驶判据之后，这一条该改成期望拒绝。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0197")]
    public async Task TheServerHasNoDrivingGateOfItsOwnSoAnOnboardStillReadyWhileDrivingGetsTheAppend()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await DriveFirstDemandAsync();
        JourneyRuntimeRow first = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);

        ReportRiotDriving(fixture, first.VehicleKey);
        await fixture.HearFromEveryVehicleAsync();
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(token);
        Assert.Equal([first.JourneyId, first.JourneyId], memberships.Select(row => row.JourneyId));
    }

    /// <summary>一辆车，受理第一条需求、建单出发。</summary>
    private static async Task<FleetFixture> DriveFirstDemandAsync()
    {
        FleetFixture fixture = await FleetFixture.CreateAsync(
            configure: options => options.Fleet = options.Fleet[..1], withRouteGraph: true);
        await fixture.AllowEnRouteAppendAsync(1_000_000);
        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0)]);
        await fixture.RunRoundAsync();
        Assert.Single(fixture.Riot.Creates);
        return fixture;
    }

    /// <summary>
    /// RIoT 那一侧报车在跑自己的单：车辆读数非 <c>IDLE</c>、有速度、有任务号；安全读数带 <c>RIOT_NONFINAL_ORDER_PRESENT</c>
    /// （<c>HttpRiotMovementGateway</c> 对任何未终结的单都这样报，于是 <c>motionState</c> 为 <c>Unknown</c>）。
    /// </summary>
    private static void ReportRiotDriving(FleetFixture fixture, string vehicleKey)
    {
        fixture.Riot.VehicleOverrides[vehicleKey] = observation => observation with
        {
            ProcState = "RUNNING",
            Speed = 0.82,
            LockStatus = 1,
            OrderTaskId = "RIOT-TASK-577",
        };
        fixture.Riot.SafetyReasons = ["RIOT_NONFINAL_ORDER_PRESENT"];
        fixture.Riot.MovementState = "MT_RUNNING";
    }

    /// <summary>
    /// 真车载端在行驶中报的样子：车载端读到上面那份 RIoT 安全读数，报 <c>VEHICLE_NOT_READY</c>，服务端把会话落到
    /// <c>RecoveryRequired / DEPARTURE_SAFETY_NOT_READY</c>。会话仍连着、心跳照发——这不是断线，与
    /// <see cref="FleetFixture.DropSessionAsync"/> 不同；形状与 <c>PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync</c> 一致。
    /// </summary>
    private static async Task ReportOnboardNotReadyWhileDrivingAsync(FleetFixture fixture, string agvId)
    {
        JourneyRuntimeRow journey = await fixture.JourneyOfAsync(agvId);
        ReportRiotDriving(fixture, journey.VehicleKey);
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(
            row => row.AgvId == agvId, TestContext.Current.CancellationToken);
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "DEPARTURE_SAFETY_NOT_READY";
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = """["VEHICLE_NOT_READY"]""";
        session.SafetyUnknownPresent = true;
        session.UpdatedAt = fixture.Clock.GetUtcNow();
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromEveryVehicleAsync();
    }
}
