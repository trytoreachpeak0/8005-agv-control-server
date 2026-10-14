namespace ControlServer.Host.Runtime;

/// <summary>
/// 一条 MES 需求 v2 不会再执行、货留在机台上要人去处理时，看板上那一行告警的原因码（control-server#703）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要有这一行。</b>v2 对 MesIngest 只读、不回写：需求在本地终结之后，它在 MES 目录里一直挂着，而终结码按业务键抑制、
/// 又被排除在饥饿告警之外（<see cref="Dispatch.StarvationExclusions"/>）。不写这一行，就没有任何地方告诉人「这批料没人搬了」。
/// </para>
/// <para>
/// <b>写在结构性派车阻断表里</b>（<c>StructuralDispatchBlocks</c>，看板派车积压页的 <c>structuralBlocks</c>），不加表：那一行的含义
/// 本来就是「不会派车，请人工搬运」，按需求与原因码去重。与派车判据得出的阻断不同，这几个码不是任何判据的结论，派车轮末的写入器
/// （<see cref="Dispatch.StructuralDispatchBlockSink"/>）只在需求离开 MES 目录时清它们——离开目录说明 MES 那边已经关了这件活。
/// </para>
/// </remarks>
public static class ManualCarryAlarms
{
    /// <summary>同一条需求因为站点超时已放回过 <see cref="JourneyRuntimeOptions.StationTimeoutRedispatchLimit"/> 次，这一次不再放回。</summary>
    public const string StationTimeoutRedispatchLimitReached = "STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED";

    /// <summary>站点超时的这条是旅程的锚需求、旅程上还有别的需求，按释放规则放不回去。</summary>
    public const string StationTimeoutAnchorNotReleasable = "STATION_TIMEOUT_ANCHOR_NOT_RELEASABLE";

    /// <summary>确定的装货失败：部分仓位可能已装上，货的去向要人判断，不放回。</summary>
    public const string DeterminateLoadFailureNeedsManual = "DETERMINATE_LOAD_FAILURE_NEEDS_MANUAL";

    /// <summary>全部码。派车轮末的写入器按这个集合认出「只在离开目录时清」的那几行。</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        StationTimeoutRedispatchLimitReached,
        StationTimeoutAnchorNotReleasable,
        DeterminateLoadFailureNeedsManual,
    };
}
