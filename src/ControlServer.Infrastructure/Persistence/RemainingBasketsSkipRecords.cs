using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 「剩下的不装」（REQ-0367，CP-0021，control-server#830）在收件箱、发件箱里的那几样事实：入站判定与结果结算两处共用。
/// </summary>
/// <remarks>
/// <para>
/// <b>没有自己的表。</b>与车载屏上的「出发」（<c>CurrentStopDepartures</c>，control-server#713）同一个做法：请求留在收件箱（含操作员与收到时刻），
/// 判定就是发件箱里那一行 <c>RemainingBasketsSkipResult</c>，发件箱里有这一行就是「判过了」。结算时按 (<c>demandId</c>,
/// <c>slotOperationAttemptId</c>) 找唯一那一次 <c>AUTHORIZED</c>（wire-notes 减篮一节）；一次装货至多授权一次，由判定保证。
/// </para>
/// <para>
/// 按 <c>PayloadJson.Contains(attempt)</c> 先在库里粗筛、再逐行解析比字段，与 <c>CurrentStopDepartures.ConflictsWithEarlierAsync</c> 同一个形状：
/// 减篮答复一辆车一趟装货至多几条，粗筛之后通常是零行或一行。
/// </para>
/// </remarks>
public static class RemainingBasketsSkipRecords
{
    public const string RequestType = "RemainingBasketsSkipRequested";
    public const string ResultType = "RemainingBasketsSkipResult";

    /// <summary><c>OperationResult.overallOutcome</c> 里减篮结束的那个值。</summary>
    public const string SkippedOutcome = "COMPLETED_REMAINING_SKIPPED";

    /// <summary>
    /// 这次装货已授权的那几个仓（<c>AUTHORIZED</c> 答复的 <c>loadedSlots</c>）；没有授权过返回 null。
    /// </summary>
    public static async Task<int[]?> AuthorizedSlotsAsync(
        ControlServerDbContext dbContext,
        string demandId,
        string slotOperationAttemptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        foreach (JsonElement payload in await ResultPayloadsAsync(dbContext, slotOperationAttemptId, cancellationToken)
                     .ConfigureAwait(false))
        {
            if (payload.GetProperty("demandId").GetString() == demandId &&
                payload.GetProperty("outcome").GetString() == "AUTHORIZED")
            {
                return [.. payload.GetProperty("loadedSlots").EnumerateArray().Select(item => item.GetInt32())];
            }
        }
        return null;
    }

    /// <summary>这次装货发出过的全部减篮答复的载荷。</summary>
    public static async Task<IReadOnlyList<JsonElement>> ResultPayloadsAsync(
        ControlServerDbContext dbContext,
        string slotOperationAttemptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string[] rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == ResultType && row.PayloadJson.Contains(slotOperationAttemptId))
            .Select(row => row.PayloadJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<JsonElement> payloads = [];
        foreach (string json in rows)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement payload = document.RootElement.GetProperty("payload");
            if (payload.GetProperty("slotOperationAttemptId").GetString() == slotOperationAttemptId)
            {
                payloads.Add(payload.Clone());
            }
        }
        return payloads;
    }
}
