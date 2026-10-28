using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>发件箱里一行未确认、未退役的行，连同它所属的旅程与需求、以及它为什么算残留。</summary>
public sealed record OutboxResidueLine(
    string MessageId,
    string MessageType,
    string? AgvId,
    DateTimeOffset CreatedAt,
    string? JourneyId,
    string? JourneyStage,
    string? DemandId,
    string? MembershipStatus,
    bool MembershipRemoved,
    string? SlotCommandConclusion,
    string[] Reasons);

/// <summary>
/// 只读扫描：发件箱里未确认、未退役，而所属旅程已收尾、所属需求已终结或已移出旅程、或（装卸命令）已有结论的行（control-server#623）。
/// </summary>
/// <remarks>
/// <para>
/// <b>做什么用。</b>部署换协议身份之前查生产库：这些行车不会再确认，留着就让 <c>ProtocolOutboxIdentityStartupCheck</c> 拒绝启动。
/// 一行不写：调用方以 SQLite 只读模式开库（FieldOps <c>outbox-residue</c>）。怎么处理是部署时的决定，这里只列。
/// </para>
/// <para>
/// <b>按什么认归属。</b>装卸命令按命令 id 认归属；载荷带 <c>demandId</c> 的按需求认（取未移除的那一条，没有就取最后加入的）；
/// 载荷带 <c>operationSessionId</c> 的按停靠认旅程。认不出归属的行照样列出它的车与类型，但不算残留——认不出它属于谁，就不替它判。
/// </para>
/// <para>
/// 装卸命令的结论与启动修复同一处判定（<see cref="SlotCommandOutbox.JudgeAsync"/>）：列出的 <c>ANSWERED</c>／<c>WITHDRAWN</c>
/// 行就是装了本修复的构建启动时会结算或退役的行。
/// </para>
/// </remarks>
public static class OutboxResidue
{
    public const string JourneyCompleted = "JOURNEY_COMPLETED";
    public const string DemandTerminated = "DEMAND_TERMINATED";
    public const string DemandRemoved = "DEMAND_REMOVED_FROM_JOURNEY";
    public const string SlotCommandAnswered = "SLOT_COMMAND_ANSWERED";
    public const string SlotCommandWithdrawn = "SLOT_COMMAND_WITHDRAWN";

    /// <summary>每一行未确认、未退役的行（<see cref="OutboxResidueLine.Reasons"/> 为空的不算残留），按车、建立时间排序。</summary>
    public static async Task<IReadOnlyList<OutboxResidueLine>> ScanAsync(
        ControlServerDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        ProtocolOutboxRow[] pending = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.AcknowledgedAt == null && row.FencedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Length == 0)
        {
            return [];
        }
        Dictionary<string, SlotCommandOutbox.Conclusion> slotConclusions = (await SlotCommandOutbox
                .JudgeAsync(dbContext, agvId: null, cancellationToken).ConfigureAwait(false))
            .ToDictionary(item => item.Row.MessageId, item => item.Conclusion, StringComparer.Ordinal);
        List<JourneyDemandRow> memberships = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> stopJourneys = (await dbContext.Set<JourneyStopRow>().AsNoTracking()
                .Select(row => new { row.OperationSessionId, row.JourneyId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(row => row.OperationSessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().JourneyId, StringComparer.Ordinal);
        Dictionary<string, JourneyRuntimeStage> stages = (await dbContext.JourneyRuntimes.AsNoTracking()
                .Select(row => new { row.JourneyId, row.Stage })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(row => row.JourneyId, row => row.Stage, StringComparer.Ordinal);

        List<OutboxResidueLine> lines = [];
        foreach (ProtocolOutboxRow row in pending)
        {
            (string? agvId, string? demandId, string? operationSessionId) = Read(row);
            JourneyDemandRow? membership =
                memberships.FirstOrDefault(item => item.LoadCommandMessageId == row.MessageId || item.UnloadCommandMessageId == row.MessageId)
                ?? (demandId is null
                    ? null
                    : memberships.Where(item => item.DemandId == demandId)
                        .OrderBy(item => item.RemovedAt is null ? 1 : 0)
                        .ThenBy(item => item.AddedAt)
                        .LastOrDefault());
            string? journeyId = membership?.JourneyId ??
                                (operationSessionId is not null && stopJourneys.TryGetValue(operationSessionId, out string? fromStop)
                                    ? fromStop
                                    : null);
            JourneyRuntimeStage? stage = journeyId is not null && stages.TryGetValue(journeyId, out JourneyRuntimeStage found)
                ? found
                : null;
            SlotCommandOutbox.Conclusion? conclusion = slotConclusions.TryGetValue(row.MessageId, out SlotCommandOutbox.Conclusion judged)
                ? judged
                : null;

            List<string> reasons = [];
            if (stage == JourneyRuntimeStage.Completed)
            {
                reasons.Add(JourneyCompleted);
            }
            if (membership?.Status == JourneyDemandStatuses.Terminated)
            {
                reasons.Add(DemandTerminated);
            }
            if (membership?.RemovedAt is not null)
            {
                reasons.Add(DemandRemoved);
            }
            if (conclusion == SlotCommandOutbox.Conclusion.Answered)
            {
                reasons.Add(SlotCommandAnswered);
            }
            else if (conclusion == SlotCommandOutbox.Conclusion.Withdrawn)
            {
                reasons.Add(SlotCommandWithdrawn);
            }
            lines.Add(new OutboxResidueLine(
                row.MessageId,
                row.MessageType,
                agvId,
                row.CreatedAt,
                journeyId,
                stage?.ToString(),
                membership?.DemandId ?? demandId,
                membership?.Status,
                membership?.RemovedAt is not null,
                conclusion?.ToString().ToUpperInvariant(),
                [.. reasons]));
        }
        return [.. lines
            .OrderBy(line => line.AgvId, StringComparer.Ordinal)
            .ThenBy(line => line.CreatedAt)
            .ThenBy(line => line.MessageId, StringComparer.Ordinal)];
    }

    private static (string? AgvId, string? DemandId, string? OperationSessionId) Read(ProtocolOutboxRow row)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            JsonElement root = document.RootElement;
            JsonElement payload = root.TryGetProperty("payload", out JsonElement inner) && inner.ValueKind == JsonValueKind.Object
                ? inner
                : default;
            return (Text(root, "agvId"), Text(payload, "demandId"), Text(payload, "operationSessionId"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
