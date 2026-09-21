using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Tests;

/// <summary>
/// 在途车那条动态事实判据（REQ-0205，批次7-06，control-server#211）判的是一辆<b>正在跑</b>的车：
/// RIoT 报着非 <c>IDLE</c>、速度不为零、手上有订单号。这里对着那样一份事实，逐项问两条判据各自怎么答。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么非要用一份「真在跑」的事实。</b>多车夹具造出来的车，RIoT 那一侧默认是
/// <c>IDLE</c>、速度零、没有订单——一份<b>空闲车</b>的事实。拿它去证明「在途链放行了这辆车」，
/// 证明不了任何事：空闲链对同一份事实也会放行。那样的用例测的是一个比现实简单的世界，它不会漏掉
/// 在途链写了什么，只会漏掉现实里会发生、而那个世界里不会发生的事。
/// </para>
/// <para>
/// <b>下面四组各答一个问题</b>：整辆真在跑的车，两条链答得相反吗（第一组）；在途链刻意不问的是<b>哪几项</b>、
/// 每一项单拎出来空闲链报的是哪个原因码（第二组）；在途链<b>仍然</b>问的那几项，对一辆在跑的车照样挡吗
/// （第三组）；以及共用的那几项，两条链给的原因码一样吗（第四组，对着一辆空闲的车问，因为只有那样两边才可比）。
/// 后两组守的是「这不是把空闲车那条判据放宽」这句话——没有它们，把在途判据写成无条件返回
/// <c>ELIGIBLE</c> 也全绿。
/// </para>
/// </remarks>
public sealed class InTransitVehicleFactsTests
{
    private const string VehicleKey = "BROKERX-0001";
    private const string Map = "MAP-26";
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 一辆正在跑自己那趟旅程的车：在途链放行，空闲链挡。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两条链对<b>同一份事实</b>给出相反的答案，这才是「另一条判据」这句话的内容。
    /// </para>
    /// <para>
    /// <b>空闲链报的是 <c>ONBOARD_DEPARTURE_UNSAFE</c>，不是 <c>RIOT_VEHICLE_NOT_IDLE</c></b>，
    /// 这里写死那个码是为了把空闲链的短路顺序一并钉住：一辆真在跑的车，车载端事实（在动、目标仓位未锁）
    /// 与 RIoT 事实（非 <c>IDLE</c>、有速度、有订单）<b>五处都不成立</b>，而空闲链先问车载端。
    /// 后面那几处各自是不是也被在途链放过了，这条断言看不见——由下面逐项那组分别钉住。
    /// </para>
    /// </remarks>
    [Fact]
    public void AVehicleUnderWayIsAdmittedByTheInTransitChainAndRefusedByTheIdleOne()
    {
        DispatchVehicleFacts underWay = UnderWay();

        Assert.Equal(DispatchAdmissionChain.Eligible, InTransitVehicleFactsCriterion.Evaluate(underWay, Options()));
        Assert.Equal("ONBOARD_DEPARTURE_UNSAFE", VehicleDynamicFactsCriterion.Evaluate(underWay, Options()));
    }

