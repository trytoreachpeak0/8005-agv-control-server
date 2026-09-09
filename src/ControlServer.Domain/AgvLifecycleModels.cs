namespace ControlServer.Domain;

/// <summary>
/// 一台车的生命周期事实，业务可用性由它派生。
/// </summary>
/// <remarks>
/// 两个正交维度加一个标志位：<see cref="Archived"/>（在不在档）、
/// <see cref="LifecycleGeneration"/>（第几个代次）、<see cref="Commissioned"/>（有没有被投运）。
/// **业务可用性不在这里**——它是 <see cref="BusinessAvailability"/> 算出来的派生量，不是一个可写
/// 状态。所以 REQ-0317 的「检查通过也不自动投运」不需要额外的守卫代码：没有任何一条路能把业务
/// 可用性直接置真。
/// </remarks>
public sealed record AgvLifecycleFacts(
    string AgvId,
    long LifecycleGeneration,
    bool Archived,
    bool Commissioned,
    bool HasCandidateRiotBinding);

/// <summary>
/// REQ-0319 要求的新代次证据：完整、正面。
/// </summary>
/// <remarks>
/// 三项都必须是明确的「是」。<c>null</c> 表示还不知道，而不知道在 fail-closed 的谓词链里等于不通过
/// ——这正是「完整、正面」这四个字要的意思。
/// </remarks>
public sealed record AgvGenerationEvidence(
    bool? RiotBindingConfirmed,
    bool? SlotConfigurationReadinessConfirmed,
    bool? SafetyChainConfirmed);

/// <summary>一台车不业务可用的原因。</summary>
public enum BusinessAvailabilityBlocker
{
    Archived,
    NotCommissioned,
    CandidateRiotBindingMissing,
    GenerationEvidenceIncomplete,
    GenerationEvidenceNegative
}

/// <summary>
/// 业务可用性的 fail-closed 谓词链。
/// </summary>
/// <remarks>
/// 这个类型只有一个静态函数，没有任何可写状态，也没有任何 setter —— 「无任何路径可直接置真」这句
/// 验收标准就是靠这个形状成立的，不是靠一条约定。
/// </remarks>
public static class BusinessAvailability
{
    public static IReadOnlyList<BusinessAvailabilityBlocker> Evaluate(
        AgvLifecycleFacts facts,
        AgvGenerationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evidence);

        List<BusinessAvailabilityBlocker> blockers = [];
        if (facts.Archived)
        {
            blockers.Add(BusinessAvailabilityBlocker.Archived);
        }
        if (!facts.Commissioned)
        {
            blockers.Add(BusinessAvailabilityBlocker.NotCommissioned);
        }
        if (!facts.HasCandidateRiotBinding)
        {
            blockers.Add(BusinessAvailabilityBlocker.CandidateRiotBindingMissing);
        }

        bool?[] items =
        [
            evidence.RiotBindingConfirmed,
            evidence.SlotConfigurationReadinessConfirmed,
            evidence.SafetyChainConfirmed
        ];
        if (items.Any(item => item is null))
        {
            blockers.Add(BusinessAvailabilityBlocker.GenerationEvidenceIncomplete);
        }
        if (items.Any(item => item is false))
        {
            blockers.Add(BusinessAvailabilityBlocker.GenerationEvidenceNegative);
        }
        return blockers;
    }

    public static bool IsAvailable(AgvLifecycleFacts facts, AgvGenerationEvidence evidence) =>
        Evaluate(facts, evidence).Count == 0;
}
