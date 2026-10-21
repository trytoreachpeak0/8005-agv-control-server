using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「剩下的不装记录」的数据面（REQ-0367，CP-0021，control-server#830）：操作员在多篮装货里申请「剩下的不装」，服务端每判一次记一行——
/// 批号、车、期待篮数 N、已装篮数 k、操作员工号、申请与判定时刻、授权还是拒绝（拒绝带原因）、结果是否已按 k 篮结算。
/// </summary>
/// <remarks>
/// <para>
/// <b>来源是收件箱与发件箱</b>（<see cref="RemainingBasketsSkipRecords"/> 的类注释）：判定就是发件箱里那一行答复，申请（含操作员与收到时刻）是收件箱里那一行
/// 请求。N 取这条需求的归属行（减篮不改它），k 取授权的 <c>loadedSlots</c>，拒绝时取申请里的。结算看这次装货：已提交、而且只剩 k 个仓。
/// </para>
/// <para>
/// 只列最近的 <see cref="Limit"/> 条：减篮一天至多几次，这张卡片是给现场与审计翻近期记录的，不是全量导出——全量在日志里，每次判定一行
/// （<c>LogRemainingBasketsSkipDecided</c>，事件号 8300）。
/// </para>
/// </remarks>
internal sealed class RemainingBasketsSkipsQueryEndpoint : IDashboardQueryEndpoint
{
    internal const int Limit = 50;

    /// <summary>拒绝原因码的说明，卡片照原样显示。</summary>
    internal static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ServerReasonCodes.DemandNotCurrent] = "申请的需求或这次装货已经不是车此刻在装的那一条",
        [ServerReasonCodes.OperationSessionMismatch] = "申请里的站点操作不是车此刻所在停靠的",
        [ServerReasonCodes.ActionNotAllowedInState] = "此刻不接受：只有一篮、装货已结束、已转人工处理、纠错或取消在处理，或这次装货已同意过一次",
        [ServerReasonCodes.SlotStateUnknown] = "有目标仓位的状态不明",
        [ServerReasonCodes.LockNotClosed] = "有仓门没有证明锁闭",
        [ServerReasonCodes.UnlockOutputNotReset] = "有仓位的开锁输出没有复位",
        [ServerReasonCodes.RemainingBasketsSkipNothingLoaded] = "按服务端读数一篮都还没装进；一篮不装应当取消装货",
        [ServerReasonCodes.SlotSetInvalid] = "申请里的已装仓位与服务端读到的不一致，或者全部仓位都已装满"
    };

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "remaining-baskets-skips";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ProtocolOutboxRow[] answers = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == RemainingBasketsSkipRecords.ResultType)
            .ToArrayAsync(cancellationToken);
        // SQLite cannot ORDER BY a DateTimeOffset: the newest first, in memory.
        answers = [.. answers.OrderByDescending(row => row.CreatedAt).ThenBy(row => row.MessageId, StringComparer.Ordinal).Take(Limit)];
        Dictionary<string, (JsonElement Envelope, DateTimeOffset ReceivedAt)> requests = await RequestsBySkipIdAsync(dbContext, cancellationToken);
        List<object> rows = [];
        foreach (ProtocolOutboxRow answer in answers)
        {
            using JsonDocument document = JsonDocument.Parse(answer.PayloadJson);
            JsonElement envelope = document.RootElement;
            JsonElement payload = envelope.GetProperty("payload");
            string skipRequestId = payload.GetProperty("skipRequestId").GetString()!;
            string demandId = payload.GetProperty("demandId").GetString()!;
            string attemptId = payload.GetProperty("slotOperationAttemptId").GetString()!;
            string outcome = payload.GetProperty("outcome").GetString()!;
            (JsonElement Envelope, DateTimeOffset ReceivedAt)? received = requests.TryGetValue(skipRequestId, out var found) ? found : null;
            JsonElement? request = received?.Envelope;
            int[] loaded = outcome == RemainingBasketsSkipRule.Authorized
                ? [.. payload.GetProperty("loadedSlots").EnumerateArray().Select(item => item.GetInt32())]
                : request is { } r ? [.. r.GetProperty("payload").GetProperty("loadedSlots").EnumerateArray().Select(item => item.GetInt32())] : [];
            int? expected = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
                .Where(row => row.DemandId == demandId)
                .Select(row => (int?)row.ExpectedBasketCount)
                .MaxAsync(cancellationToken);
            string? transportDemandKey = await dbContext.AcceptedDemands.AsNoTracking()
                .Where(row => row.DemandId == demandId)
                .Select(row => row.TransportDemandKey)
                .SingleOrDefaultAsync(cancellationToken);
            StationOperationRow? load = await dbContext.StationOperations.AsNoTracking()
                .SingleOrDefaultAsync(row => row.SlotOperationAttemptId == attemptId, cancellationToken);
            bool settled = outcome == RemainingBasketsSkipRule.Authorized && load is { Status: StationOperationStatus.Committed } &&
                           (JsonSerializer.Deserialize<int[]>(load.TargetSlotsJson) ?? []).Order().SequenceEqual(loaded.Order());
            string? reasonCode = payload.GetProperty("problem") is { ValueKind: JsonValueKind.Object } problem
                ? problem.GetProperty("reasonCode").GetString()
                : null;
            JsonElement? @operator = request?.GetProperty("payload").GetProperty("operator");
            rows.Add(new
            {
                skipRequestId,
                agvId = envelope.GetProperty("agvId").GetString(),
                demandId,
                sublot = transportDemandKey is null ? null : TransportDemandKeys.SublotOf(transportDemandKey),
                slotOperationAttemptId = attemptId,
                expectedBasketCount = expected,
                loadedBasketCount = loaded.Length,
                loadedSlots = loaded,
                operatorId = @operator is { ValueKind: JsonValueKind.Object } who ? who.GetProperty("operatorId").GetString() : null,
                requestedAt = received?.ReceivedAt,
                decidedAt = answer.CreatedAt,
                outcome,
                reasonCode,
                reasonDescription = reasonCode is null ? null : Descriptions.GetValueOrDefault(reasonCode),
                settledAsLoaded = settled
            });
        }
        return new { skips = rows.ToArray() };
    }

    private static async Task<Dictionary<string, (JsonElement Envelope, DateTimeOffset ReceivedAt)>> RequestsBySkipIdAsync(
        ControlServerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == RemainingBasketsSkipRecords.RequestType)
            .ToArrayAsync(cancellationToken);
        Dictionary<string, (JsonElement Envelope, DateTimeOffset ReceivedAt)> requests = new(StringComparer.Ordinal);
        // The first submission of a skipRequestId is the operator's press; later ones repeat it with the same payload.
        foreach (ProtocolInboxRow row in rows.OrderBy(row => row.ReceivedAt).ThenBy(row => row.MessageId, StringComparer.Ordinal))
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            string skipRequestId = document.RootElement.GetProperty("payload").GetProperty("skipRequestId").GetString()!;
            requests.TryAdd(skipRequestId, (document.RootElement.Clone(), row.ReceivedAt));
        }
        return requests;
    }
}
