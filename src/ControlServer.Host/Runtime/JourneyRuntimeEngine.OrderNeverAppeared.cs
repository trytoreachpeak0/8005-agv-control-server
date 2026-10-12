using ControlServer.Application;
using ControlServer.Host.Runtime.Charging;

namespace ControlServer.Host.Runtime;

// control-server#611：「建单发出过、结果未知，RIoT 一直查无此单」的那组证据，三种非业务移动共用一份判定——充电单（control-server#404）、
// 清桩开往等待点（control-server#409）、空闲返回（control-server#611）。
public sealed partial class JourneyRuntimeEngine
{
    /// <summary>证据齐全时答的东西：读到「车证明停稳、没单」的时刻，连续查无此单的时长，车此刻所在的站。</summary>
    private sealed record OrderNeverAppearedEvidence(DateTimeOffset ReadAt, TimeSpan Absent, int? StationId);

    /// <summary>
    /// 一张建单已发出、结果未知的单，是否可以判「从没出现过」而放弃：全部成立答证据，缺任何一项答空、这一轮什么也不做。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>判据，全部要满足</b>（control-server#404 独立审查 M2(b)、增量审查 M-A 定下的那一组，这里原样抽出、不另写一份）：
    /// </para>
    /// <list type="number">
    /// <item>RIoT 对这个 <c>upperId</c> 明确答「查无此单」：HTTP 404，或真实 RIoT 的答法——HTTP 200、业务码 0、不带 result、没有失败类别
    /// （<see cref="RiotOrderObservation.IsExactAbsentAtObservation"/>）。读不到、读到别的、或带失败类别的 Unknown，连续性断了，重新计时。</item>
    /// <item>连续满 <c>JourneyRuntime:ChargingOrderAbsentAbandonAfter</c>（默认 120 秒）。计时在进程内存里，重启从头计，只会让放弃更晚。</item>
    /// <item>车证明停稳、没有任务号（<see cref="ProvenStoppedVehicleAsync"/>）。</item>
    /// <item>RIoT 的未完成订单清单读全了，里面没有这辆车的单（排队中的也算）。</item>
    /// </list>
    /// <para>
    /// <b>为什么不会重复建单</b>：这里只判「放弃」，从不建单。放弃之后若再出发，是下一趟新的承诺、新的 <c>upperId</c>；旧单号不会再发建单请求。
    /// 那张单事后若在 RIoT 上冒出来，各调用方有自己的兜底（充电单被取消，空闲返回单挡车并交人）。
    /// </para>
    /// <para>
    /// 计时板借用充电分配的 <see cref="ChargingAllocationBoard"/>：按 <c>upperId</c> 记，三种单的 <c>upperId</c> 前缀互不相同，不会串。调用方在收尾之后
    /// 调 <see cref="ChargingAllocationBoard.SeenOrUnread"/> 把这一条丢掉。
    /// </para>
    /// </remarks>
    private async Task<OrderNeverAppearedEvidence?> OrderNeverAppearedAsync(
        string upperId,
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ChargingAllocationBoard board = dispatchRound.Charging.Board;
        RiotOrderObservation observed;
        try
        {
            observed = await vehicleFacts.ReconcileByUpperIdAsync(upperId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            board.SeenOrUnread(upperId);
            return null;
        }

        if (observed.Kind != RiotOrderObservationKind.NotFound && !observed.IsExactAbsentAtObservation(upperId))
        {
            board.SeenOrUnread(upperId);
            return null;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        TimeSpan absent = now - board.AbsentSince(upperId, now);
        if (absent < runtimeOptions.ChargingOrderAbsentAbandonAfter)
        {
            return null;
        }

        if (await ProvenStoppedVehicleAsync(vehicleKey, cancellationToken).ConfigureAwait(false) is not { } stopped)
        {
            return null;
        }

        bool noUnfinishedOrder;
        try
        {
            noUnfinishedOrder = await dispatchRound.Charging.Occupancy
                .VehicleHasNoUnfinishedOrderAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        return noUnfinishedOrder ? new OrderNeverAppearedEvidence(stopped.ReadAt, absent, stopped.Vehicle.CurrentStationId) : null;
    }
}
