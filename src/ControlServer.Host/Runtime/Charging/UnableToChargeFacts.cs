using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Charging;

/// <summary>
/// 「已确认充不上」由严格系统事实自动形成（<c>REQ-0174</c>；批次9-08，control-server#406）：这一轮读到的事实哪几项不成立。
/// 只判，不读、不写——引擎读好事实交来，答缺了哪几项。
/// </summary>
/// <remarks>
/// <para>
/// <b>全部成立才形成</b>，缺任何一项都不形成、也不暂停桩（<c>REQ-0175</c>）：车辆、订单、预占与目标桩身份一致；车停在精确的 RIoT 地图加站点上；
/// 订单里有开始充电的动作 <c>act(78,1)</c>，它的结果码是 <see cref="VerifiedFailureCode"/>；订单此刻是 <c>HANG</c>（9），而且订单观测与
/// 任务明细两处读到的都是它；这个周期里本服务端从没读到过 <c>batteryState=CHARGING</c>，此刻也不是；车辆读数与任务明细都新鲜。
/// 「连续」与「最终」由调用方再加一层：同一组事实要在两次相隔不超过 <c>JourneyRuntime:MaximumEvidenceAge</c> 的新鲜观测里都成立
/// （<see cref="ChargingAllocationBoard.ContinuesSampleRun"/>，以 <see cref="ContinuityKey"/> 为键）——一次读到的 <c>HANG</c> 不当作最终。
/// </para>
/// <para>
/// <b>只认 407802</b>（调度 09-29，PR #414 审查）：「<c>HANG</c> 加任何非 0 码」不是充不上。自由文本 <c>resultStr</c> 从不读（<c>REQ-0175</c>），
/// 网关也不带它（<see cref="RiotOrderMissionFact.ResultCode"/>）。这个事实不对车或桩归责，暂停的根因写 <c>UNKNOWN</c>。
/// </para>
/// <para>
/// <b>「全程无 CHARGING」只能是本服务端采过样的全程</b>：到桩之前引擎不采电量状态，所以这里看的是周期上记下的
/// <c>FirstChargingSeenAt</c>、判定窗口内的每一次读数（读到一次 <c>CHARGING</c> 就打断连续性，调用方负责）与此刻的读数。
/// </para>
/// </remarks>
public static class UnableToChargeFacts
{
    /// <summary>
    /// 经目标 RIoT build／契约验证过的那一个「充不上」失败码（<c>REQ-0174</c>：当前证据为 407802；白名单第 1.2 节、批次9-03 读出）。
    /// </summary>
    public const int VerifiedFailureCode = 407802;

    /// <summary>
    /// <see cref="VerifiedFailureCode"/> 是在哪一个 RIoT build 上验证的（白名单「绑定环境」<c>RIOT-8005-RUNTIME</c>）。暂停事件照这个记，
    /// 不是此刻从 RIoT 现读的版本——服务端今天没有读 RIoT build 的调用。
    /// </summary>
    public const string VerifiedRiotBuild = "v2.2.0.14";

    /// <summary><see cref="VerifiedFailureCode"/> 所依据的 RIoT 契约快照（白名单「绑定契约快照」）。同上，不是现读。</summary>
    public const string VerifiedRiotContract = "RIOT-OPENAPI-8005-202607-EARLY-01";

    /// <summary>RIoT 的充电动作号与「开始充电」参数（白名单第 1.2 节形态二）。</summary>
    public const int ChargeActionId = 78;

    /// <inheritdoc cref="ChargeActionId"/>
    public const int StartChargingParam = 1;

    // ---- 缺了哪一项（判定的结论，也写进暂停事件之前的日志）----

    public const string IdentityMismatch = "IDENTITY_MISMATCH";
    public const string ReservationNotThisJourney = "RESERVATION_NOT_THIS_JOURNEY";
    public const string NotAtExactStation = "NOT_AT_EXACT_STATION";
    public const string ChargeActionNotExecuted = "CHARGE_ACTION_NOT_EXECUTED";
    public const string FailureCodeNotVerified = "FAILURE_CODE_NOT_VERIFIED";
    public const string OrderNotFinalHang = "ORDER_NOT_FINAL_HANG";
    public const string ChargingSeen = "CHARGING_SEEN";
    public const string FactsStale = "FACTS_STALE";
    public const string FactsConflict = "FACTS_CONFLICT";

