using System.Text.Json;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「站点超时抑制清单」的数据面（REQ-0365，CP-0016，control-server#751）：取货停靠的站点离站等待期限到期、本站没人录入，
/// 被以 <c>CANCELLED_BY_STATION_TIMEOUT</c> 终结、按业务键抑制、等人工搬运的需求，每条给批号、取货站点与终结时刻。
/// </summary>
/// <remarks>
/// <para>
/// <b>来源是人工搬运告警</b>（<see cref="ManualCarryAlarms"/>），只取站点期限那两个码（<see cref="ManualCarryAlarms.StationDeadlineEndings"/>）。
/// 确定的装货失败终结时记的是同一个 <c>CANCELLED_BY_STATION_TIMEOUT</c>，但告警码不同、不是站点期限到期，不列（REQ-0365 只收站点期限到期终结的）。
/// 不按需求的终结码筛，正是因为那个码两种来源共用。
/// </para>
/// <para>
/// <b>移出</b>跟着告警走：需求离开 MES 目录（MES 关单）时派车轮末的写入器清掉告警，这里只列未清的，所以它自然消失；
/// 回到目录时告警重开，它又出现。终结时刻取这条业务键的抑制行（<c>TransportDemandSuppressions.SuppressedAt</c>）：它与终结同一次保存落库、
/// 按业务键只写一次不改写。不取告警的 <c>FirstRaisedAt</c>：告警重开时那一列改成重开的时刻（control-server#751 审查 M1）。
/// 读不到抑制行（正常走不到）时才退回告警的形成时刻。
/// </para>
/// <para>
/// 站点取告警细节里记下的 <c>stationId</c>（终结时本停靠的站点），批号从业务键拆（<see cref="TransportDemandKeys.SublotOf"/>）。
/// 读不出站点时给 <c>null</c>，卡片照样列这一行——人要知道有这批料没人搬，站点空着也比整行不见好。
/// </para>
/// </remarks>
internal sealed class StationTimeoutSuppressionsQueryEndpoint : IDashboardQueryEndpoint
{
    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "station-timeout-suppressions";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string[] codes = [.. ManualCarryAlarms.StationDeadlineEndings];
        StructuralDispatchBlockRow[] alarms = await dbContext.Set<StructuralDispatchBlockRow>().AsNoTracking()
            .Where(row => row.ClearedAt == null && codes.Contains(row.ReasonCode))
            .ToArrayAsync(cancellationToken);
        string[] keys = [.. alarms.Select(row => row.TransportDemandKey).Distinct(StringComparer.Ordinal)];
        Dictionary<string, DateTimeOffset> endedAtByDemand = (await dbContext.Set<TransportDemandSuppressionRow>().AsNoTracking()
                .Where(row => keys.Contains(row.TransportDemandKey))
                .ToArrayAsync(cancellationToken))
            .ToDictionary(row => row.DemandId, row => row.SuppressedAt, StringComparer.Ordinal);
        // SQLite cannot ORDER BY a DateTimeOffset, so the oldest ending comes first in memory.
        return new
        {
            suppressions = alarms
                .Select(row => (Alarm: row, EndedAt: endedAtByDemand.TryGetValue(row.DemandId, out DateTimeOffset at) ? at : row.FirstRaisedAt))
                .OrderBy(item => item.EndedAt)
                .ThenBy(item => item.Alarm.DemandId, StringComparer.Ordinal)
                .Select(item => new
                {
                    demandId = item.Alarm.DemandId,
                    sublot = TransportDemandKeys.SublotOf(item.Alarm.TransportDemandKey),
                    stationId = StationOf(item.Alarm.DetailJson),
                    endedAt = item.EndedAt,
                    reasonCode = item.Alarm.ReasonCode,
                    reasonDescription = DispatchBacklogQueryEndpoint.Descriptions.GetValueOrDefault(item.Alarm.ReasonCode)
                })
                .ToArray()
        };
    }

    private static string? StationOf(string detailJson)
    {
        try
        {
            using JsonDocument detail = JsonDocument.Parse(detailJson);
            return detail.RootElement.ValueKind == JsonValueKind.Object
                   && detail.RootElement.TryGetProperty("stationId", out JsonElement station)
                   && station.ValueKind == JsonValueKind.String
                ? station.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
