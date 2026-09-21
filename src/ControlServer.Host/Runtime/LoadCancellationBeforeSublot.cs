using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// The facts the cancellation before a sublot is entered (ADR-cross-0046, first case; control-server#83)
/// shares between the two places that decide a stop in <see cref="JourneyRuntimeStage.AwaitingSublot"/>:
/// the recovery coordinator, which authorizes the cancellation, and the runtime, which starts the load or
/// ends the stop at its deadline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exclusive with the entry, the first persisted winning.</b> The entry and the cancellation request
/// arrive on the same connection and are made durable one after the other, so their order in the store is
/// their order on the wire. The coordinator refuses a cancellation once an entry for the stop is durable
/// (<see cref="IsEntryForStop"/>); the runtime reads the inbox first and the open cancellation second
/// (<see cref="HasOpenCancellationAsync"/>), so an entry it can see was persisted after a cancellation it
/// can see as well. Reading them the other way round would leave a window in which both a load and the
/// cancellation go ahead.
/// </para>
/// <para>
/// <b>Exclusive with an entry that was refused, and there the entry loses.</b> Since
/// <c>8005-agv-control-server#82</c> an entry the server refuses is answered with a
/// <c>SublotRejected</c> instead of a load, so "an entry for the stop" is no longer the same thing as "a
/// load was commanded for it". Both readers exclude the submissions already refused
/// (<see cref="RefusedSubmissionIdsAsync"/>) before either question is asked, which is what keeps the two
/// refusals consistent with each other rather than split by whether the entered sublot happened to be the
/// demand's.
/// </para>
/// <para>
/// <b>Exclusive with the station deadline.</b> An open cancellation holds the stop: the runtime neither
/// starts a load nor ends the stop while the vehicle is proving there is nothing to unload. The stop ends
/// on the vehicle's ALL_EMPTY result instead, through the same <see cref="PickupStopTermination"/> the
/// deadline uses, or is blocked for recovery by any other result. Neither side trusts an earlier unlocked
/// read for this: the authorization runs inside the inbox's write transaction, and the deadline re-reads
/// the stop and its cancellations inside one of its own before ending it (BEGIN IMMEDIATE on this store).
/// The settlement checks the stop once more, and leaves a stop that has already ended as it ended.
/// </para>
/// </remarks>
public static class LoadCancellationBeforeSublot
{
    public const string WorkflowType = "LOAD_CANCELLATION";

    /// <summary>
    /// Whether the demand has a load cancellation still waiting on the vehicle's result. A settled one
    /// has left <see cref="RecoveryWorkflowState.AwaitingResult"/> and moved the journey on with it.
    /// </summary>
    public static Task<bool> HasOpenCancellationAsync(
        ControlServerDbContext dbContext,
        string demandId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        return dbContext.RecoveryWorkflows.AsNoTracking().AnyAsync(
            row => row.DemandId == demandId &&
                   row.WorkflowType == WorkflowType &&
                   row.State == RecoveryWorkflowState.AwaitingResult,
            cancellationToken);
    }

    /// <summary>
    /// Whether an inbound <c>SublotSubmitted</c> answers the entry request the stop has open: the same
    /// vehicle, the stop's operation session, station and worklist revision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The address is the stop's, not the journey row's</b> (control-server#211). It used to read four columns off
    /// the journey row, one of which -- the worklist revision -- had a single value per journey; a stop that publishes
    /// several worklist revisions makes that one a range, and the type carries it so neither reader compares its own
    /// selection of columns.
    /// </para>
    /// <para>
    /// <b>Deliberately blind to the session generation</b>, and the two readers want it differently. The
    /// runtime acts only on an answer of the current generation and checks that itself; the cancellation
    /// refuses on an entry of any generation, because an entry made before a reconnect may be the one the
    /// runtime is loading while the cancellation arrives on the next connection (control-server#116
    /// review). This is the one definition of the address both read; the runtime adds the generation, the
    /// cancellation adds nothing.
    /// </remarks>
    internal static bool AnswersTheStop(JsonElement submission, StopEntryAddress address)
    {
        JsonElement payload = submission.GetProperty("payload");
        return submission.GetProperty("agvId").GetString() == address.AgvId &&
               payload.GetProperty("operationSessionId").GetString() == address.OperationSessionId &&
               payload.GetProperty("stationId").GetString() == address.StationId &&
               address.Covers(payload.GetProperty("worklistRevision").GetInt64());
    }

