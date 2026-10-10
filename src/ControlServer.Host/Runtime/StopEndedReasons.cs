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
        "CANCELLED_BY_OPERATOR" => "LOAD_CANCELLED",
        "CANCELLED_BY_LOAD_COMPENSATION" => "LOAD_COMPENSATED",
        "TERMINATED_BY_FAULT_CARGO_HANDOFF" => "CARGO_HANDED_OFF",
        DemandJourneyLookup.ReleasedForRedispatchReason => "DEMAND_RELEASED",
        VehicleFaultRecoveryService.TripTerminatedReason => "TRIP_TERMINATED",
        _ => throw new ArgumentOutOfRangeException(
            nameof(reasonCode),
            reasonCode,
            "No stop-ended reason is defined for this reason code; add it here together with the protocol value it means.")
    };
}
