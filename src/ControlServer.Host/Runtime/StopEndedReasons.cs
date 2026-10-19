using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime;

/// <summary>
/// v3 的 <c>CurrentStopWorklistSnapshot.stopEndedReason</c>：让一站结束的那个服务端原因码，换成车上给操作员看的站级原因
/// （control-server#382）。映射只在这里，一处。
/// </summary>
/// <remarks>
/// <para>
/// 对照表是 <c>8005-agv-program</c> PR #163 正文第 3 项（<c>fp/v2-impl@0c0edfa5</c> 实读的九条来路）。空清单只从两个出口发：
/// 旅程收尾（<see cref="JourneyClosure"/>）与一站结束而旅程继续（<see cref="StopEndWorklist"/>），两处都拿终结那条需求的原因码
/// 来问这里。取值是站级原因，不与需求终态码一一对应：期限到期与确定的装货失败都以 <c>CANCELLED_BY_STATION_TIMEOUT</c> 终结，
/// 共用 <c>STATION_DEADLINE_EXPIRED</c>；扫码前取消与已下命令后的取消共用 <c>LOAD_CANCELLED</c>。一站多条需求时，原因取结束
/// 最后一条待做项的那条来路——两个出口都在那一次终结里被调用，自然如此。
/// </para>
/// <para>
/// <b>未知码抛出，不给兜底值。</b>协议把空清单的原因定为必填，兜底值会让一条新来路悄悄显示成别的原因。抛出来，那条新来路的第一条
/// 测试就会红。今天每条来路的码都在下表里，<c>StopEndedReasonsTests</c> 逐条按值断言。
/// </para>
/// <para>
/// <b>新增 <see cref="PickupStopTermination"/> 或 <see cref="JourneyClosure"/> 的调用方——包括从 <c>fp/v2-impl</c> merge 进批次分支带来的——
/// 要回到这里补它的原因码。</b>未知码在暂存收尾快照时抛出，整次终结随之回滚，下一轮再来一遍，那一站就结束不了；没有测试走到那条来路时，
/// 这件事只会在车上显出来。
/// </para>
/// <para>
/// <c>CANCELLED_BY_STOP_COMPLETE</c> 不在表里：它今天在 <c>src/</c> 里没有生产者（见
/// <see cref="PickupStopTermination.KeySuppressingReasonCodes"/> 的注释），协议的七个取值里也没有与它对应的一项。谁产生它，谁回到这里
/// 与协议一起定它说什么。
/// </para>
/// <para>
/// 持货期限截断（control-server#290）用自己的码 <see cref="JourneyRuntimeEngine.CargoHoldingTruncationReason"/>：需求被释放回积压，
/// 不终结，所以不借需求终态码。车上说 <c>DEMAND_RELEASED</c>（「本站因任务释放结束」），与释放改派
/// （<see cref="DemandJourneyLookup.ReleasedForRedispatchReason"/>）同一个取值：这一站上的需求确实是被放回待派队列了，v2 会改派。
/// 说成 <c>STATION_DEADLINE_EXPIRED</c>（「本站因超时结束」）会让现场把它当成站点期限丢单，去 MES 重提或手工处理物料，与改派重复
/// （cs#290 审查 M2）。两个值都在协议七值里，换码不改协议。
/// </para>
/// </remarks>
internal static class StopEndedReasons
{
    /// <summary>
    /// 终结的原因码对应的站级原因。<paramref name="reasonCode"/> 为 null 表示正常卸完、旅程完成。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">没人为它定过站级原因的码。</exception>
    public static string ForEnding(string? reasonCode) => reasonCode switch
    {
        null => "COMPLETED",
        "CANCELLED_BY_STATION_TIMEOUT" => "STATION_DEADLINE_EXPIRED",
        JourneyRuntimeEngine.CargoHoldingTruncationReason => "DEMAND_RELEASED",
        // control-server#703：站点期限到期、本站没录入，需求放回待派、会重新派车，所以说 DEMAND_RELEASED（与释放改派同一项）。
        // 说 STATION_DEADLINE_EXPIRED 会让操作员以为这单丢了；那一项留给放回次数用完、真的不再派的终结（CANCELLED_BY_STATION_TIMEOUT）。
        DemandJourneyLookup.ReleasedAtStationDeadlineReason => "DEMAND_RELEASED",
        "CANCELLED_BY_OPERATOR" => "LOAD_CANCELLED",
        "CANCELLED_BY_LOAD_COMPENSATION" => "LOAD_COMPENSATED",
        "TERMINATED_BY_FAULT_CARGO_HANDOFF" => "CARGO_HANDED_OFF",
        DemandJourneyLookup.ReleasedForRedispatchReason => "DEMAND_RELEASED",
        VehicleFaultRecoveryService.TripTerminatedReason => "TRIP_TERMINATED",
        // 人工撤下（control-server#771）：车上没装的需求都放回待派、会重新派车，与释放改派说同一句。
        Withdrawal.JourneyWithdrawal.WithdrawnReason => "DEMAND_RELEASED",
        // 协议 4.0.0 候选（control-server#713，wire-notes「Staging pickup stops」第 4 条）：待送点那一站因客户出发或车缺电而结束、
        // 没装的放回待派时，空清单说的是这两个，不说 DEMAND_RELEASED。
        CurrentStopDepartures.OperatorDepartedReason => "OPERATOR_DEPARTED",
        CurrentStopDepartures.ChargingRequiredReason => "CHARGING_REQUIRED",
        // 待送点上装货中取消（control-server#713）：需求放回待派、不终结，车上说的仍是操作员按的那件事。
        CurrentStopDepartures.LoadCancelledReleasedReason => "LOAD_CANCELLED",
        _ => throw new ArgumentOutOfRangeException(
            nameof(reasonCode),
            reasonCode,
            "No stop-ended reason is defined for this reason code; add it here together with the protocol value it means.")
    };
}
