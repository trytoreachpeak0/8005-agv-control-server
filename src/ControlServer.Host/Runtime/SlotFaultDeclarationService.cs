using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>What an administrator declares, after the entry point has checked its shape.</summary>
public sealed record SlotFaultDeclarationRequest(
    string RequestId,
    string AgvId,
    int SlotNo,
    string FaultCategory,
    string Note,
    string OperatorId,
    string AdministratorRole);

public enum SlotFaultDeclarationOutcome
{
    /// <summary>Persisted with its command in one save; <see cref="SlotFaultDeclarationDecision.SentToVehicle"/> says whether it went out.</summary>
    Accepted,

    /// <summary>The same request again: answered from the declaration it created, nothing written, nothing queued.</summary>
    AlreadyAccepted,

    /// <summary>A precondition REQ-0359 names does not hold; every one that fails is listed.</summary>
    Refused,

    /// <summary>The request id was already used for a different request.</summary>
    RequestIdConflict
}

public sealed record SlotFaultDeclarationDecision(
    SlotFaultDeclarationOutcome Outcome,
    IReadOnlyList<string> Reasons,
    SlotFaultDeclarationRow? Declaration,
    bool SentToVehicle);

/// <summary>Why a declaration was refused. HTTP reasons, not protocol reason codes: nothing here reaches the vehicle.</summary>
public static class SlotFaultDeclarationRefusals
{
    /// <summary>No load or unload of the vehicle's open journey is in progress, nor one that included the slot.</summary>
    public const string NoSlotOperationInProgress = "SLOT_FAULT_NO_SLOT_OPERATION_IN_PROGRESS";

    /// <summary>The slot is not one the operation was commanded to operate.</summary>
    public const string SlotNotInOperation = "SLOT_FAULT_SLOT_NOT_IN_OPERATION";

    /// <summary>The vehicle has not reported <c>SLOT_EXPECTED_ACTION_OVERDUE</c> for the operation (REQ-0358).</summary>
    public const string ExpectedActionNotOverdue = "SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE";

    /// <summary>The vehicle reports another slot overdue: REQ-0357 opens one at a time, and that one is what it waits on.</summary>
    public const string NotTheCurrentSlot = "SLOT_FAULT_NOT_THE_CURRENT_SLOT";

    /// <summary>The operation has its result already: committed, failed determinately, or cancelled.</summary>
    public const string OperationAlreadyClosed = "SLOT_FAULT_OPERATION_ALREADY_CLOSED";

    /// <summary>The operation already needs recovery: its result was <c>UNKNOWN</c> or refused, and its journey is blocked.</summary>
    public const string OperationAlreadyUnknown = "SLOT_FAULT_OPERATION_ALREADY_UNKNOWN";

    /// <summary>A declaration on this attempt is still waiting for the vehicle's answer.</summary>
    public const string DeclarationPending = "SLOT_FAULT_DECLARATION_PENDING";

    /// <summary>
    /// A load cancellation of this attempt is authorized and waiting for the vehicle's result (control-server#384): the
    /// vehicle is proving the slots empty, and the cancellation is how this operation ends.
    /// </summary>
    public const string LoadCancellationInProgress = "SLOT_FAULT_LOAD_CANCELLATION_IN_PROGRESS";

    /// <summary>No session was ever established with the vehicle, so there is no generation to address the command to.</summary>
    public const string NoSession = "SLOT_FAULT_NO_SESSION";

    /// <summary>The request id was used before for a request with other content.</summary>
    public const string RequestIdReused = "SLOT_FAULT_REQUEST_ID_REUSED";
}

