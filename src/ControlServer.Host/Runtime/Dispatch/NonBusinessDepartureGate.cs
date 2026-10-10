using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 非业务移动出发前安全门里，派车事实（<see cref="Criteria.VehicleNewPurposeReadiness"/>）之外的那几项：会话里逐个仓位的锁态与开锁输出、
/// 当前安全摘要的 <c>reasonCodes</c>、服务端自己对 RIoT 的停稳读数。空闲返回与充电出发前的门（<c>JourneyRuntimeEngine</c>）和充电分配
/// （<c>ChargingAllocator</c>）都经这里读，对同一件事答得一样（control-server#404 增量审查 S-a）。
/// </summary>
/// <remarks>
/// 分配侧不判这几项时，一辆仓位没锁好的车会被承诺一个桩、在门前等满撤回时限、撤回、下一轮又被承诺——单车时每约 45 秒循环一次，
/// 每一圈都给车发一对快照。
/// </remarks>
public static class NonBusinessDepartureGate
{
    /// <summary>
    /// 会话一半：这一代会话就绪、安全状态读得到，最新一张 <c>SafetyStateSnapshot</c> 里八个仓位都 <c>LOCKED</c>／<c>RESET</c>，当前摘要的
    /// <c>reasonCodes</c> 为空（<see cref="OnboardDispatchFactsReader.ReadDepartureSafetyGapsAsync"/>）。空表即放行。
    /// </summary>
    public static async Task<IReadOnlyList<string>> SessionGapsAsync(
        OnboardDispatchFactsReader onboardFacts,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onboardFacts);
        SessionRecoveryRow? session = await onboardFacts
            .CurrentReadySessionAsync(agvId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return ["ONBOARD_SESSION_NOT_READY"];
        }

        if (session.SafetyRevision is not long safetyRevision)
        {
            return ["SAFETY_STATE_UNREADABLE"];
        }

        return await onboardFacts
            .ReadDepartureSafetyGapsAsync(agvId, session.SessionGeneration, safetyRevision, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// RIoT 一半：服务端自己的安全读数说车停着、什么也没挡着。不是停止时答 RIoT 给的原因（没有就答 <c>RIOT_VEHICLE_NOT_STOPPED</c>）。
    /// 读失败照原样抛出：调用方把它记成 <c>RIOT_VEHICLE_SAFETY_UNREADABLE</c>（<see cref="IsUnreadable"/>）。
    /// </summary>
    public static async Task<IReadOnlyList<string>> RiotStandstillGapsAsync(
        IRiotVehicleSafetyFacts vehicleSafety,
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicleSafety);
        RiotVehicleSafetyObservation safety = await vehicleSafety
            .ReadVehicleSafetyAsync(vehicleKey, cancellationToken).ConfigureAwait(false);
        return safety.MotionState == RiotVehicleMotionState.Stopped
            ? []
            : safety.ReasonCodes.Count > 0 ? safety.ReasonCodes : ["RIOT_VEHICLE_NOT_STOPPED"];
    }

    /// <summary>对 RIoT 的一次读失败了（不是本轮被取消）：答不出来的一律算不能出发。</summary>
    public static bool IsUnreadable(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or InvalidDataException or TaskCanceledException &&
        !cancellationToken.IsCancellationRequested;

    /// <summary>RIoT 那一半读失败时写的码。</summary>
    public const string RiotSafetyUnreadable = "RIOT_VEHICLE_SAFETY_UNREADABLE";
}
