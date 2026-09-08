using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Commands;

/// <summary>Who the command is aimed at. The three identities are not interchangeable.</summary>
/// <param name="AgvId">8005's own identity for the vehicle.</param>
/// <param name="UpperId">
/// 8005's frozen order identity — what the audit trail is keyed on, and what reconciliation looks
/// the order up by. RIoT enforces its uniqueness itself (BC-ORDER-004).
/// </param>
/// <param name="OrderId">
/// RIoT's own order identity, which is what the command endpoint addresses. It is knowable only
/// after RIoT has assigned it, which is why it is separate from <paramref name="UpperId"/> rather
/// than derived from it.
/// </param>
public sealed record RiotOrderCommandTarget(string AgvId, string UpperId, string OrderId);

/// <summary>One command, as issued and as reconciled.</summary>
public sealed record RiotOrderCommandRecord(
    RiotOrderCommandAttempt Attempt,
    RiotCommandCallResult Call,
    RiotOrderObservation Observation,
    RiotOrderCommandOutcome Outcome)
{
    /// <summary>
    /// True only when reconciliation confirmed the command's intended state. An accepted call is
    /// not this.
    /// </summary>
    public bool Succeeded => Outcome == RiotOrderCommandOutcome.Confirmed;
}

/// <summary>
/// The four order commands, each armed before it goes out and reconciled after.
/// </summary>
/// <remarks>
/// <para>
/// <b>A command is not successful because RIoT accepted it.</b> That is the whole of
/// <c>RIoTRetryReconciliation</c> and it is not a stylistic preference: the SDK's own summary of
/// HangContinue says HTTP and business success do not prove the order left HANG, and Round 27
/// recorded an order sitting in EXECUTING through a software emergency stop. So every command here
/// ends by reading the order's state back and comparing it against what the command was for.
/// </para>
/// <para>
/// <b>The attempt is written before the call, not after.</b> An attempt sitting at
/// <see cref="RiotOrderCommandOutcome.Pending"/> is precisely the state that says "this may have
/// gone out and we do not know what happened to it" — the state that a crash between the call and
/// its answer leaves behind, and the one a write-after-success audit cannot represent at all.
/// </para>
/// <para>
/// <b>A retry is a new attempt, never an overwrite.</b> The audit table's unique index is
/// (command, target, attempt number) for that reason, so "it was called once" and "it was called
/// three times" are different rows rather than a matter of trust.
/// </para>
/// </remarks>
public sealed class RiotOrderCommandService(
    IRiotOrderCommandGateway gateway,
    IRiotOrderCommandAuditStore audit,
    IRiotMovementGateway movement,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Arms an attempt, issues the command, reads the order back, and records what it found.
    /// </summary>
    public async Task<RiotOrderCommandRecord> IssueAsync(
        RiotOrderCommandKind kind,
        RiotOrderCommandTarget target,
        string reason,
        long? faultGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        string commandType = RiotCommandTypeNames.For(kind);
        RiotOrderCommandAttempt attempt = await audit.ArmAttemptAsync(
            commandType,
            target.AgvId,
            target.UpperId,
            target.OrderId,
            SemanticHash(commandType, target, reason),
            faultGeneration,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        RiotCommandCallResult call = await gateway.IssueOrderCommandAsync(
            kind, target.OrderId, reason, cancellationToken).ConfigureAwait(false);

        RiotOrderObservation observation = await movement
            .ReconcileByUpperIdAsync(target.UpperId, cancellationToken).ConfigureAwait(false);
        RiotOrderCommandOutcome outcome = Reconcile(kind, call.Disposition, observation);

        await audit.RecordOutcomeAsync(
            attempt.CommandAuditId,
            outcome,
            ReceiptJson(call, observation, reason),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        return new RiotOrderCommandRecord(attempt with { Outcome = outcome }, call, observation, outcome);
    }

    /// <summary>
    /// Re-reads an attempt that is still <see cref="RiotOrderCommandOutcome.Pending"/> or
    /// <see cref="RiotOrderCommandOutcome.Unknown"/> and settles it if the order has since
    /// reached, or lost, the command's intended state.
    /// </summary>
    /// <remarks>
    /// Without this, "not successful until the terminal state is confirmed" would leave a trail of
    /// attempts that never resolve — which reads, over time, exactly like a trail of failures.
    /// An attempt already reconciled is returned untouched: a settled outcome is a fact about a
    /// moment, and re-deciding it later against a state the command did not cause would be a
    /// different claim.
    /// </remarks>
    public async Task<RiotOrderCommandOutcome> ReconcileAsync(
        RiotOrderCommandAttempt attempt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (attempt.Outcome is RiotOrderCommandOutcome.Confirmed or RiotOrderCommandOutcome.Failed)
        {
            return attempt.Outcome;
        }

        RiotOrderCommandKind kind = RiotCommandTypeNames.ParseOrderCommand(attempt.CommandType);
        RiotOrderObservation observation = await movement
            .ReconcileByUpperIdAsync(attempt.TargetUpperId, cancellationToken).ConfigureAwait(false);

        // The call itself is history by now; what is being re-decided is only what the order's
        // state says. Accepted is the neutral disposition for that: it lets the observation speak
        // and does not itself push the outcome to Failed or Unknown.
        RiotOrderCommandOutcome outcome = Reconcile(
            kind, RiotCommandCallDisposition.Accepted, observation);
        if (outcome == attempt.Outcome)
        {
            return outcome;
        }

        await audit.RecordOutcomeAsync(
            attempt.CommandAuditId,
            outcome,
            attempt.ReceiptJson,
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// What the order's state says about a command that was aimed at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure, and separated from the call for that reason: it is the one piece of this class that a
    /// test can exhaust. The state codes are RIoT's, named in <see cref="RiotOrderState"/>.
    /// </para>
    /// <para>
    /// <b>The observation outranks the call.</b> A command whose call timed out but whose intended
    /// state is now true is confirmed — that is the entire point of reading back rather than
    /// retrying on a lost answer. The call's disposition only decides the cases the state leaves
    /// open.
    /// </para>
    /// </remarks>
    internal static RiotOrderCommandOutcome Reconcile(
        RiotOrderCommandKind kind,
        RiotCommandCallDisposition disposition,
        RiotOrderObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        // No answer about the order, whatever the call said. "We could not look" is never a
        // verdict about state, and NotFound for an upperId we just commanded is a contradiction
        // rather than a confirmation.
        if (observation.Kind is RiotOrderObservationKind.Unknown or RiotOrderObservationKind.NotFound ||
            observation.OrderState is not int orderState)
        {
            return RiotOrderCommandOutcome.Unknown;
        }

        if (IntendedStates(kind).Contains(orderState))
        {
            return RiotOrderCommandOutcome.Confirmed;
        }

        // A terminal state that is not the intended one means the command did not achieve what it
        // was for and no longer can. That is a failure, not something to keep waiting on.
        if (IsTerminal(orderState))
        {
            return RiotOrderCommandOutcome.Failed;
        }

        return disposition switch
        {
            RiotCommandCallDisposition.Accepted => RiotOrderCommandOutcome.Pending,
            RiotCommandCallDisposition.Failed => RiotOrderCommandOutcome.Failed,
            _ => RiotOrderCommandOutcome.Unknown
        };
    }

    /// <summary>
    /// The order states that mean the command did what it was issued for.
    /// </summary>
    /// <remarks>
    /// The two continue commands accept SUCCESS as well as EXECUTING. A resumed order that has
    /// already run to completion by the time the state is read did resume — requiring EXECUTING
    /// alone would report a command that worked as one that did not, purely because the read was
    /// a few seconds late.
    /// </remarks>
    private static IReadOnlyList<int> IntendedStates(RiotOrderCommandKind kind) => kind switch
    {
        RiotOrderCommandKind.Cancel => [RiotOrderState.Cancelled],
        RiotOrderCommandKind.Hold => [RiotOrderState.Paused],
        RiotOrderCommandKind.ContinueFromHeld => [RiotOrderState.Executing, RiotOrderState.Success],
        RiotOrderCommandKind.ContinueFromHang => [RiotOrderState.Executing, RiotOrderState.Success],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown RIoT order command.")
    };

    private static bool IsTerminal(int orderState) => orderState is
        RiotOrderState.Cancelled or RiotOrderState.Failed or RiotOrderState.Success or
        RiotOrderState.Deleted or RiotOrderState.Suspended;

    /// <summary>
    /// A hash over everything that makes this command the command it is.
    /// </summary>
    /// <remarks>
    /// It includes the reason, so a second attempt issued for a different reason is visibly a
    /// different request rather than a repeat — which is what the column is for.
    /// </remarks>
    private static string SemanticHash(string commandType, RiotOrderCommandTarget target, string reason) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\u001f', commandType, target.AgvId, target.UpperId, target.OrderId, reason))))
            .ToLowerInvariant();

    private static string ReceiptJson(
        RiotCommandCallResult call,
        RiotOrderObservation observation,
        string reason) =>
        JsonSerializer.Serialize(new
        {
            reason,
            call = new
            {
                disposition = call.Disposition.ToString(),
                call.Receipt.Operation,
                call.Receipt.Classification,
                call.Receipt.HttpStatusCode,
                call.Receipt.BusinessCode,
                call.Receipt.FailureCategory,
                observedAt = call.Receipt.ObservedAt,
            },
            observation = new
            {
                kind = observation.Kind.ToString(),
                observation.OrderId,
                observation.OrderState,
            },
        });
}
