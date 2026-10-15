using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 车载屏上的「出发」（协议 4.0.0 候选的 <c>CurrentStopDepartureRequested</c> / <c>CurrentStopDepartureResult</c>，CP-0012、
/// control-server#713）：请求与答复在收件箱、发件箱里的那几样事实，入站与引擎两处共用。
/// </summary>
/// <remarks>
/// <para>
/// <b>请求落在收件箱，答复由引擎判。</b>与扫码录入（<c>SublotSubmitted</c>）同一个形状：入站只让请求持久、回 <c>DurableAck</c>，
/// 判定、放回需求与答复由引擎在下一轮的写事务里一起做（<c>JourneyRuntimeEngine.AnswerDepartureRequestsAsync</c>），于是
/// 「判定与放回先于答复持久」（向量的 <c>departure-decision-and-releases-durable-before-result</c>）在构造上成立，答复之后紧跟的
/// 业务快照也出自同一轮。车没有在途旅程时引擎不会推进它，那一种由派车轮之后的扫尾答（<see cref="UnansweredAsync"/> 的第二个读者）。
/// </para>
/// <para>
/// <b>业务去重按 <c>departureRequestId</c>，不按 <c>messageId</c>。</b>答复的 id 由它派生（<see cref="ResultMessageId"/>），
/// 发件箱里有这一行就是「判过了」；同号同内容原样重放那一行，同号不同内容在入站以 <c>BUSINESS_ID_CONTENT_CONFLICT</c> 拒收
/// （<see cref="ConflictsWithEarlierAsync"/>），不进收件箱、不答 <c>CurrentStopDepartureResult</c>（wire-notes 末两条）。
/// 同一行跨代次重发只差 <c>sessionGeneration</c>，由收件箱的代次重绑放行（hmi#349 审查转来的第 10 条），首次应答仍是 <c>DurableAck</c>。
/// </para>
/// <para>
/// <b>补发只在 <c>SessionReadiness</c> 之后。</b>车还没确认的答复由两处补：握手结束那条恢复报告的应答（它带着 <c>SessionReadiness</c>）
/// 之后的补发（<c>OnboardRecoveryCoordinator.ReplayPendingCommandsAsync</c>），与就绪之后引擎每轮的补发。两处都不在握手中间插行
/// （hmi#349 联调第 1 条）。补发的那一行就是发件箱里存的那一行，换代时由补发统一改写代次并落库，所以车回的 <c>DurableAck</c> 哈希对得上。
/// </para>
/// </remarks>
internal static class CurrentStopDepartures
{
    public const string RequestType = "CurrentStopDepartureRequested";
    public const string ResultType = "CurrentStopDepartureResult";

    /// <summary>
    /// 出发结束一站时写在归属行、收尾与空清单上的原因码（服务端内部码，经 <see cref="StopEndedReasons.ForEnding"/> 成为 <c>OPERATOR_DEPARTED</c>）。
    /// 放回本身仍记 <see cref="DemandJourneyLookup.ReleasedForRedispatchReason"/>：派车读口认它，control-server#703 的次数不数它。
    /// </summary>
    public const string OperatorDepartedReason = "STAGING_OPERATOR_DEPARTED";

    /// <summary>同上，车缺电而结束待送点那一站（<c>CHARGING_REQUIRED</c>）。</summary>
    public const string ChargingRequiredReason = "STAGING_CHARGING_REQUIRED";

    /// <summary>
    /// 待送点上装货中取消、需求放回待派而结束那一站（control-server#713 票正文第 5 条）：车上说 <c>LOAD_CANCELLED</c>，与机台上的取消同一句，
    /// 只是需求不终结、不抑制。
    /// </summary>
    public const string LoadCancelledReleasedReason = "STAGING_LOAD_CANCELLED_RELEASED";

    /// <summary>对一次按键的答复的 messageId：只由 <c>departureRequestId</c> 派生，重放与重判都落在同一行上。</summary>
    public static string ResultMessageId(string departureRequestId) =>
        JourneyPlanBuilder.StableGuid(departureRequestId, "current-stop-departure-result");

    /// <summary>收件箱里的一条出发请求。</summary>
    internal sealed record Request(
        string MessageId,
        string AgvId,
        string DepartureRequestId,
        string? OperationSessionId,
        string StationId,
        long WorklistRevision,
        string OperatorId,
        DateTimeOffset ReceivedAt);

