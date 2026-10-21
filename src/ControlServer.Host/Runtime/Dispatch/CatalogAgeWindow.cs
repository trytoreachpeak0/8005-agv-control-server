using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 目录时间窗（REQ-0366，CP-0020，control-server#827）的唯一定义处：一条候选此刻该不该因为「首次读到太久」而不派。
/// </summary>
/// <remarks>
/// <para>
/// <b>起点是 MesIngest 首次读到这条需求的时刻</b>，即目录项的 <c>createdAt</c>，与等待年龄（REQ-0201，<see cref="TaskStarvation"/>）
/// 同一个起点，不是 MES DATES：DATES 照旧不参与派车。同一个 <c>DemandId</c> 的 <c>createdAt</c> 不变，所以一经超窗，无论 MES 怎么更新字段
/// 都不会回到窗内；离开目录后同键再现的是新 <c>DemandId</c>、新的首次读到时刻，按它重新起算。
/// </para>
/// <para>
/// <b>只挡未受理的。</b>已受理的在链上第 10 道就停了，到不了这里；已释放、等着改派的需求（REQ-0328，放回待派、仍为已受理，
/// CP-0020 新增项一说明 14）不在轮次的已受理集合里，会走到这里，所以按受理状态放过：它在 <see cref="DispatchRoundFacts.RedispatchGenerations"/>
/// 里，那正是 <c>DemandJourneyLookup.ReleasedForRedispatch</c> 的同一批。
/// </para>
/// <para>
/// <b>边界：恰好等于时间窗不挡</b>（条文「已超过目录时间窗的」）。钟差的口径与等待年龄相同：年龄 = 本服务端此刻 − MesIngest 写下的
/// <c>createdAt</c>，今天两者同在 factory01、同一个钟；MesIngest 的钟快过本服务端时年龄为负，按窗内算。
/// </para>
/// <para>
/// 三处共用：准入判据（开轮时刻）、受理前重读目录之后（受理那一刻，<c>DispatchRoundRunner</c>）、防饥饿排除集合（<see cref="StarvationExclusions"/>）。
/// </para>
/// </remarks>
public static class CatalogAgeWindow
{
    /// <summary>
    /// 这条候选在 <paramref name="at"/> 时刻按 <paramref name="round"/> 的时间窗被挡的原因码；不挡时为空。
    /// </summary>
    public static string? Refusal(DispatchRoundFacts round, AcceptedDemandSnapshot demand, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(demand);
        if (round.RedispatchGenerations.ContainsKey(demand.DemandId))
        {
            return null;
        }
        if (!TaskStarvation.HasLocalCreation(demand))
        {
            return DispatchReasonCodes.DemandCatalogCreatedAtMissing;
        }
        return at - demand.CreatedAt > round.CatalogAgeWindow ? DispatchReasonCodes.DemandOlderThanCatalogWindow : null;
    }
}
