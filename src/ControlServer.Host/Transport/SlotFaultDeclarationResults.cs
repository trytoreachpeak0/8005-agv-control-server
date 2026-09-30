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

    public static async Task<SlotFaultDeclarationResultDisposition> RecordAsync(
        ControlServerDbContext dbContext,
        string agvId,
        string resultMessageId,
        JsonElement payload,
        DateTimeOffset receivedAt,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
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
    /// The replay itself sends only outbox rows that are neither acknowledged nor fenced, so a command the vehicle
    /// acknowledged is not sent again: having acknowledged it, the vehicle owes the result, and resends that itself.
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

    private static string Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"SlotFaultDeclarationResult payload.{property} is required.");
}