/// <summary>
/// REQ-0359's server half: judge what the server can judge, write the declaration and its
/// <c>SlotFaultDeclarationCommand</c> in one save, and send the command.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server judges what it can see; the vehicle has the last word.</b> REQ-0359's "only when" names four facts, and
/// the server holds a view of each: the operation in progress (<c>StationOperations</c>, <see cref="StationOperationStatus.Prepared"/>
/// until its result arrives), the slot's overdue alarm (<c>SLOT_EXPECTED_ACTION_OVERDUE</c> in the vehicle's latest
/// alarm snapshot), and whether the operation has closed or already gone <c>UNKNOWN</c>. That view can be a moment behind
/// the vehicle -- the operator may be shutting the door as the administrator presses the button -- so a declaration that
/// passes here can still be refused by the vehicle, and <c>NOT_APPLICABLE</c> is how that race ends without changing any
/// business state (<see cref="SlotFaultDeclarationResults"/>).
/// </para>
/// <para>
/// <b>"The current slot" is the one the overdue alarm names.</b> The server does not track which slot of a multi-slot
/// operation the vehicle is on; REQ-0357 lets only one slot be open at a time, and the alarm is raised for exactly that
/// slot, so a declaration on any other slot is refused even when that slot is one of the operation's targets.
/// </para>
/// <para>
/// <b>Readiness is not read.</b> A real onboard is not READY for the whole of a journey that carries an order of this
/// server's; a declaration is made at a station, where that holds too, so a readiness gate here would refuse every real
/// declaration while the synthetic peer, which always reports READY, passed.
/// </para>
/// <para>
/// <b>Persisted, then sent.</b> The declaration row and the outbox row go in one <c>SaveChanges</c>, so there is never a
/// declaration the vehicle will not hear about, nor a command with no declaration behind it. The command's
/// <c>messageId</c> is derived from the declaration, never from the attempt, so it can never collide with the attempt's
/// own <c>OperationResult</c>. The send afterwards is best effort: a vehicle that is not connected gets the command from the
/// replay that follows its next recovery report (<c>OnboardRecoveryCoordinator.ReplayPendingCommandsAsync</c>).
/// </para>
/// </remarks>
public sealed class SlotFaultDeclarationService(
    ControlServerDbContext dbContext,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
    TimeProvider timeProvider,
    ILogger<SlotFaultDeclarationService> logger)
{
    /// <summary>What the command's <c>messageId</c> is derived from, with the declaration id.</summary>
    public const string CommandMessagePurpose = "slot-fault-declaration-command";

    private static readonly Action<ILogger, string, int, string, string, string, Exception?> LogDeclared =
        LoggerMessage.Define<string, int, string, string, string>(
            LogLevel.Warning,
            new EventId(9500, "SlotFaultDeclared"),
            "Vehicle {AgvId} slot {SlotNo} declared faulty ({FaultCategory}) by {AdministratorId} as declaration {DeclarationId}.");

    private static readonly Action<ILogger, string, string, Exception?> LogCommandHeld =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(9501, "SlotFaultDeclarationCommandHeld"),
            "Declaration {DeclarationId} for {AgvId} is persisted; the vehicle is not connected, so its command goes out on reconnect.");

    public async Task<SlotFaultDeclarationDecision> DeclareAsync(
        SlotFaultDeclarationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string contentHash = ContentHash(request);

        if (await ReplayAsync(request, contentHash, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }

        long? generation = await dbContext.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == request.AgvId)
            .Select(row => (long?)row.SessionGeneration)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (generation is null)
        {
            return Refused([SlotFaultDeclarationRefusals.NoSession]);
        }

        (StationOperationRow? operation, string[] reasons) =
            await JudgeAsync(request, cancellationToken).ConfigureAwait(false);
        if (reasons.Length > 0)
        {
            return Refused(reasons);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string declarationId = Guid.NewGuid().ToString("D");
        string messageId = JourneyPlanBuilder.StableGuid(declarationId, CommandMessagePurpose);
        SlotFaultDeclarationRow declaration = new()
        {
            DeclarationId = declarationId,
            RequestId = request.RequestId,
            RequestContentHash = contentHash,
            AgvId = request.AgvId,
            DemandId = operation!.DemandId,
            SlotOperationAttemptId = operation.SlotOperationAttemptId,
            OperationType = operation.OperationType == SlotOperationType.Load ? "LOAD" : "UNLOAD",
            SlotNo = request.SlotNo,
            FaultCategory = request.FaultCategory,
            Note = request.Note,
            AdministratorId = request.OperatorId,
            AdministratorRole = request.AdministratorRole,
            DeclaredAt = now,
            ReadingsJson = await SlotReadings.ReadAsync(
                dbContext, request.AgvId, generation.Value, request.SlotNo, cancellationToken).ConfigureAwait(false),
            CommandMessageId = messageId,
            State = SlotFaultDeclarationStates.Pending
        };
        string wire = ProtocolEnvelope.Serialize(
            "SlotFaultDeclarationCommand",
            messageId,
            correlationId: null,
            request.AgvId,
            generation.Value,
            now,
            JsonSerializer.SerializeToElement(CommandPayload(declaration), ProtocolEnvelope.SerializerOptions));

        dbContext.Set<SlotFaultDeclarationRow>().Add(declaration);
        await store.StageOutboundEnvelopeAsync(
            messageId, "SlotFaultDeclarationCommand", wire, now, cancellationToken).ConfigureAwait(false);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Both rows were in that save, so neither is there. What decides the answer is what is in the database now: a
            // declaration another request just wrote for this attempt, or this very request written by a concurrent
            // retry. Anything else is a failure nobody here can explain, and it is not turned into a refusal.
            dbContext.ChangeTracker.Clear();
            if (await ReplayAsync(request, contentHash, cancellationToken).ConfigureAwait(false) is { } concurrent)
            {
                return concurrent;
            }
            if (await HasPendingDeclarationAsync(operation.SlotOperationAttemptId, cancellationToken).ConfigureAwait(false))
            {
                return Refused([SlotFaultDeclarationRefusals.DeclarationPending]);
            }
            throw;
        }

        LogDeclared(logger, request.AgvId, request.SlotNo, request.FaultCategory, request.OperatorId, declarationId, null);
        bool sent;
        try
        {
            await publisher.SendPersistedAsync(messageId, cancellationToken).ConfigureAwait(false);
            sent = true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // OnboardConnectionUnavailableException is an IOException: not attached, still in its handshake, or attached
            // under another session generation. The row stays unacknowledged in the outbox for the reconnect's replay.
            LogCommandHeld(logger, declarationId, request.AgvId, null);
            sent = false;
        }
        return new SlotFaultDeclarationDecision(SlotFaultDeclarationOutcome.Accepted, [], declaration, sent);
    }

    /// <summary>The command's payload, the nine fields of CP-0005 section 4.3.</summary>
    /// <remarks>
    /// <c>verificationMethod</c> is <c>SESSION</c> and <c>verifiedAt</c> the declaring moment: the verification this
    /// server performed is the shared credential checked on the call that made the declaration (CP-0005 section 8).
    /// </remarks>
    private static object CommandPayload(SlotFaultDeclarationRow declaration) => new
    {
        declarationId = declaration.DeclarationId,
        demandId = declaration.DemandId,
        slotOperationAttemptId = declaration.SlotOperationAttemptId,
        slotNo = declaration.SlotNo,
        administrator = new
        {
            operatorId = declaration.AdministratorId,
            verificationMethod = ProtocolOperatorContext.Session,
            verifiedAt = declaration.DeclaredAt
        },
        administratorRole = declaration.AdministratorRole,
        faultCategory = declaration.FaultCategory,
        note = declaration.Note,
        declaredAt = declaration.DeclaredAt
    };

    private async Task<SlotFaultDeclarationDecision?> ReplayAsync(
        SlotFaultDeclarationRequest request,
        string contentHash,
        CancellationToken cancellationToken)
    {
        SlotFaultDeclarationRow? existing = await dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.RequestId == request.RequestId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }
        return existing.RequestContentHash == contentHash && existing.AgvId == request.AgvId
            ? new SlotFaultDeclarationDecision(SlotFaultDeclarationOutcome.AlreadyAccepted, [], existing, SentToVehicle: false)
            : new SlotFaultDeclarationDecision(
                SlotFaultDeclarationOutcome.RequestIdConflict, [SlotFaultDeclarationRefusals.RequestIdReused], null, false);
    }

    /// <summary>The operation the declaration would be about, and every precondition that does not hold.</summary>
    private async Task<(StationOperationRow? Operation, string[] Reasons)> JudgeAsync(
        SlotFaultDeclarationRequest request,
        CancellationToken cancellationToken)
    {
        StationOperationRow? operation = await OperationForAsync(request.AgvId, request.SlotNo, cancellationToken)
            .ConfigureAwait(false);
        List<string> reasons = [];
        if (operation is null)
        {
            reasons.Add(SlotFaultDeclarationRefusals.NoSlotOperationInProgress);
        }
        else
        {
            int[] targets = JsonSerializer.Deserialize<int[]>(operation.TargetSlotsJson) ?? [];
            if (!targets.Contains(request.SlotNo))
            {
                reasons.Add(SlotFaultDeclarationRefusals.SlotNotInOperation);
            }
            switch (operation.Status)
            {
                case StationOperationStatus.Committed:
                case StationOperationStatus.Failed:
                case StationOperationStatus.Cancelled:
                    reasons.Add(SlotFaultDeclarationRefusals.OperationAlreadyClosed);
                    break;
                case StationOperationStatus.RecoveryRequired:
                    reasons.Add(SlotFaultDeclarationRefusals.OperationAlreadyUnknown);
                    break;
            }
            if (await HasPendingDeclarationAsync(operation.SlotOperationAttemptId, cancellationToken).ConfigureAwait(false))
            {
                reasons.Add(SlotFaultDeclarationRefusals.DeclarationPending);
            }
            // The other order of the guard OnboardRecoveryCoordinator.AuthorizeLoadCancellationAsync keeps (control-server#384,
            // approved by the coordinator beyond the ticket's untouched list): a cancellation already authorized ends this
            // attempt, so a declaration would give it a second conclusion. The onboard answers such a declaration
            // NOT_APPLICABLE (onboard-hmi#247); refused here, the administrator sees why when submitting.
            if (await HasOpenLoadCancellationAsync(operation.SlotOperationAttemptId, cancellationToken).ConfigureAwait(false))
            {
                reasons.Add(SlotFaultDeclarationRefusals.LoadCancellationInProgress);
            }
        }

        // The overdue alarm names a slot and nothing else: on the wire an alarm has one subject, and an expected-action-overdue
        // alarm's is SLOT (CP-0005 4.1; OnboardAlarmCodes.IsSlotExpectedActionOverdue requires the slot number), so it never
        // carries the attempt it was raised under. Which attempt it belongs to cannot be read off it; that it is the current
        // one rests on the snapshot being whole -- the vehicle withdraws the alarm when the slot closes -- and on the vehicle
        // refusing a declaration for an attempt that is not waiting (NOT_APPLICABLE). An attempt filter here was dead code
        // (review of control-server#383, S4); SlotFaultDeclarationTests pins the premise that made it so.
        IReadOnlyList<OnboardAlarmEntry> overdue = await new OnboardAlarmProjectionStore(dbContext, timeProvider)
            .ReadExpectedActionOverdueAsync(request.AgvId, cancellationToken).ConfigureAwait(false);
        if (overdue.Count == 0)
        {
            reasons.Add(SlotFaultDeclarationRefusals.ExpectedActionNotOverdue);
        }
        else if (!overdue.Any(alarm => alarm.PhysicalSlotNumber == request.SlotNo))
        {
            reasons.Add(SlotFaultDeclarationRefusals.NotTheCurrentSlot);
        }

        return (operation, [.. reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The slot operation of the vehicle's open journeys that is in progress; failing that, the latest one that included
    /// the slot, so a closed or UNKNOWN operation is refused for what it is rather than as "nothing in progress".
    /// </summary>
    private async Task<StationOperationRow?> OperationForAsync(string agvId, int slotNo, CancellationToken cancellationToken)
    {
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] journeyIds = [.. journeys.Select(row => row.JourneyId)];
        JourneyDemandRow[] members = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => journeyIds.Contains(row.JourneyId) && row.RemovedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] attempts =
        [
            .. journeys.SelectMany(row => new[] { row.LoadSlotOperationAttemptId, row.UnloadSlotOperationAttemptId }),
            .. members.SelectMany(row => new[] { row.LoadSlotOperationAttemptId, row.UnloadSlotOperationAttemptId })
        ];
        string[] distinct = [.. attempts.Distinct(StringComparer.Ordinal)];
        StationOperationRow[] operations = await dbContext.StationOperations.AsNoTracking()
            .Where(row => distinct.Contains(row.SlotOperationAttemptId))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        StationOperationRow[] latestFirst = [.. operations.OrderByDescending(row => row.CreatedAt)];
        return latestFirst.FirstOrDefault(row => row.Status == StationOperationStatus.Prepared)
               ?? latestFirst.FirstOrDefault(row =>
                   (JsonSerializer.Deserialize<int[]>(row.TargetSlotsJson) ?? []).Contains(slotNo));
    }

    private Task<bool> HasPendingDeclarationAsync(string attemptId, CancellationToken cancellationToken) =>
        dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking().AnyAsync(
            row => row.SlotOperationAttemptId == attemptId && row.State == SlotFaultDeclarationStates.Pending,
            cancellationToken);

    private Task<bool> HasOpenLoadCancellationAsync(string attemptId, CancellationToken cancellationToken) =>
        dbContext.RecoveryWorkflows.AsNoTracking().AnyAsync(
            row => row.SlotOperationAttemptId == attemptId &&
                   row.WorkflowType == LoadCancellationBeforeSublot.WorkflowType &&
                   row.State == RecoveryWorkflowState.AwaitingResult,
            cancellationToken);

    private static SlotFaultDeclarationDecision Refused(IReadOnlyList<string> reasons) =>
        new(SlotFaultDeclarationOutcome.Refused, reasons, null, false);

    private static string ContentHash(SlotFaultDeclarationRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            request.AgvId,
            request.SlotNo,
            request.FaultCategory,
            request.Note,
            request.OperatorId,
            request.AdministratorRole
        })))).ToLowerInvariant();
}

