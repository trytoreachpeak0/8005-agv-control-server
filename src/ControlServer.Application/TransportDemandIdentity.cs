using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// 「是不是同一件活」的身份：业务键 <c>TransportDemandKey</c>（<c>{sublot}|{workType}</c>，与 MesIngest 同义、不改）加上「程」
/// <c>TransportLeg</c>（control-server#911）。受理唯一、按身份永久抑制（REQ-0155）与完成事实都按它，而不再只按业务键。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有「程」。</b>mes-ingest#15 给 <c>STAGING_TO_WIRE</c> 加了工序「焊线2」以后，同一个子批会先后两次走这一类任务：先送「焊线」
/// 机台，机台做完再送「焊线2」机台。两程的业务键相同，而只按业务键判身份时，第二程永远被第一程的受理行（第 12 道）或抑制行（第 11 道）挡住，
/// 送到时还会撞上完成事实的主键。用户 2026-11-23 定：业务键不动，按工序分程，压制按程算——焊线那程取消不影响焊线2 那程。
/// </para>
/// <para>
/// <b>只有 <see cref="LegSplitWorkTypes"/> 里的任务类型分程，程取受理时 MES 的 <c>STEP</c> 原文；其余任务类型的程恒为空串</b>，
/// 身份退化为业务键本身，与本票之前逐字相同。这个清单是显式的：MES 以后给别的任务类型加工序，要开票改这里，不会悄悄生效。
/// 分程不是「按 STEP 猜方向」（REQ-0184 禁的是那个）——方向仍只由任务类型定，STEP 只用来区分同一任务类型里的先后两程。
/// </para>
/// <para>
/// <b>同一程重复下发、换代、取消后再现仍按原样去重</b>：MES 对同一程再发（新 <c>DemandId</c>、generation 加一）时 <c>STEP</c> 不变，
/// 身份相同。同一个 <c>STEP</c> 走两次（例如返工）仍被挡，与本票之前一样。
/// </para>
/// <para>
/// <b>分程类型的 <c>STEP</c> 为空时程为空串，不抛。</b>MesIngest 在 <c>STEP</c> 为空时不让外读（REQ 词汇表 LiveMesFieldSet），所以正常
/// 走不到；走到了也只是这一条与同键其他空程的需求互相去重，不会让整轮派车失败。
/// </para>
/// </remarks>
public readonly record struct TransportDemandIdentity(string TransportDemandKey, string TransportLeg)
{
    /// <summary>不分程的任务类型的程，也是迁移给既有行的缺省值。</summary>
    public const string NoLeg = "";

    /// <summary>按 MES <c>STEP</c> 分程的任务类型。只有 <c>STAGING_TO_WIRE</c>（control-server#911，用户 2026-11-23 批准）。</summary>
    public static IReadOnlySet<string> LegSplitWorkTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { TransportTaskTypes.StagingToWire };

    /// <summary>这个任务类型、这个 <c>STEP</c> 的程。</summary>
    public static string LegOf(string workType, string? step)
    {
        ArgumentNullException.ThrowIfNull(workType);
        return LegSplitWorkTypes.Contains(workType) && !string.IsNullOrWhiteSpace(step) ? step : NoLeg;
    }

    /// <summary>目录里这条需求的身份：业务键原样，程按它的任务类型与此刻的 <c>STEP</c>。</summary>
    public static TransportDemandIdentity Of(AcceptedDemandSnapshot demand)
    {
        ArgumentNullException.ThrowIfNull(demand);
        // 任务类型列缺省为空串的快照（只在测试里）按业务键读回任务类型：两者在目录适配器里同源。
        string workType = demand.WorkType.Length > 0
            ? demand.WorkType
            : TransportDemandKeys.WorkTypeOf(demand.TransportDemandKey) ?? string.Empty;
        return new(demand.TransportDemandKey, LegOf(workType, demand.LiveMesFields?.Step));
    }

    public override string ToString() =>
        TransportLeg.Length == 0 ? TransportDemandKey : $"{TransportDemandKey} [{TransportLeg}]";
}
