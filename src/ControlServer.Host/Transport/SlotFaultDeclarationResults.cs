using System.Text.Json;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Transport;

public enum SlotFaultDeclarationResultDisposition
{
    /// <summary>The first result for the declaration: recorded.</summary>
    Recorded,

    /// <summary>The declaration already has its result; this one changes nothing.</summary>
    AlreadyAnswered,

    /// <summary>No declaration of this server has that id; acknowledged and changes nothing.</summary>
    UnknownDeclaration,

    /// <summary>The result names another attempt than the declaration; acknowledged, and the declaration stays pending.</summary>
    AttemptMismatch
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
/// acknowledged, so refusing one by throwing would drop the connection and loop on every reconnect. A result for a
/// declaration this server never made, or for another attempt than the declaration's, is acknowledged, logged and changes
/// nothing; the inbox keeps the line. A malformed one -- a required field missing, or an <c>outcome</c> outside the
/// schema's two values -- does throw <see cref="InvalidDataException"/>, which ends the session like any other message
/// the server cannot read; such a line is a contract violation, not a result to record.
/// </para>
/// <para>
/// <b>The first answer is the record</b> (business deduplication on <c>declarationId</c>): a second result for an answered
/// declaration, under another <c>messageId</c> or with another outcome, changes nothing.
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

        SlotFaultDeclarationRow? declaration = await dbContext.Set<SlotFaultDeclarationRow>()
            .SingleOrDefaultAsync(
                row => row.DeclarationId == declarationId && row.AgvId == agvId,
                cancellationToken).ConfigureAwait(false);
        SlotFaultDeclarationResultDisposition disposition =
            declaration is null ? SlotFaultDeclarationResultDisposition.UnknownDeclaration
            : declaration.State != SlotFaultDeclarationStates.Pending ? SlotFaultDeclarationResultDisposition.AlreadyAnswered
            : declaration.SlotOperationAttemptId != attemptId ? SlotFaultDeclarationResultDisposition.AttemptMismatch
            : SlotFaultDeclarationResultDisposition.Recorded;
        if (disposition == SlotFaultDeclarationResultDisposition.AlreadyAnswered)
        {
            // A resend, or a store a build before control-server#384 answered without settling: the command is settled
            // all the same, the declaration stays as first answered.
            await store.SettleAnsweredCommandAsync(declaration!.CommandMessageId, receivedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        if (disposition != SlotFaultDeclarationResultDisposition.Recorded)
        {
            LogUnmatched(logger, agvId, declarationId, disposition.ToString(), null);
            return disposition;
        }

        declaration!.State = state;
        declaration.ResultMessageId = resultMessageId;
        declaration.ResultOutcome = outcome;
        declaration.ResultProblemJson =
            payload.TryGetProperty("problem", out JsonElement problem) && problem.ValueKind == JsonValueKind.Object
                ? problem.GetRawText()
                : null;
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

    private static string Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"SlotFaultDeclarationResult payload.{property} is required.");
}
