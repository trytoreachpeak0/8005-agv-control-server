using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// control-server#335（REQ-0246）：车在路上时，门锁未能证明锁闭——明确没锁、状态未知、没有观测、观测过期——交给故障模型：
/// 记故障、对本车本单 <c>OrderHold</c> 并回查，按不住、车仍在动就 <c>triggerEmergency</c>，从不 Cancel。
/// 正常行驶、6 秒之内的断线重连、站内授权装卸，一样都不许触发。
/// </summary>
/// <remarks>
/// <para>
/// <b>「未能证明」的口径是需求来源定的，不是这里选的。</b>REQ-0246 的来源决定（program 仓
/// <c>.scratch/current-requirements-baseline/issues/70-...</c> 答案第 2 条）：「明确未锁闭、反馈无效或未知均属于『仓门未能证明安全锁闭』，
/// 按不安全处理」。所以「没有观测」「观测过期」与「明确没锁」走同一条路，下面三格断的是同一个结果。
/// </para>
/// <para>
/// <b>事实从哪里来，是开工前在真装置上量出来的</b>（run 36387029532，<c>real-onboard-in-transit-door-facts</c>）：真车载端挂着本服务端
/// 在途单时会话整段 <c>RecoveryRequired</c>，但门锁摘要照发、照收，按「车号 + 会话行代次 + 会话行 SafetyRevision」取得到，全程非空；
/// 摘要是变了才发，最旧到 33 秒，心跳最长间隔约 2 秒。所以新鲜度按「这一代最后一条入站」判，阈值是会话静默窗口
/// <see cref="SessionLiveness.Timeout"/>，不按摘要自己的年龄判——那样每一趟走满几十秒都会被判过期。
/// </para>
/// <para>
/// <b><c>unknownPresent</c> 不是「门锁未知」。</b>真车载端在车动时它也为真（运动状态未知时），门锁那一栏照实报；门锁未知要看
/// <c>SLOT_STATE_UNKNOWN</c>。<see cref="ANormalDriveOnTheOwnOrderNeverHoldsOrStops"/> 守这一条：它的摘要带 <c>unknownPresent=true</c>。
/// </para>
/// <para>
/// <b>断言落在故障状态与命令审计这一层</b>，同 <see cref="FailedOrderBehindSessionGateTests"/>；判据断「发给了本车、本单」，
/// 不只断「发过一次」。
/// </para>
/// </remarks>
public sealed class InTransitDoorLockFaultTests
{
    /// <summary>门锁这一个症状在故障模型里的证据码。本票新增；值在这里写死，产品常量与它对不上时这里红。</summary>
    internal const string DoorSymptom = "VEHICLE_DOOR_NOT_PROVEN_LOCKED";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 三种「未能证明锁闭」：同一个结果 -------------------------------------------------------------------------

    /// <summary>
    /// 行驶中车载端报门没锁（<c>LOCK_NOT_CLOSED</c>），或报仓位状态未知（<c>SLOT_STATE_UNKNOWN</c>，IO 失联时的样子，真装置实测
    /// 同时带 <c>allTargetSlotsLocked=false</c>）。会话在闸门两侧各跑一遍：真车载端在途时未就绪（闸门后），合成车载端就绪（闸门前）。
    /// 取货段空车、关卡段有货各一遍；有货时货要与需求绑住（REQ-0238）。
    /// </summary>
    [Theory]
    [InlineData("pickup", "behind-gate", "unlocked")]
    [InlineData("pickup", "behind-gate", "unknown")]
    [InlineData("gate", "behind-gate", "unlocked")]
    [InlineData("gate", "behind-gate", "unknown")]
    [InlineData("pickup", "ready", "unlocked")]
    [InlineData("gate", "ready", "unknown")]
    [Trait("Requirement", "REQ-0246")]
    [Trait("Requirement", "REQ-0234")]
    public async Task ADoorNotProvenLockedWhileDrivingIsHeldOnThisOrderThenStopped(string leg, string side, string doors)
    {
        await using RuntimeFixture fixture = leg == "gate" ? await GateArrivalWaitAsync() : await DispatchedToPickupAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        string upperId = leg == "gate" ? underWay.GateUpperId! : underWay.PickupUpperId!;
        if (side == "behind-gate")
        {
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        }
        fixture.Riot.MovementState = "MT_RUNNING";
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);