    /// <summary>
    /// 这辆车还没有答复的出发请求，按收件时刻；同一个 <c>departureRequestId</c> 只给最早那一条（之后的同号同内容由重放答，见类注释）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>按车过滤与「还没答」都在库里做</b>（独立审查 M3）。引擎每轮、每辆车都问一次，而收件箱没有清理，出发请求只增不减；原先把所有车的历史
    /// 请求读回来逐条解析、再逐条查一次发件箱，开销随按键次数线性增长、拖慢整轮推进。现在库里用 <c>json_extract</c> 取信封上的车号，并对发件箱里的
    /// 答复做反连接，读回来的只有这辆车还没答的那几条，通常是零条。
    /// </para>
    /// <para>
    /// <c>json_extract</c> 比的是解析后的值：车号常是中文，存下的那一行里可能被转义，在原文上 <c>Contains</c> 永远不中，这里不受影响。
    /// 答复的 messageId 由 <c>departureRequestId</c> 哈希派生（<see cref="ResultMessageId"/>），库里算不出来，所以反连接比的是答复载荷里的
    /// <c>departureRequestId</c>。
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<Request>> UnansweredAsync(
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox
            .FromSqlInterpolated($"""
                SELECT i."MessageId", i."MessageType", i."RequestJson", i."ContentHash", i."FirstResponseJson", i."ReceivedAt",
                       i."ReceivedAtUtcTicks"
                FROM "ProtocolInbox" AS i
                WHERE i."MessageType" = {RequestType}
                  AND json_extract(i."RequestJson", '$.agvId') = {agvId}
                  AND NOT EXISTS (
                      SELECT 1 FROM "ProtocolOutbox" AS o
                      WHERE o."MessageType" = {ResultType}
                        AND json_extract(o."PayloadJson", '$.payload.departureRequestId') =
                            json_extract(i."RequestJson", '$.payload.departureRequestId'))
                """)
            .AsNoTracking()
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<Request> unanswered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ProtocolInboxRow row in rows.OrderBy(row => row.ReceivedAt).ThenBy(row => row.MessageId, StringComparer.Ordinal))
        {
            Request request = Parse(row);
            if (seen.Add(request.DepartureRequestId))
            {
                unanswered.Add(request);
            }
        }
        return unanswered;
    }

    /// <summary>
    /// 这条请求的 <c>departureRequestId</c> 先前以别的 messageId 到过、而内容不同：业务号内容冲突（入站据此拒收）。
    /// 比的是整个 payload；同一行跨代次重发不走到这里，收件箱按 messageId 先答了。
    /// </summary>
    public static async Task<bool> ConflictsWithEarlierAsync(
        ControlServerDbContext dbContext,
        string messageId,
        string agvId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string departureRequestId = payload.GetProperty("departureRequestId").GetString()!;
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == RequestType && row.MessageId != messageId &&
                          row.RequestJson.Contains(departureRequestId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? candidate = JsonNode.Parse(payload.GetRawText());
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement stored = document.RootElement.GetProperty("payload");
            if (!string.Equals(stored.GetProperty("departureRequestId").GetString(), departureRequestId, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.Equals(document.RootElement.GetProperty("agvId").GetString(), agvId, StringComparison.Ordinal) ||
                !JsonNode.DeepEquals(JsonNode.Parse(stored.GetRawText()), candidate))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>这辆车还没确认的出发答复，给握手之后的补发与引擎每轮的补发。</summary>
    public static async Task<IReadOnlyList<string>> PendingResultIdsAsync(
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        var rows = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == ResultType && row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => new { row.MessageId, row.PayloadJson })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        List<string> ids = [];
        foreach (var row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            if (string.Equals(document.RootElement.GetProperty("agvId").GetString(), agvId, StringComparison.Ordinal))
            {
                ids.Add(row.MessageId);
            }
        }
        return ids;
    }

    /// <summary>这辆车的清单流此刻是哪一版：发件箱里这辆车最高的那一号（含已退役的；号按车单调）。一版都没有为 0。</summary>
    public static async Task<long> CurrentWorklistRevisionAsync(
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken) =>
        await JourneyClosure.HighestSentRevisionAsync(dbContext, agvId, "CurrentStopWorklistSnapshot", cancellationToken)
            .ConfigureAwait(false) ?? 0;

    private static Request Parse(ProtocolInboxRow row)
    {
        using JsonDocument document = JsonDocument.Parse(row.RequestJson);
        JsonElement root = document.RootElement;
        JsonElement payload = root.GetProperty("payload");
        JsonElement session = payload.GetProperty("operationSessionId");
        return new Request(
            row.MessageId,
            root.GetProperty("agvId").GetString()!,
            payload.GetProperty("departureRequestId").GetString()!,
            session.ValueKind == JsonValueKind.Null ? null : session.GetString(),
            payload.GetProperty("stationId").GetString()!,
            payload.GetProperty("worklistRevision").GetInt64(),
            payload.GetProperty("operator").GetProperty("operatorId").GetString()!,
            row.ReceivedAt);
    }
}

/// <summary>
/// 待送点出发相关、要跨轮记住的告警（control-server#713 独立审查 L2）：缺电带货而锚没装时的 Warning 每趟旅程只记一次。宿主注册为单例；
/// 引擎每轮新建，跨轮的状态放在这里而不是引擎里或静态字段里。
/// </summary>
public sealed class StagingDepartureWarnings
{
    /// <summary>已经记过「锚没装、不自动出发」的旅程。</summary>
    internal ConcurrentDictionary<string, byte> AnchorUnloadedWarned { get; } = new(StringComparer.Ordinal);
}
