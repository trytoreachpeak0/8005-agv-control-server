using System.Globalization;
using System.Text;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 防饥饿阈值标定证据的只读查询端点（REQ-0203；批次7-09，control-server#214）。
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /api/dispatch/starvation-calibration?from=…&amp;to=…</c> 给 JSON；再加 <c>&amp;format=csv</c> 给可下载的 CSV。
/// <c>from</c>、<c>to</c> 是 ISO 8601 时刻，样本期取 [from, to)，两个都必须给、且 from 早于 to，否则 400。口径见
/// <see cref="StarvationCalibrationQuery"/>。
/// </para>
/// <para>
/// <b>放在看板之外、FieldOps 之外，是分票决定的</b>：本批看板由批次7-12（control-server#217）独占，FieldOps 动词表由
/// 批次7-11（control-server#216）独占。这里只交付查询；要进看板或 FieldOps，由那两张票接线。只读，与
/// <c>/api/runtime/*</c> 那几个查询一样不带凭据——它不改任何东西，暴露面由服务端绑定的网卡决定。
/// </para>
/// </remarks>
public static class StarvationCalibrationEndpoints
{
    public const string Route = "/api/dispatch/starvation-calibration";

    public static IEndpointRouteBuilder MapStarvationCalibrationReport(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet(Route, async (
            string? from,
            string? to,
            string? format,
            ControlServerDbContext dbContext,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseInstant(from, out DateTimeOffset windowFrom) || !TryParseInstant(to, out DateTimeOffset windowTo))
            {
                return Results.Problem(
                    "Both 'from' and 'to' are required, as ISO 8601 instants.", statusCode: StatusCodes.Status400BadRequest);
            }
            if (windowFrom >= windowTo)
            {
                return Results.Problem("'from' must be earlier than 'to'.", statusCode: StatusCodes.Status400BadRequest);
            }

            StarvationCalibrationReport report = await StarvationCalibrationQuery.ReadAsync(
                dbContext, windowFrom, windowTo, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                string fileName = string.Create(
                    CultureInfo.InvariantCulture,
                    $"starvation-calibration-{windowFrom.UtcDateTime:yyyyMMddTHHmmssZ}-{windowTo.UtcDateTime:yyyyMMddTHHmmssZ}.csv");
                return Results.File(Encoding.UTF8.GetBytes(StarvationCalibrationQuery.ToCsv(report)), "text/csv", fileName);
            }

            return Results.Json(report);
        });
        return endpoints;
    }

    private static bool TryParseInstant(string? text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out instant) &&
        !string.IsNullOrWhiteSpace(text);
}
