using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>
/// The person's way out of an automatic rebuild that stopped (control-server#345), on the same entry point, with the same
/// operator identity and the same event 9203 as a fault clearance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to exist.</b> REQ-0361 says a demand that has a problem again within the window is not rebuilt
/// automatically but held, alarmed and "handled by a person" -- and until this there was nothing for the person to handle it
/// with. The journey stayed under <c>OWN_ORDER_REBUILD_STOPPED</c>, its demand not redispatched and its vehicle taking no new
/// work, until an engineer edited the database.
/// </para>
/// <para>
/// <b>Only a stopped journey.</b> The action is judged on the record the journey's current stop waits on
/// (<see cref="OwnOrderRebuilds.ForStopAsync"/>): it has to be <see cref="OwnOrderRebuildStates.Stopped"/>, and stopped by the
/// third guard. A journey whose rebuild is merely waiting is refused -- the engine will make it -- and so is one that is not
/// waiting on a rebuild at all.
/// </para>
/// <para>
/// <b>Nothing is released and nothing is sent.</b> The request stages a new rebuild, due at once, and gives the journey back
/// its transitional code; the engine's next round takes it from there through every check #318 put before a create
/// (<see cref="OwnOrderRebuilds.StageManualRebuildAsync"/>). No RIoT call is made at all, so the request reads and writes this
/// server's tables only, under <see cref="JourneyMutationGate"/> like a clearance.
/// </para>
/// </remarks>
public sealed partial class VehicleFaultRecoveryService
{
    /// <summary>The journey is not waiting on a rebuild the third guard stopped; there is nothing to rebuild on request.</summary>
    public const string ExitNotStoppedReason = "OWN_ORDER_REBUILD_EXIT_NOT_STOPPED";

    private async Task<VehicleFaultRecoveryDecision> RebuildStoppedAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string agvId = request.Subject.AgvId;
        List<string> reasons = [.. PersonReasons(request)];
        using IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            return Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], null);
        }

        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes
            .SingleOrDefaultAsync(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed, cancellationToken)
            .ConfigureAwait(false);
        JourneyStopRow? stop = null;
        OwnOrderRebuildRow? stopped = null;
        if (runtime is not null && WaitsOnAnOrder(runtime))
        {
            stop = (await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false)).Current;
            stopped = await OwnOrderRebuilds.ForStopAsync(dbContext, stop, cancellationToken).ConfigureAwait(false);
        }

        if (stopped is not { State: OwnOrderRebuildStates.Stopped })
        {
            reasons.Add(ExitNotStoppedReason);
        }

        if (reasons.Count > 0)
        {
            return Refused(reasons, null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        OwnOrderRebuildRow manual = await OwnOrderRebuilds.StageManualRebuildAsync(
                dbContext, runtime!, stop!, stopped!, request.OperatorId!, now, cancellationToken)
            .ConfigureAwait(false);
        runtime!.SetBlockReason(TransitionalCode(manual.Source), now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.RebuildRequested, [], VehicleFaultRecoveryDispositions.RebuildScheduled, null);
    }

    /// <summary>
    /// The code the journey waits under until the engine's next round names what the rebuild waits on, or confirms it: the
    /// one its source waits under before an automatic rebuild is due.
    /// </summary>
    private static string TransitionalCode(string source) => source switch
    {
        OwnOrderRebuildSources.FaultClearedCargoOnBoard => CargoOnBoardReason,
        OwnOrderRebuildSources.FaultClearedNothingOnBoard => NothingOnBoardReason,
        _ => JourneyRuntimeEngine.OrderEndedWithoutArrivalReason,
    };
}
