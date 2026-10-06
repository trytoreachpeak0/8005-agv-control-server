using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Transport;

public enum SlotFaultDeclarationResultDisposition
{
    /// <summary>The first result for the declaration: recorded.</summary>
    Recorded,

    /// <summary>The declaration already has this very answer (same outcome, attempt and problem); this one changes nothing.</summary>
    AlreadyAnswered,

    /// <summary>No declaration of this server has that id; acknowledged and changes nothing.</summary>
    UnknownDeclaration,

    /// <summary>The result names another attempt than the declaration; refused, and the declaration stays pending.</summary>
    AttemptMismatch,

    /// <summary>The declaration already has another answer, or none the vehicle can still give (control-server#481); refused.</summary>
    AnswerConflict,

    /// <summary>The declaration is another vehicle's (control-server#481); refused, and it is left as it was.</summary>
    AnotherVehicle
}

/// <summary>
/// The inbound half of REQ-0359: <c>SlotFaultDeclarationResult</c>, and which declaration commands a reconnect replays.
/// </summary>
/// <remarks>
/// <para>
/// <b>A result settles the declaration, never the operation.</b> <c>APPLIED</c> means the vehicle stopped the operation;
/// what the operation came to arrives separately, as the <c>OperationResult</c> the vehicle sends afterwards, and that
/// is settled by the path every result takes (<c>WireToGateStore.ApplyOperationResultAsync</c>: an <c>UNKNOWN</c> slot
/// puts the operation and the demand into <c>RecoveryRequired</c>, and the runtime blocks the journey). <c>NOT_APPLICABLE</c>
/// withdraws the declaration and changes no business state (REQ-0359). Since neither answer writes to the demand, the
/// operation or the journey, the two messages can arrive in either order without a settlement running twice or not at all.
/// </para>
/// <para>
/// <b>A well-formed result never ends the session.</b> The message is RELIABLE and the vehicle resends it until it is
/// answered. A result for a declaration this server never made is acknowledged, logged and changes nothing; the inbox keeps
/// the line. A result that contradicts what this server holds -- another attempt than the declaration's, another answer for
/// an answered declaration, another vehicle's declaration -- is refused with <c>BUSINESS_ID_CONTENT_CONFLICT</c> through
/// <see cref="InboundMessageRejectedException"/>: a <c>ProtocolProblem</c> correlated to it, the connection kept
/// (control-server#478, #481). That code is one of the four the vehicle gives a row up on (onboard-hmi#254), so it is not
/// resent. A malformed one -- a required field missing, or an <c>outcome</c> outside the schema's two values -- does throw
/// <see cref="InvalidDataException"/>, which ends the session like any other message the server cannot read; such a line is
/// a contract violation, not a result to record.
/// </para>
/// <para>
/// <b>The first answer is the record</b> (business deduplication on <c>declarationId</c>): a second result with the same
/// content -- outcome, attempt and problem -- under another <c>messageId</c> changes nothing and is acknowledged.
/// </para>
/// <para>
/// <b>A refused answer still has to end the wait</b> (control-server#481). Refused, the answer is given up on the vehicle and
/// the declaration it was for may still be pending -- it is when the answer named another attempt, or when the inbox refused
/// it as <c>MESSAGE_ID_CONTENT_CONFLICT</c> before it reached here -- so the command is replayed on every reconnect. The
/// vehicle refuses that replayed command with <c>SLOT_OPERATION_CONFLICT</c>, and <see cref="ObserveCommandRefusedAsync"/>
/// takes that as the end: the declaration becomes <see cref="SlotFaultDeclarationStates.Unreconciled"/>.
/// </para>
/// </remarks>
public static class SlotFaultDeclarationResults
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogRecorded =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(9502, "SlotFaultDeclarationAnswered"),
            "Vehicle {AgvId} answered slot fault declaration {DeclarationId}: {Outcome}.");

    private static readonly Action<ILogger, string, string, string, Exception?> LogUnmatched =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(9503, "SlotFaultDeclarationResultUnmatched"),
            "Vehicle {AgvId} sent a slot fault declaration result for {DeclarationId} that changes nothing: {Disposition}.");

    private static readonly Action<ILogger, string, string, string, string, string, string, Exception?> LogRefused =
        LoggerMessage.Define<string, string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(9507, "SlotFaultDeclarationResultRefused"),
            "Vehicle {AgvId} sent a slot fault declaration result for {DeclarationId} (attempt {AttemptId}, outcome {Outcome}, " +
            "message {MessageId}) that contradicts this server's record: {Disposition}. Refused with BUSINESS_ID_CONTENT_CONFLICT; " +
            "the vehicle gives that answer up (control-server#481).");

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> LogUnreconciled =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Warning,
            new EventId(9508, "SlotFaultDeclarationUnreconciled"),
            "Vehicle {AgvId} refused the replayed command {CommandMessageId} of slot fault declaration {DeclarationId} " +
            "(attempt {AttemptId}) with {ReasonCode}: it gave its answer up. The declaration is UNRECONCILED -- not replayed, " +
            "and its attempt's load cancellation stays refused; what the vehicle made of it is unknown and needs a person " +
            "(control-server#481).");

    private static readonly Action<ILogger, int, Exception?> LogBackfilled =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(9505, "SlotFaultDeclarationCommandsSettledAtStartup"),
            "Settled {Count} slot fault declaration command(s) answered before the result settled its command (control-server#384).");

    public static async Task<SlotFaultDeclarationResultDisposition> RecordAsync(
        ControlServerDbContext dbContext,
        WireToGateStore store,
        string agvId,
        string resultMessageId,
        JsonElement payload,
        DateTimeOffset receivedAt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        string declarationId = Required(payload, "declarationId");
        string attemptId = Required(payload, "slotOperationAttemptId");
        string outcome = Required(payload, "outcome");
        string state = outcome switch
        {
            "APPLIED" => SlotFaultDeclarationStates.Applied,
            "NOT_APPLICABLE" => SlotFaultDeclarationStates.NotApplicable,
            _ => throw new InvalidDataException("SlotFaultDeclarationResult outcome is not supported.")
        };

        string? problemJson =
            payload.TryGetProperty("problem", out JsonElement problem) && problem.ValueKind == JsonValueKind.Object
                ? problem.GetRawText()
                : null;

        // By id alone (control-server#481): another vehicle's declaration is a known id, refused, not an unknown one.
        SlotFaultDeclarationRow? declaration = await dbContext.Set<SlotFaultDeclarationRow>()
            .SingleOrDefaultAsync(row => row.DeclarationId == declarationId, cancellationToken).ConfigureAwait(false);
        SlotFaultDeclarationResultDisposition disposition =
            declaration is null ? SlotFaultDeclarationResultDisposition.UnknownDeclaration
            : declaration.AgvId != agvId ? SlotFaultDeclarationResultDisposition.AnotherVehicle
            : declaration.State != SlotFaultDeclarationStates.Pending
                ? SameAnswer(declaration, attemptId, outcome, problemJson)
                    ? SlotFaultDeclarationResultDisposition.AlreadyAnswered
                    : SlotFaultDeclarationResultDisposition.AnswerConflict
            : declaration.SlotOperationAttemptId != attemptId ? SlotFaultDeclarationResultDisposition.AttemptMismatch
            : SlotFaultDeclarationResultDisposition.Recorded;
        if (disposition == SlotFaultDeclarationResultDisposition.AlreadyAnswered)
        {
            // A resend, or a store a build before control-server#384 answered without settling: the command is settled
            // all the same, the declaration stays as first answered.
            await store.SettleAnsweredCommandAsync(declaration!.CommandMessageId, receivedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        if (disposition is SlotFaultDeclarationResultDisposition.AttemptMismatch
            or SlotFaultDeclarationResultDisposition.AnswerConflict
            or SlotFaultDeclarationResultDisposition.AnotherVehicle)
        {
            LogRefused(logger, agvId, declarationId, attemptId, outcome, resultMessageId, disposition.ToString(), null);
            throw new InboundMessageRejectedException(
                ServerReasonCodes.BusinessIdContentConflict,
                $"SlotFaultDeclarationResult for {declarationId} contradicts what this server holds for it: {disposition}.");
        }
        if (disposition != SlotFaultDeclarationResultDisposition.Recorded)
        {
            LogUnmatched(logger, agvId, declarationId, disposition.ToString(), null);
            return disposition;
        }

        declaration!.State = state;
        declaration.ResultMessageId = resultMessageId;
        declaration.ResultOutcome = outcome;
        declaration.ResultProblemJson = problemJson;
        declaration.ResultReceivedAt = receivedAt;
        // The result is the command's answer (control-server#384): store and declaration share the context, so the settle's
        // save carries the declaration too. The save after it covers a command line that is gone or already settled, where
        // the settle returns without saving.
        await store.SettleAnsweredCommandAsync(declaration.CommandMessageId, receivedAt, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogRecorded(logger, agvId, declarationId, outcome, null);
        return disposition;
    }

    /// <summary>
    /// The vehicle refusing a message of this server with a <c>ProtocolProblem</c>: when that message is the command of one
    /// of this vehicle's pending declarations and the code is <c>SLOT_OPERATION_CONFLICT</c>, the vehicle has given its
    /// answer to the declaration up (control-server#481), and the declaration becomes
    /// <see cref="SlotFaultDeclarationStates.Unreconciled"/>. Returns whether it did; saves only then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is needed.</b> A refused answer is given up on the vehicle (onboard-hmi#254) and never sent again. A
    /// declaration still pending after that -- its answer named another attempt, or the inbox refused it -- would have its
    /// command replayed on every reconnect, would hold back its attempt's load cancellation and keep a second declaration off
    /// the attempt, with nothing to end it short of a database edit. The vehicle answers the replay instead with this refusal
    /// (onboard-hmi#266), which is what ends it here.
    /// </para>
    /// <para>
    /// <b>Only that code.</b> It is the one the vehicle gives up with, <c>MANUAL_REVIEW</c>: the two ends disagree about this
    /// slot operation and a person has to look. Any other code -- one that means "later", say -- leaves the declaration
    /// pending and its command replayed, as does a refusal naming another vehicle's declaration.
    /// </para>
    /// <para>
    /// <b>What it changes.</b> The declaration and its command's outbox line, nothing of the business. The answer that would
    /// have said what the vehicle did is gone, so it is held to what an applied one holds back -- the attempt's load
    /// cancellation (<c>OnboardRecoveryCoordinator</c>): if the vehicle applied it, it refuses a cancellation without a word.
    /// The attempt can be declared again (the pending index no longer holds it), and the operation still ends through its
    /// <c>OperationResult</c> as every one does.
    /// </para>
    /// </remarks>
    public static async Task<bool> ObserveCommandRefusedAsync(
        ControlServerDbContext dbContext,
        WireToGateStore store,
        string agvId,
        string refusalMessageId,
        string rejectedMessageId,
        JsonElement problem,
        DateTimeOffset receivedAt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        string? reasonCode = problem.ValueKind == JsonValueKind.Object &&
                             problem.TryGetProperty("reasonCode", out JsonElement code) &&
                             code.ValueKind == JsonValueKind.String
            ? code.GetString()
            : null;
        if (reasonCode != ServerReasonCodes.SlotOperationConflict)
        {
            return false;
        }
        SlotFaultDeclarationRow? declaration = await dbContext.Set<SlotFaultDeclarationRow>()
            .SingleOrDefaultAsync(
                row => row.AgvId == agvId && row.CommandMessageId == rejectedMessageId &&
                       row.State == SlotFaultDeclarationStates.Pending,
                cancellationToken).ConfigureAwait(false);
        if (declaration is null)
        {
            return false;
        }

        declaration.State = SlotFaultDeclarationStates.Unreconciled;
        declaration.ResultMessageId = refusalMessageId;
        declaration.ResultProblemJson = problem.GetRawText();
        declaration.ResultReceivedAt = receivedAt;
        // As for a result (control-server#384): the settle's save carries the declaration, the save after it covers a command
        // line that is gone or already settled.
        await store.SettleAnsweredCommandAsync(declaration.CommandMessageId, receivedAt, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        LogUnreconciled(
            logger, agvId, rejectedMessageId, declaration.DeclarationId, declaration.SlotOperationAttemptId, reasonCode, null);
        return true;
    }

    /// <summary>
    /// The commands of this vehicle's declarations that have no answer yet, for the replay after its recovery report; none
    /// while the entry point is switched off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only unanswered declarations are replayed. The vehicle never sends a DurableAck for the command -- its answer is the
    /// result, which settles the command's outbox line (<see cref="RecordAsync"/>, control-server#384) -- so a command the
    /// vehicle took but has not yet answered is replayed, and the vehicle answers it again from its own record.
    /// </para>
    /// <para>
    /// <b>Switched off means not replayed either</b> (review of control-server#383, S1). A site that turned the entry point
    /// on and then off again may still hold PENDING declarations; replaying them would put the command in front of an
    /// onboard that may not know it, which ends that vehicle's session on every reconnect. They stay in the table and the
    /// outbox, unsent, and the host warns about them at startup (<see cref="SlotFaultDeclarationStartupCheck"/>).
    /// </para>
    /// </remarks>
    public static async Task<string[]> PendingCommandMessageIdsAsync(
        ControlServerDbContext dbContext,
        IConfiguration configuration,
        string agvId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!ControlServer.Host.Runtime.SlotFaultDeclarationOptions.IsEnabled(configuration))
        {
            return [];
        }
        return await dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking()
            .Where(row => row.AgvId == agvId && row.State == SlotFaultDeclarationStates.Pending)
            .Select(row => row.CommandMessageId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles the commands of declarations that already have their answer and whose outbox line is still unsettled --
    /// what a build before control-server#384 left behind. Returns how many lines it settled. Idempotent: the host runs it on
    /// every start, before <see cref="ProtocolOutboxIdentityStartupCheck"/> reads the outbox.
    /// </summary>
    /// <remarks>
    /// A declaration still <see cref="SlotFaultDeclarationStates.Pending"/> keeps its command unsettled: the vehicle owes
    /// that answer, the reconnect replay may still need the line, and the identity check is right to stop on it.
    /// </remarks>
    public static async Task<int> SettleAnsweredCommandsAsync(
        ControlServerDbContext dbContext, DateTimeOffset settledAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        string[] answered = await dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking()
            .Where(row => row.State != SlotFaultDeclarationStates.Pending)
            .Select(row => row.CommandMessageId)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (answered.Length == 0)
        {
            return 0;
        }
        ProtocolOutboxRow[] unsettled = await dbContext.ProtocolOutbox
            .Where(row => answered.Contains(row.MessageId) && row.AcknowledgedAt == null)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (ProtocolOutboxRow row in unsettled)
        {
            row.AcknowledgedAt = settledAt;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return unsettled.Length;
    }

    /// <summary>The host's entry point for <see cref="SettleAnsweredCommandsAsync(ControlServerDbContext, DateTimeOffset, CancellationToken)"/>.</summary>
    public static async Task SettleAnsweredCommandsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        int settled = await SettleAnsweredCommandsAsync(
                provider.GetRequiredService<ControlServerDbContext>(),
                (provider.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (settled > 0)
        {
            LogBackfilled(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SlotFaultDeclarationResults).FullName!),
                settled,
                null);
        }
    }

    /// <summary>
    /// Whether a result says what the answered declaration already holds: the same outcome, the declaration's attempt, and
    /// the same problem, two absent problems counting as the same. An unreconciled declaration holds no outcome, so no
    /// result is its answer.
    /// </summary>
    private static bool SameAnswer(SlotFaultDeclarationRow declaration, string attemptId, string outcome, string? problemJson) =>
        declaration.ResultOutcome == outcome &&
        declaration.SlotOperationAttemptId == attemptId &&
        JsonNode.DeepEquals(
            declaration.ResultProblemJson is null ? null : JsonNode.Parse(declaration.ResultProblemJson),
            problemJson is null ? null : JsonNode.Parse(problemJson));

    private static string Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"SlotFaultDeclarationResult payload.{property} is required.");
}
