using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// 车载屏上的「剩下的不装」（协议 6.0.0 候选的 <c>RemainingBasketsSkipRequested</c> / <c>RemainingBasketsSkipResult</c>，REQ-0367，CP-0021，
/// control-server#830）：入站判定这一半。结算那一半在 <c>WireToGateStore.ApplyOperationResultAsync</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>请求落在收件箱，答复在回完 DurableAck 之后判。</b>入站只让请求持久（RELIABLE、durableBeforeAck）；判定在应答之后另开一个写事务做
/// （<see cref="DecidePendingAsync"/>），这时收件箱里已经有这条请求，也有车先前报来的全部读数。判定与答复同一次提交、提交之后才发，于是
/// 「判定先于答复持久」（向量的 <c>skip-decision-durable-before-result</c>）在构造上成立。
/// </para>
/// <para>
/// <b>读数过期就先要快照、不判。</b>逐仓读数只有 <c>SafetyStateSnapshot</c> 带；<c>SafetyStateChanged</c> 只说哪几个仓变了。车关上一个仓门就发一条
/// 变化，紧接着才可能有人按「剩下的不装」，所以申请到时读数多半已经过期：入站那一刻就要一份快照（<see cref="ReadingsAreStaleAsync"/>），
/// 快照到了、应答之后再判一次。这期间请求一直待判，车按协议不开新的仓、到点重发。
/// </para>
/// <para>
/// <b>待判的请求在这几处有出口</b>：快照到了（判出结果）；这次装货先来了结果（<c>OperationResult</c> 应答之后判，此时「已有结果」，答
/// <c>ACTION_NOT_ALLOWED_IN_STATE</c>）；车重连后以新的 messageId 重提同一个 <c>skipRequestId</c>（应答之后判）。
/// </para>
/// <para>
/// <b>业务去重按 <c>skipRequestId</c>。</b>答复的 id 由它派生（<see cref="ResultMessageId"/>）；同号同内容重放存下的那一行，不重判；同号不同内容在入站
/// 以 <c>BUSINESS_ID_CONTENT_CONFLICT</c> 拒收（<see cref="ConflictsWithEarlierAsync"/>），不进收件箱、不答 <c>RemainingBasketsSkipResult</c>。
/// </para>
/// </remarks>
internal static class RemainingBasketsSkips
{
    public const string RequestType = RemainingBasketsSkipRecords.RequestType;
    public const string ResultType = RemainingBasketsSkipRecords.ResultType;

    /// <summary>对一次按键的答复的 messageId：只由 <c>skipRequestId</c> 派生，重放与重判都落在同一行上。</summary>
    public static string ResultMessageId(string skipRequestId) =>
        JourneyPlanBuilder.StableGuid(skipRequestId, "remaining-baskets-skip-result");

    /// <summary>收件箱里的一条减篮请求。</summary>
    internal sealed record Request(
        string MessageId,
        string AgvId,
        string SkipRequestId,
        string DemandId,
        string OperationSessionId,
        string SlotOperationAttemptId,
        int[] LoadedSlots,
        string? OperatorId,
        DateTimeOffset ReceivedAt);