        if (doors == "unlocked")
        {
            await fixture.ReportSafetySummaryAsync(
                allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED", "ACTION_NOT_ALLOWED_IN_STATE"]);
        }
        else
        {
            await fixture.ReportSafetySummaryAsync(
                allTargetSlotsLocked: false,
                unknownPresent: true,
                ["SLOT_STATE_UNKNOWN", "LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET", "ACTION_NOT_ALLOWED_IN_STATE"],
                allUnlockOutputsReset: false);
        }
        await DriveOneRoundAsync(fixture);

        await AssertHeldThenStoppedAsync(fixture, upperId);
        if (leg == "gate")
        {
            await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
            FaultedVehicleCargoRow cargo = await reading.FaultedVehicleCargo.AsNoTracking().SingleAsync(Token);
            Assert.Equal((underWay.DemandId, (DateTimeOffset?)null), (cargo.DemandId, cargo.ReleasedAt));
        }
    }

    /// <summary>
    /// 没有观测：车断线后隔了超过静默窗口才重连，新一代握手已开始、它的安全快照还没到。上一代最后一条门锁事实已经比
    /// <see cref="SessionLiveness.Timeout"/> 旧，这一代又什么都还没说——门锁在这段时间里做过什么，没有人知道。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task NoObservationAfterALongDisconnectWhileDrivingIsHeldThenStopped()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        await ReportDrivingOnTheOwnOrderAsync(fixture);
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);

        fixture.Clock.Advance(SessionLiveness.Timeout + TimeSpan.FromSeconds(1));
        await fixture.BeginGenerationWithoutSafetyAsync(2);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        await AssertHeldThenStoppedAsync(fixture, underWay.GateUpperId!);
    }

    /// <summary>
    /// 观测过期：会话没换代，门锁最后一次报的是锁着，但这一代已经超过静默窗口没有任何入站。闸门两侧各一遍：
    /// 未就绪的会话静默（真车载端在途），与就绪的会话静默（<see cref="JourneyRuntimeEngine.OnboardSessionLostReason"/> 那条路）。
    /// </summary>
    [Theory]
    [InlineData("behind-gate")]
    [InlineData("ready")]
    [Trait("Requirement", "REQ-0246")]
    public async Task AStaleObservationWhileDrivingIsHeldThenStopped(string side)
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        JourneyRuntimeRow underWay = await fixture.RuntimeAsync();
        if (side == "behind-gate")
        {
            await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
            await ReportDrivingOnTheOwnOrderAsync(fixture);
        }
        fixture.Riot.MovementState = "MT_RUNNING";
        await DriveOneRoundAsync(fixture);
        await AssertNothingRaisedAsync(fixture);

        // 一秒一轮、车载端不说话，走过静默窗口。边界上那一轮之前一条都不许发。
        int silentRounds = (int)SessionLiveness.Timeout.TotalSeconds + 1;
        for (int round = 1; round <= silentRounds; round++)
        {
            // Checked after rounds 0 to 5: the last word was at round 0, so the newest fact is at most 5 s old here.
            if (round <= SessionLiveness.Timeout.TotalSeconds)
            {
                await AssertNothingRaisedAsync(fixture);
            }
            await SilentRoundAsync(fixture);
        }

        await AssertHeldThenStoppedAsync(fixture, underWay.GateUpperId!);
    }

    // ---- 不许触发的 ------------------------------------------------------------------------------------------------

    /// <summary>
    /// 真车载端正常走一趟：会话因本服务端在途单整段未就绪，门锁全程锁着，摘要照真装置实测的样子变化——出发时
    /// <c>VEHICLE_NOT_READY</c> 且 <c>unknownPresent=true</c>，随后 <c>ACTION_NOT_ALLOWED_IN_STATE</c>——之后很久不再变。
    /// 心跳两秒一次。走 30 秒（远超静默窗口，也超过摘要不变的时长）：不记故障、不发 Hold、不急停。
    /// </summary>
    /// <remarks>
    /// 这一格合成 L2 按构造看不见（合成车载端永远报安全、会话永远就绪），真装置那一遍是 <c>real-onboard-normal-load</c>。
    /// 把「门锁未知」读成 <c>unknownPresent</c>、或者按摘要自己的年龄判过期，这条都会红。
    /// </remarks>
    [Theory]
    [InlineData("pickup")]
    [InlineData("gate")]
    [Trait("Requirement", "REQ-0246")]
    public async Task ANormalDriveOnTheOwnOrderNeverHoldsOrStops(string leg)
    {
        await using RuntimeFixture fixture = leg == "gate" ? await GateArrivalWaitAsync() : await DispatchedToPickupAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: true, unknownPresent: true, ["VEHICLE_NOT_READY"]);
        await DriveOneRoundAsync(fixture);
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);

        for (int second = 0; second < 30; second++)
        {
            await DriveOneRoundAsync(fixture, hear: second % 2 == 0);
            await AssertNothingRaisedAsync(fixture);
        }

        Assert.Equal("ONBOARD_SESSION_NOT_READY", (await fixture.RuntimeAsync()).BlockReasonCode);
    }

    /// <summary>
    /// 行驶中断线一次、静默窗口之内重连：新一代的会话行先到、安全快照后到（真装置实测相隔约 100 ms），中间那一轮新一代没有门锁事实。
    /// 上一代最后一条门锁事实仍在窗口之内，所以这一轮不许触发；快照到了之后也不许。
    /// </summary>
    /// <remarks>
    /// 按代次边界判「没有观测」，这条在快照到之前那一轮就会红——真装置上等于每次重连都按住加急停。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AReconnectWithinTheSilenceWindowWhileDrivingNeitherHoldsNorStops()
    {
        await using RuntimeFixture fixture = await GateArrivalWaitAsync();
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        fixture.Riot.MovementState = "MT_RUNNING";
        await ReportDrivingOnTheOwnOrderAsync(fixture);
        await DriveOneRoundAsync(fixture);

        // 断开，两秒后重连（真装置 2.24 s），新一代握手开始，快照未到。
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await fixture.BeginGenerationWithoutSafetyAsync(2);
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
        await AssertNothingRaisedAsync(fixture);

        // 快照到了，门锁锁着；会话仍因在途单未就绪。
        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);
        await OwnOrderRebuildTests.DropSessionOnOwnOrderAsync(fixture);
        for (int second = 0; second < 10; second++)
        {
            await DriveOneRoundAsync(fixture);
            await AssertNothingRaisedAsync(fixture);
        }
    }

    /// <summary>
    /// 站内授权装卸：车停在取货站、正在等录入，门开着是授权的开仓（来源决定答案第 3 条：只维持 <c>StationOperationGuard</c>，不告警、不急停）。
    /// 本票只管在途，这一格守住范围：到站之后门锁没锁不进故障模型。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task AnOpenDoorAtTheStationIsNotAnInTransitFault()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToSublotWaitAsync();
        JourneyRuntimeStage stage = (await fixture.RuntimeAsync()).Stage;
        Assert.NotEqual(JourneyRuntimeStage.AwaitingPickupArrival, stage);
        Assert.NotEqual(JourneyRuntimeStage.AwaitingGateArrival, stage);

        await fixture.ReportSafetySummaryAsync(
            allTargetSlotsLocked: false, unknownPresent: false, ["LOCK_NOT_CLOSED"], vehicleStopped: true);
        for (int second = 0; second < 3; second++)
        {
            await DriveOneRoundAsync(fixture);
            await AssertNothingRaisedAsync(fixture);
        }
    }

    // ---- 急停只有一个入口 ------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>EmergencyStopSupervisor.RequestStopAsync</c> 在产品里只有 <see cref="VehicleFaultCoordinator"/> 调用（票面第 4 条）：门锁症状交给
    /// 故障模型，不另开一条通往急停的路。第二个调用方出现的那一天，这里红。
    /// </summary>
    /// <remarks>
    /// 按调用点认，扫宿主程序集每个方法体的 IL，写法与 <c>VehicleFaultRecoveryTests.OnlyTheRuntimeRoundAndTheRecoveryTakeTheGate</c> 相同，
    /// 理由也相同：怎么拿到 supervisor 都要调这个方法，所以逃不过。<see cref="TheStopCallerScanSeesACallSite"/> 证明它认得出调用点。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public void OnlyTheFaultCoordinatorRequestsAnEmergencyStop()
    {
        Assert.Equal([nameof(VehicleFaultCoordinator)], StopRequesters(typeof(EmergencyStopSupervisor).Assembly));
    }

    /// <summary>扫描器自己的正例：本测试程序集里的 <see cref="StopRequesterProbe"/> 调了 <c>RequestStopAsync</c>，必须被认出来。</summary>
    [Fact]
    public void TheStopCallerScanSeesACallSite()
    {
        Assert.Contains(nameof(StopRequesterProbe), StopRequesters(typeof(StopRequesterProbe).Assembly));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>
    /// 真车载端在本服务端在途单上的样子：会话未就绪之后它报的那条摘要——门锁锁着，<c>ACTION_NOT_ALLOWED_IN_STATE</c>。
    /// </summary>
    private static Task ReportDrivingOnTheOwnOrderAsync(RuntimeFixture fixture) =>
        fixture.ReportSafetySummaryAsync(allTargetSlotsLocked: true, unknownPresent: false, ["ACTION_NOT_ALLOWED_IN_STATE"]);

    /// <summary>钟走一秒，车载端（按需）说一句话，跑一轮。</summary>
    private static async Task DriveOneRoundAsync(RuntimeFixture fixture, bool hear = true)
    {
        DateTimeOffset before = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(fixture.Clock.GetUtcNow() > before, "the clock did not move");
        if (hear)
        {
            await fixture.HearFromPeerAsync();
        }
        fixture.Context.ChangeTracker.Clear();
        await fixture.Engine.ExecuteOnceAsync(Token);
        fixture.Context.ChangeTracker.Clear();
    }

    private static Task SilentRoundAsync(RuntimeFixture fixture) => DriveOneRoundAsync(fixture, hear: false);

    /// <summary>没有故障事实、没有 Hold、没有急停、没有 Cancel。</summary>
    private static async Task AssertNothingRaisedAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        Assert.Empty(await reading.VehicleFaultStates.AsNoTracking()
            .Where(row => row.Level != VehicleFaultLevel.None).ToArrayAsync(Token));
        Assert.Empty(await reading.RiotOrderCommandAudit.AsNoTracking()
            .Where(row => row.CommandType == RiotCommandTypeNames.OrderHold ||
                          row.CommandType == RiotCommandTypeNames.TriggerEmergency ||
                          row.CommandType == RiotCommandTypeNames.CancelOrder)
            .ToArrayAsync(Token));
    }

    /// <summary>
    /// 这一轮之后：一条 <c>SuspectedBlocked</c> 故障（第 1 代、证据是门锁症状、已升级）；审计里对本车本单发过一次 Hold、
    /// 而且早于急停；急停触发一次；没有任何 Cancel；旅程写门锁症状。
    /// </summary>
    private static async Task AssertHeldThenStoppedAsync(RuntimeFixture fixture, string upperId)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = Assert.Single(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            (fixture.Options.AgvId, VehicleFaultLevel.SuspectedBlocked, 1L, DoorSymptom, true),
            (fault.AgvId, fault.Level, fault.FaultGeneration, fault.EvidenceCode, fault.EscalatedAt is not null));

        RiotOrderCommandAuditRow[] audit = await reading.RiotOrderCommandAudit.AsNoTracking().ToArrayAsync(Token);
        RiotOrderCommandAuditRow hold = Assert.Single(audit, row => row.CommandType == RiotCommandTypeNames.OrderHold);
        Assert.Equal(
            (fixture.Options.AgvId, upperId, (long?)1),
            (hold.AgvId, hold.TargetUpperId, hold.FaultGeneration));
        RiotOrderCommandAuditRow trigger = Assert.Single(
            audit, row => row.CommandType == RiotCommandTypeNames.TriggerEmergency);
        Assert.Equal((fixture.Options.AgvId, (long?)1), (trigger.AgvId, trigger.FaultGeneration));
        Assert.True(hold.IssuedAt <= trigger.IssuedAt, "the hold must be issued before the emergency stop");
        Assert.DoesNotContain(audit, row => row.CommandType == RiotCommandTypeNames.CancelOrder);

        Assert.Equal(DoorSymptom, (await reading.JourneyRuntimes.AsNoTracking().SingleAsync(Token)).BlockReasonCode);
    }

    private static string[] StopRequesters(System.Reflection.Assembly assembly)
    {
        System.Reflection.MethodInfo[] targets =
            [typeof(EmergencyStopSupervisor).GetMethod(nameof(EmergencyStopSupervisor.RequestStopAsync))!];
        const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.DeclaredOnly;
        HashSet<string> callers = new(StringComparer.Ordinal);
        foreach (Type type in assembly.GetTypes())
        {
            IEnumerable<System.Reflection.MethodBase> methods =
                type.GetMethods(all).Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(all));
            foreach (System.Reflection.MethodBase method in methods)
            {
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il is null || !CallsAny(method, il, targets))
                {
                    continue;
                }

                Type outer = type;
                while (outer.DeclaringType is not null)
                {
                    outer = outer.DeclaringType;
                }
                callers.Add(outer.Name);
            }
        }

        return [.. callers.Order(StringComparer.Ordinal)];
    }

    private static bool CallsAny(System.Reflection.MethodBase method, byte[] il, System.Reflection.MethodInfo[] targets)
    {
        for (int index = 0; index + 4 < il.Length; index++)
        {
            if (il[index] is not (0x28 or 0x6F))
            {
                continue;
            }

            try
            {
                System.Reflection.MethodBase? called = method.Module.ResolveMethod(
                    BitConverter.ToInt32(il, index + 1),
                    method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
                if (called is not null && targets.Contains(called))
                {
                    return true;
                }
            }
            catch (Exception error) when (error is ArgumentException or BadImageFormatException or MissingMethodException or
                                          TypeLoadException)
            {
            }
        }

        return false;
    }

    private static async Task<RuntimeFixture> DispatchedToPickupAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await TickAndRunAsync(fixture);
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }

    private static async Task<RuntimeFixture> GateArrivalWaitAsync()
    {
        RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(FirstDemandId, FirstSublot, createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set(FirstSublot, 7);
        await fixture.AdvanceToGateArrivalAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingGateArrival, (await fixture.RuntimeAsync()).Stage);
        fixture.Context.ChangeTracker.Clear();
        return fixture;
    }
}

/// <summary><see cref="InTransitDoorLockFaultTests.TheStopCallerScanSeesACallSite"/> 的正例：一个调用 <c>RequestStopAsync</c> 的方法。</summary>
internal static class StopRequesterProbe
{
    internal static Task<EmergencyStopDecision> RequestAsync(EmergencyStopSupervisor supervisor, EmergencyStopRequest request) =>
        supervisor.RequestStopAsync(request, CancellationToken.None);
}
