using System.Globalization;
using System.Text;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
/// <remarks>
/// <para>口径，一个分区一行：</para>
/// <list type="bullet">
/// <item><b>完整周期</b>：一趟已完成旅程从建立（绑车）到完成（车再次空闲、具备接单资格），含空驶、运输与全部人工装卸。
/// 完成时刻取旅程行进入 <c>Completed</c> 时写下的 <c>UpdatedAt</c>——完成之后引擎不再推进这一行。</item>
/// <item><b>建单至绑车等待</b>：需求的本地建单时刻（积压行 <c>DemandCreatedAt</c>，即 MesIngest 的 <c>CreatedAt</c>，与排序用的
/// 等待年龄同一个起点）到它被绑进旅程（从属需求行 <c>AddedAt</c>，途中追加进来的也算）。没有积压行、或者建单时刻是默认值
/// （MesIngest 没给）的需求只计任务量、不计等待样本。</item>
/// <item><b>车辆数、任务量</b>：样本期内被绑车的需求条数，以及承载它们的旅程涉及的车辆数。</item>
/// <item><b>人工装卸（站点作业耗时）</b>：一次装或卸从下发（<c>StationOperations.CreatedAt</c>）到人工确认提交
/// （<c>CommittedAt</c>），按装、卸分开；没提交的不计。分区取作业所属需求的从属需求行。</item>
/// </list>
/// <para>
/// 样本期取 [from, to)：旅程按建立时刻、需求按绑车时刻、作业按下发时刻落在窗里——人工标定票要在「一次只开一个仓门」
/// （REQ-0357）上线之后取样，靠的就是这个窗。中位数在偶数个样本时取中间两个的平均，P95 取最近秩（第 ⌈0.95·n⌉ 小）。
/// </para>
/// <para>
/// <b>时间比较全在内存里做</b>：生产库是 SQLite，按 <c>DateTimeOffset</c> 在库里比较或排序会抛。几张表整张读回来再筛，
/// 对一个人手动点开的报表是可以接受的代价。
/// </para>
/// </remarks>
public static class StarvationCalibrationQuery
{
    /// <summary>CSV 的列，次序固定。</summary>
    public const string CsvHeader =
        "dispatch_zone,window_from,window_to,vehicle_count,task_count," +
        "full_cycle_count,full_cycle_median_s,full_cycle_p95_s,full_cycle_max_s," +
        "wait_to_bind_count,wait_to_bind_median_s,wait_to_bind_p95_s,wait_to_bind_max_s," +
        "load_count,load_median_s,load_p95_s,load_max_s," +
        "unload_count,unload_median_s,unload_p95_s,unload_max_s";

    private const string LoadType = "LOAD";
    private const string UnloadType = "UNLOAD";

    /// <summary>一组时长的中位数、P95（最近秩）与最大值。</summary>
    public static DurationSummary Summarize(IEnumerable<TimeSpan> durations)
    {
        ArgumentNullException.ThrowIfNull(durations);
        double[] seconds = [.. durations.Select(duration => duration.TotalSeconds).Order()];
        if (seconds.Length == 0)
        {
            return new DurationSummary(0, null, null, null);
        }

        int n = seconds.Length;
        double median = n % 2 == 1 ? seconds[n / 2] : (seconds[(n / 2) - 1] + seconds[n / 2]) / 2;
        int p95Rank = (int)Math.Ceiling(0.95 * n);
        return new DurationSummary(n, median, seconds[p95Rank - 1], seconds[^1]);
    }

