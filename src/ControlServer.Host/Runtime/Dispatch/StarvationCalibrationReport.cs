using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>一组时长的分布：样本数、中位数、P95、最大值，单位秒；没有样本时三个值都为空。</summary>
public sealed record DurationSummary(int Count, double? MedianSeconds, double? P95Seconds, double? MaxSeconds);

/// <summary>一种站点作业（装或卸）的人工作业耗时分布。</summary>
public sealed record StationOperationSummary(string OperationType, DurationSummary Duration);

/// <summary>一个分区在样本期内的标定证据。</summary>
public sealed record ZoneCalibrationEvidence(
    string DispatchZone,
    DurationSummary FullCycle,
    DurationSummary WaitToBind,
    int VehicleCount,
    int TaskCount,
    IReadOnlyList<StationOperationSummary> StationOperations);

/// <summary>防饥饿阈值的标定证据报表（REQ-0203；批次7-09，control-server#214）。</summary>
public sealed record StarvationCalibrationReport(
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ZoneCalibrationEvidence> Zones);

/// <summary>
/// 从本服务端自己的库里取标定证据：只给数，不套公式、不给建议值（REQ-0203「系统只提供证据」）。
/// </summary>
public static class StarvationCalibrationQuery
{
    /// <summary>一组时长的中位数、P95（最近秩）与最大值。</summary>
    public static DurationSummary Summarize(IEnumerable<TimeSpan> durations)
    {
        ArgumentNullException.ThrowIfNull(durations);
        return new DurationSummary(0, null, null, null);
    }

    /// <summary>样本期 [<paramref name="from"/>, <paramref name="to"/>) 内的报表。</summary>
    public static Task<StarvationCalibrationReport> ReadAsync(
        ControlServerDbContext dbContext,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _ = cancellationToken;
        return Task.FromResult(new StarvationCalibrationReport(from, to, now, []));
    }

    /// <summary>同一份报表的 CSV：一个分区一行。</summary>
    public static string ToCsv(StarvationCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return string.Empty;
    }
}
