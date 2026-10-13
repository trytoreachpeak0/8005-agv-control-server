using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>
/// The person's way out of a demand leg whose create result is unknown and whose order RIoT does not show, when the engine's own
/// evidence never completes (control-server#673).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it has to exist.</b> The engine takes such an order as never created only on control-server#611's evidence: RIoT
/// answering exactly "no such order" for two minutes without a break, the vehicle proven idle, RIoT's unfinished-order listing read
/// in full. A vehicle that is offline, a listing that never reads complete, or reconciliation that keeps timing out leave the leg
/// under <c>PICKUP_ResultUnknown</c> or <c>GATE_ResultUnknown</c>, and nothing else accepted that state.
/// </para>
/// <para>
/// <b>It decides one thing only: that the order was never created.</b> The leg is recorded as ended with
/// <see cref="OwnOrderRebuildSources.OrderNeverAppeared"/>, already <see cref="OwnOrderRebuildStates.Stopped"/>
/// (<see cref="OwnOrderRebuilds.NeverAppearedDeclaredByOperator"/>), so that the person then takes one of control-server#345's three
/// ways out on the same entry: rebuild it once more (every check before a create applies, a cargo proof included), give a trip
/// with nothing on board up, or hand a loaded one to the exception recovery session. Nothing moves, nothing is released and no
/// demand ends on this request: what happens to the demand -- and so to MES -- is that next choice, already governed.
/// </para>
/// <para>
/// <b>What it requires, every unmet one named</b>: the person and their confirmation; a journey waiting in its arrival stage on a
/// stop with no rebuild record; that stop's intent sent at least once, still <c>RESULT_UNKNOWN</c>, never given an orderId
/// (<see cref="NeverAppearedOrderIdKnownReason"/>: it had an order in RIoT); its last create answer older than
/// <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>, so the engine's own exit had its chance; and RIoT answering, read now and
/// precisely by the upperId, "no such order" -- an order there, or an answer that says nothing, refuses it, so the entry never takes
/// a vehicle off an order that was created after all. RIoT is read before the gate, the journey judged again under it.
/// </para>
/// </remarks>
public sealed partial class VehicleFaultRecoveryService
{
    /// <summary>The vehicle's journey is not waiting on a demand leg whose create result is unknown, or that leg already has a rebuild record.</summary>
    public const string NeverAppearedNotUnknownReason = "ORDER_NEVER_APPEARED_EXIT_NOT_UNKNOWN";

    /// <summary>
    /// The leg's intent was given an orderId, or RIoT's create answer was accepted or carried one: RIoT had that order, whatever it
    /// answers now.
    /// </summary>
    public const string NeverAppearedOrderIdKnownReason = "ORDER_NEVER_APPEARED_EXIT_ORDER_ID_KNOWN";

    /// <summary>The last create answer is not yet older than <c>JourneyRuntime:OwnOrderRebuildRepeatWindow</c>.</summary>
    public const string NeverAppearedTooEarlyReason = "ORDER_NEVER_APPEARED_EXIT_TOO_EARLY";

    /// <summary>RIoT, read by the upperId, shows an order, or one that cannot be told apart from one.</summary>
    public const string NeverAppearedOrderNotAbsentReason = "ORDER_NEVER_APPEARED_EXIT_ORDER_NOT_ABSENT";

    /// <summary>RIoT did not answer the read by the upperId.</summary>
    public const string NeverAppearedOrderUnreadableReason = "ORDER_NEVER_APPEARED_EXIT_ORDER_UNREADABLE";

    private async Task<VehicleFaultRecoveryDecision> DeclareOrderNeverAppearedAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        string agvId = request.Subject.AgvId;
        List<string> reasons = [.. PersonReasons(request)];
        UnknownLeg read = await ReadUnknownLegAsync(agvId, cancellationToken).ConfigureAwait(false);
        if (read.AlreadyDeclared && reasons.Count == 0)
        {
            return AlreadyDone();
        }

        if (read.Refusal is not null)
        {
            reasons.Add(read.Refusal);
            return Refused(reasons, null);
        }

        // RIoT is read here, before the gate, as every RIoT read of this service is (control-server#299 review M2).
        string upperId = read.Intent!.UpperId;
        try
        {
            RiotOrderObservation order = await movement.ReconcileByUpperIdAsync(upperId, cancellationToken).ConfigureAwait(false);
            if (order.Kind == RiotOrderObservationKind.Unknown && !order.IsExactAbsentAtObservation(upperId))
            {
                reasons.Add(NeverAppearedOrderUnreadableReason);
            }
            else if (order.Kind != RiotOrderObservationKind.NotFound && !order.IsExactAbsentAtObservation(upperId))
            {
                reasons.Add(NeverAppearedOrderNotAbsentReason);
            }
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or TaskCanceledException &&
                                      !cancellationToken.IsCancellationRequested)
        {
            reasons.Add(NeverAppearedOrderUnreadableReason);
        }

        if (reasons.Count > 0)
        {
            return Refused(reasons, null);
        }