    /// <summary>
    /// 在途链刻意不问的六项，每一项单独拎出来：空闲链挡，在途链放行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 逐项而不是整辆一起，因为整辆一起时空闲链只会报<b>第一个</b>撞上的原因码，后面几项是不是也被
    /// 在途链放过了，那个断言看不见。
    /// </para>
    /// <para>
    /// <b><c>AllTargetSlotsLocked</c> 是其中唯一一项安全事实</b>，在途链去掉它是有意的：它说的是这一趟
    /// 已经装好的货有没有锁住，而追加一条需求既不改变那件事，也不该被它挡住——车正在两站之间走，
    /// 下一站的目标仓位本来就还没锁。其余五项是运行状态，不是安全。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("procState", "RIOT_VEHICLE_NOT_IDLE")]
    [InlineData("speed", "RIOT_VEHICLE_NOT_STOPPED")]
    [InlineData("orderTaskId", "RIOT_VEHICLE_ORDER_OCCUPIED")]
    [InlineData("lockStatus", "RIOT_VEHICLE_ORDER_OCCUPIED")]
    [InlineData("vehicleMoving", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("targetSlotsUnlocked", "ONBOARD_DEPARTURE_UNSAFE")]
    public void EachFactTheInTransitChainDoesNotAskAboutStopsTheIdleChainOnItsOwn(string fact, string idleReason)
    {
        DispatchVehicleFacts facts = Idle();
        facts = fact switch
        {
            "procState" => With(facts, observation => observation with { ProcState = "RUNNING" }),
            "speed" => With(facts, observation => observation with { Speed = 0.82 }),
            "orderTaskId" => With(facts, observation => observation with { OrderTaskId = "RIOT-TASK-9001" }),
            "lockStatus" => With(facts, observation => observation with { LockStatus = 1 }),
            "vehicleMoving" => facts with { Onboard = facts.Onboard! with { VehicleStopped = false } },
            "targetSlotsUnlocked" => facts with { Onboard = facts.Onboard! with { AllTargetSlotsLocked = false } },
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact, "unknown fact"),
        };

        Assert.Equal(idleReason, VehicleDynamicFactsCriterion.Evaluate(facts, Options()));
        Assert.Equal(DispatchAdmissionChain.Eligible, InTransitVehicleFactsCriterion.Evaluate(facts, Options()));
    }

    /// <summary>
    /// 在途链仍然问的那几项，对一辆正在跑的车照样挡。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这一组是上面那组的另一半，也是这个文件里唯一会因为「把在途链写宽一点」而红的一组：只有上面那组时，
    /// 一个无条件返回 <c>ELIGIBLE</c> 的在途判据全绿。
    /// </para>
    /// <para>
    /// <b>这里不与空闲链对照</b>，尽管第一版那么写过。对一辆正在跑的车，空闲链几乎总是先撞上它独有的那几项
    /// （车在动、目标仓位未锁）而报 <c>ONBOARD_DEPARTURE_UNSAFE</c>，两条链的原因码因此本来就不该一样——
    /// 断言它们相同是拿一个错误的期望去测一份正确的实现。「共用那几项在两条链里判得一样」是另一个问题，
    /// 由下面那组对着一辆<b>空闲</b>的车问。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("departureUnsafe", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("unknownPresent", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("unlockOutputsNotReset", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("onboardFactsMissing", "ONBOARD_FACTS_NOT_READY")]
    [InlineData("disconnected", "RIOT_VEHICLE_NOT_AVAILABLE")]
    [InlineData("bindingMismatch", "RIOT_VEHICLE_BINDING_MISMATCH")]
    [InlineData("wrongMap", "RIOT_VEHICLE_MAP_MISMATCH")]
    [InlineData("staleObservation", "RIOT_VEHICLE_FACT_STALE")]
    [InlineData("batteryUnknown", "BATTERY_FACT_UNKNOWN")]
    [InlineData("batteryTooLow", "BATTERY_POLICY_NOT_SATISFIED")]
    [InlineData("charging", "BATTERY_POLICY_NOT_SATISFIED")]
    public void TheFactsBothChainsAskAboutStopAVehicleUnderWayToo(string fault, string reason)
    {
        DispatchVehicleFacts facts = Break(UnderWay(), fault);

        Assert.Equal(reason, InTransitVehicleFactsCriterion.Evaluate(facts, Options()));
    }

