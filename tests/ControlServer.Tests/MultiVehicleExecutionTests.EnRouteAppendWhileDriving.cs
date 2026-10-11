using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 途中追加只在停站时、行驶中不接（用户 2026-09-22 定；批次10-09，control-server#577）：车在路上时第二条需求不进它的旅程，
/// 原因码 <c>ONBOARD_FACTS_NOT_READY</c>，原有那一单照常。
/// </summary>
/// <remarks>
/// <para>
/// <b>这几条用例守的是一个前提，不是一道判据：服务端靠车载端未就绪挡住行驶中的追加。</b>在途链
/// （<see cref="Host.Runtime.Dispatch.Criteria.InTransitVehicleFactsCriterion"/>）刻意不要求车停着，它只在读不到车载端事实时
/// 拒绝。真车载端在车跑本服务端的在途单时整段报离站不安全：单还开着、车还没动时，RIoT 安全读数带
/// <c>RIOT_NONFINAL_ORDER_PRESENT</c>、<c>motionState=UNKNOWN</c>，车载端报 <c>VEHICLE_NOT_READY</c>、<c>unknownPresent=true</c>；
/// 车动起来以后读数是 <c>MOVING</c>，车载端报 <c>ACTION_NOT_ALLOWED_IN_STATE</c>。两种摘要都是 <c>departureSafe=false</c>，
/// 服务端的就绪判定把会话落到 <c>RecoveryRequired / DEPARTURE_SAFETY_NOT_READY</c>，于是
/// <see cref="Host.Runtime.Dispatch.OnboardDispatchFactsReader.ReadOnboardFactsAsync"/> 读出空，在途链判 <c>ONBOARD_FACTS_NOT_READY</c>。
/// 两端的出处（file:line）写在 control-server#577 的 PR 里。
/// </para>
/// <para>
/// 会话就绪与否不在这里写死：用例像车载端那样送一份安全摘要进来（落一条 <c>SafetyStateChanged</c>，经
/// <see cref="WireToGateStore.ApplySafetySnapshotAsync"/> 与 <see cref="WireToGateStore.DecideReadinessAsync"/>，与
/// <c>OnboardMessageProcessor</c> 收到这条消息时同一对调用），由服务端自己判。
/// </para>
/// <para>
/// <b>以后改真车载端（例如行驶中也报离站安全），或者照合成车载端（<c>tools/ControlServer.FakeOnboard</c> 的
/// <c>SafetySummary</c> 默认 <c>departureSafe=true</c>、<c>vehicleStopped=true</c>，行驶中不变）去理解真车，这条规则在服务端就没有
/// 东西守了。</b>最后一条用例把这一点写成可执行的：车在跑、车载端仍报安全时，追加今天会被受理。服务端以后若自己加了
/// 「车在行驶不接追加」的判据（PR 里的选项乙），那一条会红，那时改它的期望，而不是删掉它。
/// </para>
/// <para>
/// 车队裁成一辆、装路网，与 <see cref="AnInTransitVehicleTakesAnAppendedDemandIntoItsExistingJourney"/> 同一个夹具、同样两条需求：
/// 三辆车时第二条需求会被空闲车接走，测不到追加；没有路网，在途追加那条判据不进链。
/// </para>
/// </remarks>
public sealed partial class MultiVehicleExecutionTests
{
    /// <summary>
    /// 车在跑本服务端的在途单、真车载端照真实形状报离站不安全：第二条需求不进这趟旅程，积压行原因码 <c>ONBOARD_FACTS_NOT_READY</c>，
    /// 原有那一单照常——同一趟旅程、没被阻断、在途单没被终结、没有建第二张单。
    /// </summary>
    /// <param name="phase">
    /// <c>order-open</c>：单已建、车还没动（安全读数 <c>UNKNOWN</c>）；<c>moving</c>：车在动（安全读数 <c>MOVING</c>）。
    /// </param>
    [Theory]
    [InlineData("order-open")]
    [InlineData("moving")]
    [Trait("Requirement", "REQ-0197")]
    public async Task AnAppendWhileTheVehicleIsDrivingIsRefusedBecauseTheOnboardIsNotReady(string phase)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await DriveFirstDemandAsync();
        JourneyRuntimeRow first = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);
        int ordersCreated = fixture.Riot.Creates.Count;

        ReportRiotDriving(fixture, first.VehicleKey, moving: phase == "moving");
        SessionReadinessDecision decision = await ReportOnboardSafetyAsync(
            fixture, FleetFixture.AgvIds[0], phase == "moving" ? MovingSummary : OrderOpenSummary);
        // 服务端自己判出来的，不是用例写进去的。
        Assert.Equal(
            (SessionReadiness.RecoveryRequired, "DEPARTURE_SAFETY_NOT_READY"),
            (decision.Readiness, decision.ReasonCode));

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
    /// 前提的另一面：车同样在动，车载端却仍报离站安全（合成车载端的形状），第二条需求今天会被追加进这趟旅程——服务端自己没有
    /// 「车在行驶」这一道。
    /// </summary>
    /// <remarks>
    /// 这不是在认可行驶中追加，是把「规则只靠车载端守」写成会响的东西：上一条用例的判别力就是这一条，两者只差车载端送来的那份
    /// 安全摘要。服务端加了自己的行驶判据之后，这一条该改成期望拒绝。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0197")]
    public async Task TheServerHasNoDrivingGateOfItsOwnSoAnOnboardStillSafeWhileDrivingGetsTheAppend()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using FleetFixture fixture = await DriveFirstDemandAsync();
        JourneyRuntimeRow first = await fixture.JourneyOfAsync(FleetFixture.AgvIds[0]);

        ReportRiotDriving(fixture, first.VehicleKey, moving: true);
        SessionReadinessDecision decision = await ReportOnboardSafetyAsync(fixture, FleetFixture.AgvIds[0], SyntheticSummary);
        Assert.Equal(SessionReadiness.Ready, decision.Readiness);

        fixture.Catalog.Set([FleetFixture.Demand(0, "N1-1", 0), FleetFixture.Demand(1, "N1-2", 1)]);
        await fixture.RunRoundAsync(TimeSpan.FromSeconds(1));

        JourneyDemandRow[] memberships = await fixture.Context.Set<JourneyDemandRow>().AsNoTracking().ToArrayAsync(token);
        Assert.Equal([first.JourneyId, first.JourneyId], memberships.Select(row => row.JourneyId));
    }

    /// <summary>车载端安全摘要里本用例关心的几项；仓位一律锁好、开锁输出复位，与车在两站之间时一样。</summary>
    private sealed record OnboardSafetySummary(bool DepartureSafe, bool VehicleStopped, bool UnknownPresent, string[] ReasonCodes);

    /// <summary>单已建、车还没动：车载端读到 <c>UNKNOWN</c>，报 <c>VEHICLE_NOT_READY</c>。</summary>
    private static readonly OnboardSafetySummary OrderOpenSummary = new(false, false, true, ["VEHICLE_NOT_READY"]);

    /// <summary>车在动：车载端读到 <c>MOVING</c>，报 <c>ACTION_NOT_ALLOWED_IN_STATE</c>，不算未知。</summary>
    private static readonly OnboardSafetySummary MovingSummary = new(false, false, false, ["ACTION_NOT_ALLOWED_IN_STATE"]);

    /// <summary>合成车载端不论车在做什么都报的那一份。</summary>
    private static readonly OnboardSafetySummary SyntheticSummary = new(true, true, false, []);

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
    /// RIoT 那一侧报车在跑自己的单：车辆读数 <c>PROCESSING_ORDER</c>、有任务号，动起来以后再有速度与 <c>MT_RUNNING</c>；
    /// 安全读数带 <c>RIOT_NONFINAL_ORDER_PRESENT</c>（<c>HttpRiotMovementGateway.Safety</c> 对任何未终结的单都这样报）。
    /// </summary>
    private static void ReportRiotDriving(FleetFixture fixture, string vehicleKey, bool moving)
    {
        fixture.Riot.VehicleOverrides[vehicleKey] = observation => observation with
        {
            ProcState = "PROCESSING_ORDER",
            Speed = moving ? 0.82 : 0,
            LockStatus = 1,
            OrderTaskId = "RIOT-TASK-577",
        };
        fixture.Riot.SafetyReasons = moving ? ["RIOT_MOTION_ACTIVE"] : ["RIOT_NONFINAL_ORDER_PRESENT"];
        fixture.Riot.MovementState = moving ? "MT_RUNNING" : "MT_WAIT_FOR_START";
    }

    /// <summary>
    /// 车载端送来一份新的安全摘要（<c>SafetyStateChanged</c>，版本 8，接在夹具种下的版本 7 之后），服务端照收到这条消息时那样
    /// 落库并重新判就绪。会话仍连着、心跳照发——这不是断线，与 <see cref="FleetFixture.DropSessionAsync"/> 不同。
    /// </summary>
    private static async Task<SessionReadinessDecision> ReportOnboardSafetyAsync(
        FleetFixture fixture, string agvId, OnboardSafetySummary summary)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const long revision = 8;
        const long generation = 1;
        string messageId = Guid.NewGuid().ToString("D");
        fixture.Context.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = messageId,
            MessageType = "SafetyStateChanged",
            RequestJson = JsonSerializer.Serialize(
                new
                {
                    messageType = "SafetyStateChanged",
                    messageId,
                    agvId,
                    sessionGeneration = generation,
                    sentAt = fixture.Clock.GetUtcNow(),
                    payload = new
                    {
                        safetyStateVersion = revision,
                        observedAt = fixture.Clock.GetUtcNow(),
                        safety = new
                        {
                            departureSafe = summary.DepartureSafe,
                            vehicleStopped = summary.VehicleStopped,
                            allTargetSlotsLocked = true,
                            allUnlockOutputsReset = true,
                            unknownPresent = summary.UnknownPresent,
                            reasonCodes = summary.ReasonCodes,
                        },
                    },
                },
                SerializerOptions),
            ContentHash = new string('8', 64),
            FirstResponseJson = "{}",
            ReceivedAt = fixture.Clock.GetUtcNow(),
        });
        await fixture.Context.SaveChangesAsync(token);

        WireToGateStore store = new(fixture.Context);
        await store.ApplySafetySnapshotAsync(
            agvId, generation, revision, summary.DepartureSafe, new string('8', 64), token,
            summary.ReasonCodes, summary.UnknownPresent);
        SessionReadinessDecision decision = await store.DecideReadinessAsync(agvId, generation, token);
        fixture.Context.ChangeTracker.Clear();
        await fixture.HearFromEveryVehicleAsync();
        return decision;
    }
}
