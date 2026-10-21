using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 试验机批号规则（REQ-0368，CP-0023，control-server#828）：SUBLOT 自左起第 <see cref="Position"/> 个字符（开头的 <c>Q</c> 算第 1 个）
/// 是 <see cref="Characters"/> 里的某一个字符。
/// </summary>
/// <remarks>
/// <para>
/// <b>按目录原样逐字符比较</b>（REQ-0009）：不去空白、不转半角、不转大小写，按序数比较。所以全角「９」不是 <c>9</c>；
/// 前面多一个空格的批号整体错一位，按错位后的那个字符判；不足该长度的（含空串）不判。
/// </para>
/// <para>
/// <b>配置为空不判</b>：字符位为 0 或取值为空即关。出厂两者都空；生产值第 6 个字符、<c>9</c>，用户 2026-10-21 确认，写在生产实例定义里。
/// 只写一半的配置由 <see cref="JourneyRuntimeOptionsValidator"/> 拒绝启动。
/// </para>
/// </remarks>
public sealed record TrialSublotRule(int Position, string Characters)
{
    /// <summary>关：什么都不判中。</summary>
    public static TrialSublotRule Off { get; } = new(0, "");

    /// <summary>按 <paramref name="options"/> 的两项配置取规则。</summary>
    public static TrialSublotRule From(JourneyRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TrialSublotRule(options.TrialSublotCharacterPosition, options.TrialSublotCharacters ?? "");
    }

    /// <summary>规则开着：字符位为正、取值不空。</summary>
    public bool IsOn => Position > 0 && !string.IsNullOrEmpty(Characters);

    /// <summary><paramref name="sublot"/> 是不是试验批。规则关着、批号为空或不够长时为假。</summary>
    public bool Matches(string? sublot) =>
        IsOn && sublot is not null && sublot.Length >= Position && Characters.Contains(sublot[Position - 1], StringComparison.Ordinal);
}

/// <summary>
/// 一条候选此刻该不该因为是试验机批号而不派（REQ-0368，control-server#828）的唯一定义处。
/// </summary>
/// <remarks>
/// <para>
/// <b>只挡未受理的。</b>已受理的在链上第 10 道就停了，到不了这里；放回待派、仍为已受理的（REQ-0328）不在轮次的已受理集合里，会走到这里，
/// 所以按 <see cref="DispatchRoundFacts.RedispatchGenerations"/> 放过，同 <see cref="CatalogAgeWindow"/>。
/// </para>
/// <para>
/// 四处共用：准入判据（<see cref="Criteria.TrialSublotCriterion"/>）、AREA→EQP 观测（<see cref="Criteria.AreaEqpUniqueCriterion"/>）、
/// 防饥饿排除集合（<see cref="StarvationExclusions"/>）、派车轮的滤除日志。受理前重读目录时不另判：SUBLOT 是重读比对的决策事实之一
/// （<c>DemandIntakeService.HasSameDecisionFacts</c>，<c>src/ControlServer.Application/WireToGateOrchestration.cs</c>），重读出来的批号变了就放弃本次受理，下一轮按新批号经判据判；规则本身一轮只读一次。
/// </para>
/// </remarks>
public static class TrialSublot
{
    /// <summary>这条候选按 <paramref name="round"/> 的规则被挡的原因码；不挡时为空。</summary>
    public static string? Refusal(DispatchRoundFacts round, AcceptedDemandSnapshot demand)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(demand);
        if (round.RedispatchGenerations.ContainsKey(demand.DemandId))
        {
            return null;
        }
        return round.TrialSublotRule.Matches(demand.Sublot) ? DispatchReasonCodes.DemandTrialSublot : null;
    }
}