    /// <summary>
    /// 共用的那几项，两条链判得一模一样——同一个原因码，不只是「都挡住了」。
    /// </summary>
    /// <remarks>
    /// 对着一辆<b>空闲</b>的车问，因为只有那样空闲链才不会先撞上它独有的检查，两边才可比。
    /// 上面那组证明在途链仍然挡，这一组证明它挡的<b>理由</b>没有跟空闲链分叉：两份判定是抄在两个类里的，
    /// 一处改了另一处不会跟着动，而准入原因码是仪表盘和结构化派车阻塞都读的东西。
    /// </remarks>
    [Theory]
    [InlineData("departureUnsafe", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("unknownPresent", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("unlockOutputsNotReset", "ONBOARD_DEPARTURE_UNSAFE")]
    [InlineData("onboardFactsMissing", "ONBOARD_FACTS_NOT_READY")]
    [InlineData("disconnected", "RIOT_VEHICLE_NOT_AVAILABLE")]
    [InlineData("bindingMismatch", "RIOT_VEHICLE_BINDING_MISMATCH")]
    [InlineData("wrongMap", "RIOT_VEHICLE_MAP_MISMATCH")]
    [InlineData("staleObservation", "RIOT_VEHICLE_FACT_STALE")]
    [InlineData("batteryUnknown", "BATTERY_FACT_UNKNOWN")]
    [InlineData("batteryTooLow", "BATTERY_POLICY_NOT_SATISFIED")]
    [InlineData("charging", "BATTERY_POLICY_NOT_SATISFIED")]
    public void BothChainsGiveTheSameReasonForTheFactsTheyShare(string fault, string reason)
    {
        DispatchVehicleFacts facts = Break(Idle(), fault);

        Assert.Equal(reason, InTransitVehicleFactsCriterion.Evaluate(facts, Options()));
        Assert.Equal(reason, VehicleDynamicFactsCriterion.Evaluate(facts, Options()));
    }

    private static JourneyRuntimeOptions Options() =>
        new() { MapIdentity = Map, MinimumBatteryPercent = 30, MaximumEvidenceAge = TimeSpan.FromSeconds(30) };

    /// <summary>一辆停着等活的车：RIoT 那一侧是 <c>IDLE</c>、速度零、没有订单。</summary>
    private static DispatchVehicleFacts Idle() =>
        new(
            VehicleKey,
            "agv02",
            new OnboardDispatchFacts(
                SessionGeneration: 7,
                AvailableSlots: [3, 4],
                DepartureSafe: true,
                VehicleStopped: true,
                AllTargetSlotsLocked: true,
                AllUnlockOutputsReset: true,
                UnknownPresent: false),
            new RiotVehicleObservation(VehicleKey, true, true, "IDLE", Map, 12, 90, "NO_CHARGE", 0, Now),
            Now);

    /// <summary>
    /// 一辆正在跑的车：那几项一起变，因为现实里它们是一起发生的。
    /// </summary>
    /// <remarks>
    /// <c>VehicleStopped</c> 与 <c>AllTargetSlotsLocked</c> 跟着变：车载端那一侧也知道自己在动、
    /// 下一站的仓位还没锁。在途链两项都不问，但把它们留成空闲车的值，就又造出了一份现实里不会有的事实。
    /// </remarks>
    private static DispatchVehicleFacts UnderWay()
    {
        DispatchVehicleFacts idle = Idle();
        return With(
            idle with
            {
                Onboard = idle.Onboard! with { VehicleStopped = false, AllTargetSlotsLocked = false },
            },
            observation => observation with
            {
                ProcState = "RUNNING",
                Speed = 0.82,
                LockStatus = 1,
                OrderTaskId = "RIOT-TASK-9001",
            });
    }

    /// <summary>把两条链<b>都</b>问的那几项里的一项弄坏。</summary>
    private static DispatchVehicleFacts Break(DispatchVehicleFacts facts, string fault) =>
        fault switch
        {
            "departureUnsafe" => facts with { Onboard = facts.Onboard! with { DepartureSafe = false } },
            "unknownPresent" => facts with { Onboard = facts.Onboard! with { UnknownPresent = true } },
            "unlockOutputsNotReset" => facts with { Onboard = facts.Onboard! with { AllUnlockOutputsReset = false } },
            "onboardFactsMissing" => facts with { Onboard = null },
            "disconnected" => With(facts, observation => observation with { Connected = false }),
            "bindingMismatch" => With(facts, observation => observation with { VehicleKey = "BROKERX-0002" }),
            "wrongMap" => With(facts, observation => observation with { CurrentMap = "MAP-25" }),
            "staleObservation" => With(facts, observation => observation with { ObservedAt = Now.AddMinutes(-5) }),
            "batteryUnknown" => With(facts, observation => observation with { BatteryPercent = null }),
            "batteryTooLow" => With(facts, observation => observation with { BatteryPercent = 12 }),
            "charging" => With(facts, observation => observation with { BatteryState = "CHARGING" }),
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, "unknown fault"),
        };

    private static DispatchVehicleFacts With(
        DispatchVehicleFacts facts,
        Func<RiotVehicleObservation, RiotVehicleObservation> change) =>
        facts with { Vehicle = change(facts.Vehicle) };
}