/// <summary>
/// The slot's readings from the vehicle's latest <c>SafetyStateSnapshot</c> of its current session, for the audit
/// (REQ-0359: "判定时车载端最近上报的读数").
/// </summary>
/// <remarks>
/// The same reading as the dashboard's expected-action-overdue card (<c>ExpectedActionOverdueQueryEndpoint</c>, REQ-0358):
/// latest by <c>safetyStateVersion</c> within the session on the session row, and a <c>SafetyStateChanged</c> for the slot
/// after that snapshot marked rather than hidden. It is written out here rather than shared because that endpoint belongs
/// to the dashboard, which this ticket does not touch (control-server#384 does); what the two must agree on is small and
/// stated above.
/// </remarks>
internal static class SlotReadings
{
    public static async Task<string?> ReadAsync(
        ControlServerDbContext dbContext,
        string agvId,
        long generation,
        int slotNo,
        CancellationToken cancellationToken)
    {
        string[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot" || row.MessageType == "SafetyStateChanged")
            .Select(row => row.RequestJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

        JsonElement? latest = null;
        long latestVersion = long.MinValue;
        List<(long Version, int[] Slots)> changes = [];
        foreach (string json in rows)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("agvId", out JsonElement agv) || agv.GetString() != agvId ||
                !root.TryGetProperty("sessionGeneration", out JsonElement session) ||
                session.ValueKind != JsonValueKind.Number || session.GetInt64() != generation)
            {
                continue;
            }
            JsonElement payload = root.GetProperty("payload");
            long version = payload.GetProperty("safetyStateVersion").GetInt64();
            if (root.GetProperty("messageType").GetString() == "SafetyStateSnapshot")
            {
                if (version > latestVersion)
                {
                    latestVersion = version;
                    latest = payload.Clone();
                }
            }
            else if (payload.TryGetProperty("affectedSlots", out JsonElement affected) &&
                     affected.ValueKind == JsonValueKind.Array)
            {
                changes.Add((version, [.. affected.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Number)
                    .Select(item => item.GetInt32())]));
            }
        }

        if (latest is not JsonElement snapshot ||
            !snapshot.TryGetProperty("slotStates", out JsonElement slotStates) ||
            slotStates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement state in slotStates.EnumerateArray())
        {
            if (state.TryGetProperty("slotNo", out JsonElement number) && number.GetInt32() == slotNo)
            {
                return JsonSerializer.Serialize(new
                {
                    observedAt = snapshot.GetProperty("observedAt").GetDateTimeOffset(),
                    safetyStateVersion = latestVersion,
                    lockState = state.GetProperty("lockState").GetString(),
                    physicalState = state.GetProperty("physicalState").GetString(),
                    unlockOutputState = state.GetProperty("unlockOutputState").GetString(),
                    changedSinceObserved = changes.Any(change => change.Version > latestVersion && change.Slots.Contains(slotNo))
                });
            }
        }
        return null;
    }
}