        using IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);
        if (round is null)
        {
            return Refused(["FAULT_RECOVERY_RUNTIME_BUSY"], null);
        }

        // From here on: this server's tables only. The leg is judged again, and has to be the very one RIoT was read for.
        UnknownLeg standing = await ReadUnknownLegAsync(agvId, cancellationToken).ConfigureAwait(false);
        if (standing.Refusal is not null || standing.Runtime!.JourneyId != read.Runtime!.JourneyId ||
            standing.Intent!.UpperId != upperId)
        {
            return Refused(["FAULT_RECOVERY_STATE_CHANGED"], null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        JourneyRuntimeRow runtime = standing.Runtime;
        if (standing.Superseded is { } ordering)
        {
            // The leg is a rebuild's own new order (review H1): that rebuild ends here, in the same save, as the engine ends it
            // when its own evidence completes -- left Ordering beside the record staged below, ForStopAsync would find two.
            ordering.State = OwnOrderRebuildStates.Ended;
            ordering.StoppedReason = OwnOrderRebuilds.NeverAppearedBeforeConfirmation;
            ordering.StoppedAt = now;
            ordering.WaitingReason = null;
            ordering.WaitingSince = null;
        }

        OwnOrderRebuildRow record = await OwnOrderRebuilds.StageAsync(
                dbContext, runtime, standing.Stop!, upperId, endedOrderId: null, endedOrderState: null,
                OwnOrderRebuildSources.OrderNeverAppeared, incidentAt: now, now, request.OperatorId, runtimeOptions.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (record.State != OwnOrderRebuildStates.Stopped)
        {
            record.State = OwnOrderRebuildStates.Stopped;
            record.StoppedReason = OwnOrderRebuilds.NeverAppearedDeclaredByOperator;
            record.StoppedAt = now;
        }

        runtime.SetBlockReason(JourneyRuntimeEngine.OwnOrderRebuildStoppedReason, now);
        runtime.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.NeverAppearedDeclared, [], VehicleFaultRecoveryDispositions.RebuildStopped, null);
    }

    /// <summary>
    /// The vehicle's journey, the stop it waits at and that stop's intent, read afresh and tracked; why the declaration does not
    /// apply to it, or null; and whether a person has already declared this very leg.
    /// </summary>
    private async Task<UnknownLeg> ReadUnknownLegAsync(string agvId, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        JourneyRuntimeRow? runtime = await dbContext.JourneyRuntimes
            .SingleOrDefaultAsync(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed, cancellationToken)
            .ConfigureAwait(false);
        if (runtime is null || runtime.CarriesNoDemand() || !WaitsOnAnOrder(runtime))
        {
            return new UnknownLeg(runtime, null, null, null, NeverAppearedNotUnknownReason, AlreadyDeclared: false);
        }

        JourneyStopRow stop = (await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false))
            .Current;
        OrderIntentRow? intent = await dbContext.OrderIntents.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UpperId == stop.UpperId, cancellationToken).ConfigureAwait(false);
        // The record of this very order's ending, if any: this leg is already governed by it. A person's declaration made it
        // when the record is stopped, names a person and came from this source -- whichever reason it stopped under, the window's
        // own included (review L1).
        OwnOrderRebuildRow? record = await dbContext.OwnOrderRebuilds.AsNoTracking()
            .SingleOrDefaultAsync(row => row.RebuildId == OwnOrderRebuilds.RebuildIdFor(stop.UpperId), cancellationToken)
            .ConfigureAwait(false);
        if (record is not null)
        {
            bool declared = record is
            {
                State: OwnOrderRebuildStates.Stopped, Source: OwnOrderRebuildSources.OrderNeverAppeared, OperatorId: not null,
            };
            return new UnknownLeg(runtime, stop, intent, null, NeverAppearedNotUnknownReason, declared);
        }

        // The rebuild the stop waits on otherwise (review H1). Only one is accepted: the rebuild whose own new order this is, still
        // Ordering -- it is superseded by the declaration. Any other (waiting for its delay, failed, stopped) governs the leg itself.
        OwnOrderRebuildRow? governing = await OwnOrderRebuilds.ForStopAsync(dbContext, stop, cancellationToken).ConfigureAwait(false);
        if (governing is not null &&
            !(governing.State == OwnOrderRebuildStates.Ordering && governing.NewUpperId == stop.UpperId))
        {
            return new UnknownLeg(runtime, stop, intent, null, NeverAppearedNotUnknownReason, AlreadyDeclared: false);
        }

        string? refusal = intent switch
        {
            not { Status: "RESULT_UNKNOWN", CreateAttemptCount: > 0 } => NeverAppearedNotUnknownReason,
            { OrderId: not null } => NeverAppearedOrderIdKnownReason,
            // Review S1: a create answer that carried an orderId, or was accepted, says RIoT had the order.
            _ when await AbandonedDemandLegOrders.RiotEverAnsweredWithAnOrderAsync(dbContext, intent, cancellationToken)
                .ConfigureAwait(false) => NeverAppearedOrderIdKnownReason,
            _ when timeProvider.GetUtcNow() - (intent.LastCreateOutcomeAt ?? intent.CreatedAt) <=
                   runtimeOptions.Value.OwnOrderRebuildRepeatWindow => NeverAppearedTooEarlyReason,
            _ => null,
        };
        return new UnknownLeg(runtime, stop, intent, governing, refusal, AlreadyDeclared: false);
    }

    /// <summary>What <see cref="ReadUnknownLegAsync"/> found; the journey is tracked in this context.</summary>
    private sealed record UnknownLeg(
        JourneyRuntimeRow? Runtime,
        JourneyStopRow? Stop,
        OrderIntentRow? Intent,
        OwnOrderRebuildRow? Superseded,
        string? Refusal,
        bool AlreadyDeclared);
}