    /// <summary>判定的「连续」那一层在板上的键。</summary>
    public static string ContinuityKey(string cycleId) => "unable-to-charge:" + cycleId;

    /// <summary>
    /// 这一轮的事实里不成立的那几项；空即全部成立（调用方另要求连续）。
    /// </summary>
    /// <param name="reservationHolderJourneyId">这个桩此刻的独占行属于哪趟旅程；没有独占行为空。</param>
    public static IReadOnlyList<string> Missing(
        JourneyRuntimeRow runtime,
        JourneyStopRow stop,
        string? intentOrderId,
        DateTimeOffset? firstChargingSeenAt,
        string? reservationHolderJourneyId,
        RiotOrderObservation order,
        RiotOrderMissionFacts missions,
        RiotVehicleObservation? vehicle,
        DateTimeOffset now,
        TimeSpan maximumEvidenceAge)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(missions);

        List<string> missing = [];
        bool Fresh(DateTimeOffset observedAt) => observedAt <= now && now - observedAt <= maximumEvidenceAge;

        // Identity: the order is the one this journey sent, for this vehicle, to this charger on this map; the mission detail is
        // about the same order; the charger's reservation is this journey's.
        if (string.IsNullOrWhiteSpace(intentOrderId) ||
            order.OrderId != intentOrderId ||
            order.VehicleKey != runtime.VehicleKey ||
            order.MapId != runtime.MapId ||
            order.DestinationStationId != stop.StationRiotId ||
            missions.Status != RiotOrderMissionFactsStatus.Found ||
            missions.OrderId != intentOrderId)
        {
            missing.Add(IdentityMismatch);
        }
        if (!string.Equals(reservationHolderJourneyId, runtime.JourneyId, StringComparison.Ordinal))
        {
            missing.Add(ReservationNotThisJourney);
        }

        if (vehicle is not
            {
                Connected: true,
                CurrentStationId: int current,
            } ||
            current != stop.StationRiotId ||
            !string.Equals(vehicle.CurrentMap, runtime.MapIdentity, StringComparison.Ordinal) ||
            vehicle.Speed is not 0d)
        {
            missing.Add(NotAtExactStation);
        }

        RiotOrderMissionFact[] moves =
        [
            .. missions.Missions.Where(mission =>
                string.Equals(mission.Type, "move", StringComparison.OrdinalIgnoreCase) &&
                mission.MapId == runtime.MapId && mission.Destination == stop.StationRiotId),
        ];
        RiotOrderMissionFact[] starts =
        [
            .. missions.Missions.Where(mission =>
                string.Equals(mission.Type, "act", StringComparison.OrdinalIgnoreCase) &&
                mission.ActionId == ChargeActionId && mission.ActionParam1 == StartChargingParam),
        ];
        if (moves.Length == 0 || starts.Length != 1)
        {
            missing.Add(ChargeActionNotExecuted);
        }
        else if (starts[0].ResultCode != VerifiedFailureCode)
        {
            missing.Add(FailureCodeNotVerified);
        }

        bool observedHang = order is { Kind: RiotOrderObservationKind.Active, OrderState: RiotOrderState.Hang };
        bool detailHang = missions.OrderState == RiotOrderState.Hang;
        if (!observedHang && !detailHang)
        {
            missing.Add(OrderNotFinalHang);
        }
        else if (observedHang != detailHang)
        {
            // Two reads of the same order disagree on whether it hangs: neither is taken.
            missing.Add(FactsConflict);
        }

        if (firstChargingSeenAt is not null ||
            string.Equals(vehicle?.BatteryState, BatteryEligibility.ChargingBatteryState, StringComparison.Ordinal))
        {
            missing.Add(ChargingSeen);
        }

        if (vehicle is null || !Fresh(vehicle.ObservedAt) || !Fresh(missions.ObservedAt))
        {
            missing.Add(FactsStale);
        }

        return missing;
    }
}