    /// <summary>
    /// Whether an inbound <c>SublotSubmitted</c> is an entry for this stop: one that answers its entry
    /// request and names the demand's sublot. Anything else neither loads nor holds the stop.
    /// </summary>
    /// <remarks>
    /// Protocol 2.0.0 took <c>demandId</c> off <c>SublotSubmitted</c>: the server resolves the demand
    /// from the sublot, inside the demands the journey was dispatched for. That resolution is
    /// <c>8005-agv-control-server#82</c>; this predicate is the part of it the cancellation already
    /// needs, and it stays a pure one so both readers can use it without a database.
    /// </remarks>
    internal static bool IsEntryForStop(
        JsonElement submission,
        StopEntryAddress address,
        string demandSublot) =>
        AnswersTheStop(submission, address) &&
        submission.GetProperty("payload").GetProperty("sublot").GetString() == demandSublot;

    /// <summary>
    /// The submissions that have already been answered with a <c>SublotRejected</c>, keyed by the id of
    /// the submission each answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A refused entry is not an entry for the stop</b> (control-server#82, decided there and recorded
    /// here because this is where the stop's decisions share their facts). The operator is standing at
    /// the vehicle being shown why their scan was refused; cancelling the stop before any sublot is
    /// exactly what they are likely to want next, and holding the stop against a refusal they have just
    /// been told about leaves them only the station deadline. Without this the two refusals would also
    /// disagree with each other: a sublot outside the dispatch scope never satisfies
    /// <see cref="IsEntryForStop"/> and so never held the stop, while a sublot inside it that failed
    /// BR-013 did.
    /// </para>
    /// <para>
    /// <b>Read from the store, never from memory.</b> The refusal's <c>correlationId</c> is the only
    /// place the answer is recorded — no column holds it — and it is durable before it is visible here,
    /// so a refusal that has not been stored has not been decided: the cancellation stays refused until
    /// it has, which is the conservative side of the race the rest of this class is about.
    /// </para>
    /// <para>
    /// <b>Asked about the candidates, not about the refusals.</b> Reading every <c>SublotRejected</c>
    /// ever written and parsing each one is the same unbounded read that
    /// <see cref="JourneyRuntimeEngine.FindMatchingSublotAsync"/> exists to avoid on the inbox, and this
    /// runs on every poll of every stop's entry wait. The callers therefore hand over the submissions
    /// they are asking about — already narrowed to the stop's own in the store — and each is looked up by
    /// its own id inside the stored payload. The parse is still what decides; the substring only keeps
    /// rows that cannot be the answer out of the read, exactly as the inbox narrowing does.
    /// </para>
    /// <para>
    /// <b>One exemption, kept as it was.</b> An entry the runtime refuses for the station's task types
    /// is not a <c>SublotRejected</c> — it names a station that cannot do the work at all, and blocks the
    /// journey — and its own carve-out lives in the coordinator. This set does not reach it.
    /// </para>
    /// </remarks>
    public static async Task<HashSet<string>> RefusedSubmissionIdsAsync(
        ControlServerDbContext dbContext,
        IReadOnlyCollection<string> submissionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(submissionIds);
        HashSet<string> refused = new(StringComparer.Ordinal);
        foreach (string submissionId in submissionIds)
        {
            // A refusal of a later generation, or of another vehicle, is a different message and does not
            // answer this submission, so the stored id is compared rather than the substring alone.
            string[] stored = await dbContext.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "SublotRejected" && row.PayloadJson.Contains(submissionId))
                .Select(row => row.PayloadJson)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (string payload in stored)
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("correlationId", out JsonElement correlationId) &&
                    string.Equals(correlationId.GetString(), submissionId, StringComparison.Ordinal))
                {
                    refused.Add(submissionId);
                    break;
                }
            }
        }

        return refused;
    }
}
