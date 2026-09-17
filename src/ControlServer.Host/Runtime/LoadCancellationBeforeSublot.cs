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
    /// Whether an inbound <c>SublotSubmitted</c> is an entry for this stop: the same vehicle, the stop's
    /// operation session, station and worklist revision, and the demand's sublot. Anything else the runtime
    /// records as a mismatch and does not load.
    /// </summary>
    /// <remarks>
    /// Deliberately blind to the session generation. The runtime acts only on an entry of the current
    /// generation and checks that itself; the cancellation refuses on an entry of any generation, because an
    /// entry made before a reconnect may be the one the runtime is loading while the cancellation arrives on
    /// the next connection (control-server#116 review).
    /// </remarks>
    public static bool IsEntryForStop(
        JsonElement submission,
        JourneyRuntimeRow runtime,
        string demandSublot)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        JsonElement payload = submission.GetProperty("payload");
        // Protocol 2.0.0 took demandId off SublotSubmitted: the server resolves the demand. With one
        // demand per journey the operation session already names it; resolving by dispatch scope and
        // refusing sublots outside it is 8005-agv-control-server#82.
        return submission.GetProperty("agvId").GetString() == runtime.AgvId &&
               payload.GetProperty("operationSessionId").GetString() == runtime.OperationSessionId &&
               payload.GetProperty("stationId").GetString() == runtime.PickupStationId &&
               payload.GetProperty("worklistRevision").GetInt64() == runtime.WorklistRevision &&
               payload.GetProperty("sublot").GetString() == demandSublot;
    }
}