    /// <summary>
    /// 这条请求的 <c>skipRequestId</c> 先前以别的 messageId 到过、而内容不同：业务号内容冲突（入站据此拒收）。比的是整个 payload。
    /// </summary>
    public static async Task<bool> ConflictsWithEarlierAsync(
        ControlServerDbContext dbContext,
        string messageId,
        string agvId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string skipRequestId = payload.GetProperty("skipRequestId").GetString()!;
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == RequestType && row.MessageId != messageId &&
                          row.RequestJson.Contains(skipRequestId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        JsonNode? candidate = JsonNode.Parse(payload.GetRawText());
        foreach (ProtocolInboxRow row in rows)
        {
            using JsonDocument document = JsonDocument.Parse(row.RequestJson);
            JsonElement stored = document.RootElement.GetProperty("payload");
            if (!string.Equals(stored.GetProperty("skipRequestId").GetString(), skipRequestId, StringComparison.Ordinal))
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

    /// <summary>
    /// 这条请求还没判、而服务端手上关于它目标仓的读数已经过期：入站据此要一份快照。已经判过的、目标仓读不出来的（不是本服务端的装货）不要。
    /// </summary>
    public static async Task<bool> ReadingsAreStaleAsync(
        ControlServerDbContext dbContext,
        string agvId,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string skipRequestId = payload.GetProperty("skipRequestId").GetString()!;
        if (await dbContext.ProtocolOutbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == ResultMessageId(skipRequestId), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        string attemptId = payload.GetProperty("slotOperationAttemptId").GetString()!;
        StationOperationRow? operation = await dbContext.StationOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.SlotOperationAttemptId == attemptId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return false;
        }
        int[] targets = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
        return (await SessionSlotReadings.ReadAsync(dbContext, agvId, targets, operation.CreatedAt, cancellationToken)
            .ConfigureAwait(false)) is null;
    }

    /// <summary>
    /// 判这辆车还没答的减篮请求，暂存答复；读数过期而又非看读数不可的那几条留着待判。返回暂存的答复的 messageId（提交之后由调用方发），
    /// 与是否还有请求在等读数（入站据此按间隔再要快照，审查 M2）。
    /// </summary>
    /// <remarks>
    /// 在应答之后调用，收件箱那一次已经提交，所以这里自己开写事务（调用方已有事务就加入它）。这个库的写事务是 BEGIN IMMEDIATE，判定读到的是
    /// 此前提交的一切——这次装货的结果、别的连接上的取消与纠错、引擎的推进——判与写之间别人插不进来。
    /// </remarks>
    public static async Task<(IReadOnlyList<string> Staged, bool AwaitingReadings)> DecidePendingAsync(
        ControlServerDbContext dbContext,
        WireToGateStore store,
        string agvId,
        long sessionGeneration,
        DateTimeOffset now,
        Action<Request, RemainingBasketsSkipRule.Decision, int>? decided,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(store);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction =
            dbContext.Database.CurrentTransaction is null
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                : null;
        List<string> staged = [];
        bool awaitingReadings = false;
        foreach (Request request in await UnansweredAsync(dbContext, agvId, cancellationToken).ConfigureAwait(false))
        {
            (RemainingBasketsSkipRule.Decision? decision, int expectedBasketCount) =
                await DecideAsync(dbContext, request, cancellationToken).ConfigureAwait(false);
            if (decision is null)
            {
                awaitingReadings = true;
                continue;
            }
            await OnboardJourneyPublisher.StageRemainingBasketsSkipResultAsync(
                store,
                ResultMessageId(request.SkipRequestId),
                agvId,
                sessionGeneration,
                request.SkipRequestId,
                request.DemandId,
                request.SlotOperationAttemptId,
                decision,
                now,
                cancellationToken).ConfigureAwait(false);
            decided?.Invoke(request, decision, expectedBasketCount);
            staged.Add(ResultMessageId(request.SkipRequestId));
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return (staged, awaitingReadings);
    }

    /// <summary>
    /// 这次装货有一条减篮请求在待判，或者已经授权过：此时申请取消一律拒（wire-notes 减篮一节：「A cancellation asked for while a skip is pending
    /// or authorized is refused」）。待判的请求按车的说法至少装进了一篮；已授权的，服务端自己读到至少一篮。
    /// </summary>
    public static async Task<bool> HoldsCancellationAsync(
        ControlServerDbContext dbContext,
        string demandId,
        string slotOperationAttemptId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        if (await RemainingBasketsSkipRecords.AuthorizedSlotsAsync(dbContext, demandId, slotOperationAttemptId, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            return true;
        }
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == RequestType && row.RequestJson.Contains(slotOperationAttemptId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (Request request in rows.Select(Parse))
        {
            if (request.DemandId == demandId && request.SlotOperationAttemptId == slotOperationAttemptId &&
                !await dbContext.ProtocolOutbox.AsNoTracking()
                    .AnyAsync(row => row.MessageId == ResultMessageId(request.SkipRequestId), cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>这辆车还没确认的减篮答复，给握手之后的补发与每次判定之后的发送。</summary>
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

    /// <summary>
    /// 一条请求的判定，与需求的 N；读数过期而非看读数不可时判定为 null（待判）。
    /// </summary>
    private static async Task<(RemainingBasketsSkipRule.Decision? Decision, int ExpectedBasketCount)> DecideAsync(
        ControlServerDbContext dbContext,
        Request request,
        CancellationToken cancellationToken)
    {
        StationOperationRow? operation = await dbContext.StationOperations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.SlotOperationAttemptId == request.SlotOperationAttemptId, cancellationToken)
            .ConfigureAwait(false);
        JourneyDemandRow? membership = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.DemandId == request.DemandId && row.RemovedAt == null &&
                       row.LoadSlotOperationAttemptId == request.SlotOperationAttemptId,
                cancellationToken).ConfigureAwait(false);
        JourneyRuntimeRow? runtime = membership is null ? null : await dbContext.JourneyRuntimes.AsNoTracking()
            .SingleOrDefaultAsync(row => row.JourneyId == membership.JourneyId, cancellationToken).ConfigureAwait(false);
        JourneyStopRow? currentStop = null;
        bool current = false;
        if (operation is not null && membership is not null && runtime is not null &&
            runtime.AgvId == request.AgvId && runtime.Stage == JourneyRuntimeStage.AwaitingLoadResult &&
            operation.DemandId == request.DemandId && operation.OperationType == SlotOperationType.Load)
        {
            JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
            currentStop = stops.Current;
            current = currentStop.StopId == membership.PickupStopId &&
                      stops.LoadingAtCurrentStop?.Demand.DemandId == request.DemandId;
        }
        int expectedBasketCount = membership?.ExpectedBasketCount ?? 0;
        int[] targets = operation is null ? [] : JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
        string? notAllowed = current
            ? await NotAllowedInStateAsync(dbContext, request, operation!, cancellationToken).ConfigureAwait(false)
            : null;
        RemainingBasketsSkipRule.Facts preliminary = new(
            current,
            current && string.Equals(currentStop!.OperationSessionId, request.OperationSessionId, StringComparison.Ordinal),
            expectedBasketCount,
            notAllowed,
            targets,
            new Dictionary<int, RemainingBasketsSkipRule.SlotReading>(),
            request.LoadedSlots);
        RemainingBasketsSkipRule.Decision early = RemainingBasketsSkipRule.Decide(preliminary);
        if (early.Problem is { } problem && problem.ReasonCode != ServerReasonCodes.SlotStateUnknown)
        {
            // Decided before any reading is consulted: the slot checks come after these in the protocol's order.
            return (early, expectedBasketCount);
        }
        IReadOnlyDictionary<int, RemainingBasketsSkipRule.SlotReading>? readings =
            await SessionSlotReadings.ReadAsync(dbContext, request.AgvId, targets, operation!.CreatedAt, cancellationToken)
                .ConfigureAwait(false);
        if (readings is null)
        {
            return (null, expectedBasketCount);
        }
        return (RemainingBasketsSkipRule.Decide(preliminary with { Readings = readings }), expectedBasketCount);
    }

    /// <summary>
    /// 装货此刻不接受减篮的原因（wire-notes 的 <c>ACTION_NOT_ALLOWED_IN_STATE</c> 那一格），没有为 null。屏上原样显示，所以不写票号。
    /// </summary>
    private static async Task<string?> NotAllowedInStateAsync(
        ControlServerDbContext dbContext,
        Request request,
        StationOperationRow operation,
        CancellationToken cancellationToken)
    {
        if (operation.Status != StationOperationStatus.Prepared ||
            await dbContext.OperationResults.AsNoTracking()
                .AnyAsync(row => row.SlotOperationAttemptId == request.SlotOperationAttemptId, cancellationToken)
                .ConfigureAwait(false))
        {
            return operation.Status == StationOperationStatus.RecoveryRequired
                ? "这次装货已转人工处理，不能申请剩下的不装。"
                : "这次装货已经结束，不能再申请剩下的不装。";
        }
        if (await dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking().AnyAsync(
                row => row.SlotOperationAttemptId == request.SlotOperationAttemptId &&
                       (row.State == SlotFaultDeclarationStates.Pending || row.State == SlotFaultDeclarationStates.Applied ||
                        row.State == SlotFaultDeclarationStates.Unreconciled),
                cancellationToken).ConfigureAwait(false) ||
            await dbContext.ExceptionRecoverySessions.AsNoTracking().AnyAsync(
                row => row.AgvId == request.AgvId && row.State != "CLOSED", cancellationToken).ConfigureAwait(false))
        {
            return "这次装货已转人工处理，不能申请剩下的不装。";
        }
        if (await dbContext.RecoveryWorkflows.AsNoTracking().AnyAsync(
                row => row.DemandId == request.DemandId &&
                       (row.WorkflowType == "LOAD_CORRECTION" || row.WorkflowType == "LOAD_CANCELLATION") &&
                       row.State != RecoveryWorkflowState.Reconciled && row.State != RecoveryWorkflowState.HistoricalOnly,
                cancellationToken).ConfigureAwait(false))
        {
            return "这条需求的纠错或取消正在处理，不能同时申请剩下的不装。";
        }
        if (await RemainingBasketsSkipRecords.AuthorizedSlotsAsync(
                dbContext, request.DemandId, request.SlotOperationAttemptId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return "这次装货已经同意过剩下的不装。";
        }
        return null;
    }

    /// <summary>
    /// 这辆车还没有答复的减篮请求，按收件时刻；同一个 <c>skipRequestId</c> 只给最早那一条。
    /// </summary>
    private static async Task<IReadOnlyList<Request>> UnansweredAsync(
        ControlServerDbContext dbContext,
        string agvId,
        CancellationToken cancellationToken)
    {
        ProtocolInboxRow[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == RequestType)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> answered = new(
            await dbContext.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == ResultType)
                .Select(row => row.MessageId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.Ordinal);
        List<Request> unanswered = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ProtocolInboxRow row in rows.OrderBy(row => row.ReceivedAt).ThenBy(row => row.MessageId, StringComparer.Ordinal))
        {
            Request request = Parse(row);
            if (request.AgvId == agvId && !answered.Contains(ResultMessageId(request.SkipRequestId)) &&
                seen.Add(request.SkipRequestId))
            {
                unanswered.Add(request);
            }
        }
        return unanswered;
    }

    private static Request Parse(ProtocolInboxRow row)
    {
        using JsonDocument document = JsonDocument.Parse(row.RequestJson);
        JsonElement root = document.RootElement;
        JsonElement payload = root.GetProperty("payload");
        JsonElement @operator = payload.GetProperty("operator");
        return new Request(
            row.MessageId,
            root.GetProperty("agvId").GetString()!,
            payload.GetProperty("skipRequestId").GetString()!,
            payload.GetProperty("demandId").GetString()!,
            payload.GetProperty("operationSessionId").GetString()!,
            payload.GetProperty("slotOperationAttemptId").GetString()!,
            [.. payload.GetProperty("loadedSlots").EnumerateArray().Select(item => item.GetInt32())],
            @operator.ValueKind == JsonValueKind.Object ? @operator.GetProperty("operatorId").GetString() : null,
            row.ReceivedAt);
    }
}