    /// <summary>样本期 [<paramref name="from"/>, <paramref name="to"/>) 内的报表。</summary>
    public static async Task<StarvationCalibrationReport> ReadAsync(
        ControlServerDbContext dbContext,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        bool InWindow(DateTimeOffset at) => at >= from && at < to;

        var journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Select(row => new { row.JourneyId, row.AgvId, row.DispatchZone, row.Stage, row.CreatedAt, row.UpdatedAt })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var memberships = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Select(row => new { row.JourneyId, row.DemandId, row.DispatchZone, row.AddedAt })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, DateTimeOffset> createdLocally = (await dbContext.JourneyBacklog.AsNoTracking()
                .Select(row => new { row.DemandId, row.DemandCreatedAt })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row.DemandId, row => row.DemandCreatedAt, StringComparer.Ordinal);
        var operations = await dbContext.StationOperations.AsNoTracking()
            .Where(row => row.CommittedAt != null)
            .Select(row => new { row.DemandId, row.OperationType, row.CreatedAt, row.CommittedAt })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> agvByJourney = journeys.ToDictionary(
            journey => journey.JourneyId, journey => journey.AgvId, StringComparer.Ordinal);
        // 一条需求取它最早那条从属需求行：改派过的需求按第一次绑车计一次，分区也取那一次的。
        var firstBinding = memberships
            .GroupBy(row => row.DemandId, StringComparer.Ordinal)
            .Select(group => group.MinBy(row => row.AddedAt)!)
            .ToDictionary(row => row.DemandId, StringComparer.Ordinal);
        var bound = firstBinding.Values.Where(row => InWindow(row.AddedAt)).ToArray();
        var cycles = journeys
            .Where(journey => journey.Stage == JourneyRuntimeStage.Completed && InWindow(journey.CreatedAt))
            .ToArray();
        var issued = operations
            .Where(operation => InWindow(operation.CreatedAt) && firstBinding.ContainsKey(operation.DemandId))
            .Select(operation => new
            {
                Zone = firstBinding[operation.DemandId].DispatchZone,
                operation.OperationType,
                Duration = operation.CommittedAt!.Value - operation.CreatedAt,
            })
            .ToArray();

        string[] zones =
        [
            .. bound.Select(row => row.DispatchZone)
                .Concat(cycles.Select(journey => journey.DispatchZone))
                .Concat(issued.Select(operation => operation.Zone))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        List<ZoneCalibrationEvidence> evidence = [];
        foreach (string zone in zones)
        {
            var boundHere = bound.Where(row => row.DispatchZone == zone).ToArray();
            var issuedHere = issued.Where(operation => operation.Zone == zone).ToArray();
            evidence.Add(new ZoneCalibrationEvidence(
                zone,
                Summarize(cycles
                    .Where(journey => journey.DispatchZone == zone)
                    .Select(journey => journey.UpdatedAt - journey.CreatedAt)),
                Summarize(boundHere
                    .Where(row => createdLocally.TryGetValue(row.DemandId, out DateTimeOffset created) && created != default)
                    .Select(row => row.AddedAt - createdLocally[row.DemandId])),
                boundHere
                    .Select(row => agvByJourney.GetValueOrDefault(row.JourneyId))
                    .OfType<string>()
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
                boundHere.Length,
                [
                    new StationOperationSummary(LoadType, Summarize(issuedHere
                        .Where(operation => operation.OperationType == SlotOperationType.Load)
                        .Select(operation => operation.Duration))),
                    new StationOperationSummary(UnloadType, Summarize(issuedHere
                        .Where(operation => operation.OperationType == SlotOperationType.Unload)
                        .Select(operation => operation.Duration))),
                ]));
        }

        return new StarvationCalibrationReport(from, to, now, evidence);
    }

    /// <summary>同一份报表的 CSV：固定表头，一个分区一行，CRLF 换行；没有样本的统计量留空。</summary>
    public static string ToCsv(StarvationCalibrationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        StringBuilder csv = new();
        csv.Append(CsvHeader).Append("\r\n");
        foreach (ZoneCalibrationEvidence zone in report.Zones)
        {
            DurationSummary load = zone.StationOperations.Single(operation => operation.OperationType == LoadType).Duration;
            DurationSummary unload = zone.StationOperations.Single(operation => operation.OperationType == UnloadType).Duration;
            string[] fields =
            [
                Quote(zone.DispatchZone),
                report.From.ToString("O", CultureInfo.InvariantCulture),
                report.To.ToString("O", CultureInfo.InvariantCulture),
                Number(zone.VehicleCount),
                Number(zone.TaskCount),
                .. Columns(zone.FullCycle),
                .. Columns(zone.WaitToBind),
                .. Columns(load),
                .. Columns(unload),
            ];
            csv.AppendJoin(',', fields).Append("\r\n");
        }
        return csv.ToString();
    }

    private static string[] Columns(DurationSummary summary) =>
        [Number(summary.Count), Number(summary.MedianSeconds), Number(summary.P95Seconds), Number(summary.MaxSeconds)];

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>分区名来自现场配置；含逗号、引号或换行时按 RFC 4180 加引号。</summary>
    private static string Quote(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
