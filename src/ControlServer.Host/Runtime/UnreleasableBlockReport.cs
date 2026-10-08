using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 服务起来时数一遍停在不可放行阻塞码上的旅程（<see cref="BlockedJourneyRelease.IsUnreleasable"/>），有就记一条 Warning
/// （control-server#505）。只读，不改任何行。
/// </summary>
/// <remarks>
/// 这两种码不会自动放行：两条放行路径都不放，之后的异常处置对上了也不放车；只有旅程收尾（<c>JourneyClosure</c>）结束它——经
/// <c>PickupStopTermination</c> 终结最后一条需求、释放服务释放最后一条待装需求、人放弃停住的一趟。升级时一次性数据迁移把旧的普通 <c>*_NOT_RECONCILED</c> 改成其中一种，
/// 现场要在升级之后立刻知道有几趟，而不是等某一辆车被发现一直不动。
/// </remarks>
internal static class UnreleasableBlockReport
{
    private static readonly Action<ILogger, int, int, int, Exception?> LogUnreleasableBlocks =
        LoggerMessage.Define<int, int, int>(
            LogLevel.Warning,
            new EventId(2138, nameof(LogUnreleasableBlocks)),
            "{Total} journey(s) are blocked under a code no release path lifts: {OnEndedDemand} ending in " +
            "_NOT_RECONCILED_ON_ENDED_DEMAND and {BeforeUpgrade} ending in _NOT_RECONCILED_BEFORE_UPGRADE. None is released " +
            "to the runtime again; only its journey's closing ends it, so each holds its vehicle until then (control-server#505).");

    /// <summary>两种不可放行阻塞各有几趟旅程停在上面。</summary>
    public static async Task<(int OnEndedDemand, int BeforeUpgrade)> CountAsync(
        ControlServerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string[] codes = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage == JourneyRuntimeStage.Blocked && row.BlockReasonCode != null)
            .Select(row => row.BlockReasonCode!)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return (
            codes.Count(code => code.EndsWith(BlockedJourneyRelease.OnEndedDemandSuffix, StringComparison.Ordinal)),
            codes.Count(code => code.EndsWith(BlockedJourneyRelease.BeforeUpgradeSuffix, StringComparison.Ordinal)));
    }

    /// <summary>有就记一条 Warning，写清总数与两种各多少；没有就什么都不记。</summary>
    public static async Task LogAsync(ControlServerDbContext dbContext, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        (int onEndedDemand, int beforeUpgrade) = await CountAsync(dbContext, cancellationToken).ConfigureAwait(false);
        if (onEndedDemand + beforeUpgrade > 0)
        {
            LogUnreleasableBlocks(logger, onEndedDemand + beforeUpgrade, onEndedDemand, beforeUpgrade, null);
        }
    }
}
