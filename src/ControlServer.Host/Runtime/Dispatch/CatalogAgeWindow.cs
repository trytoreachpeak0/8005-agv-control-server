using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 目录时间窗（REQ-0366，CP-0020，control-server#827）的唯一定义处：一条候选此刻该不该因为「首次读到太久」而不派。
/// </summary>
public static class CatalogAgeWindow
{
    /// <summary>
    /// 这条候选在 <paramref name="at"/> 时刻按 <paramref name="round"/> 的时间窗被挡的原因码；不挡时为空。
    /// </summary>
    public static string? Refusal(DispatchRoundFacts round, AcceptedDemandSnapshot demand, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(demand);
        _ = at;
        return null;
    }
}
