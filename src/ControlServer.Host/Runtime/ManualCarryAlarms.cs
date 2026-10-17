using System.Text.Json;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// 站点离站等待期限到期终结的两个码：看板「站点超时抑制清单」只收这两个（REQ-0365，control-server#751）。
    /// 确定的装货失败终结时记同一个 <c>CANCELLED_BY_STATION_TIMEOUT</c>，但不是站点期限到期，不在其中。
    /// </summary>
    public static IReadOnlySet<string> StationDeadlineEndings { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        StationTimeoutRedispatchLimitReached,
        StationTimeoutAnchorNotReleasable,
    };

    /// <summary>
    /// 暂存这一行告警，不保存：它与终结同一次保存落库，崩在中间不会留下「需求终结了、却没人知道」。同一需求同一个码已有一行时重开它。
    /// </summary>
    public static async Task StageAsync(
        ControlServerDbContext dbContext,
        string demandId,
        string transportDemandKey,
        string reasonCode,
        object detail,
        DateTimeOffset raisedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(transportDemandKey);
        if (!All.Contains(reasonCode))
        {
            throw new ArgumentOutOfRangeException(nameof(reasonCode), reasonCode, "Not a manual-carry alarm code.");
        }
        string detailJson = JsonSerializer.Serialize(detail, DetailSerializerOptions);
        StructuralDispatchBlockRow? row = await dbContext.Set<StructuralDispatchBlockRow>()
            .SingleOrDefaultAsync(candidate => candidate.DemandId == demandId && candidate.ReasonCode == reasonCode, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            dbContext.Set<StructuralDispatchBlockRow>().Add(new StructuralDispatchBlockRow
            {
                DemandId = demandId,
                ReasonCode = reasonCode,
                TransportDemandKey = transportDemandKey,
                FirstRaisedAt = raisedAt,
                LastSeenAt = raisedAt,
                DetailJson = detailJson
            });
            return;
        }
        row.TransportDemandKey = transportDemandKey;
        row.LastSeenAt = raisedAt;
        row.DetailJson = detailJson;
        if (row.ClearedAt is not null)
        {
            row.FirstRaisedAt = raisedAt;
            row.ClearedAt = null;
        }
    }

    private static readonly JsonSerializerOptions DetailSerializerOptions = new(JsonSerializerDefaults.Web);
}
