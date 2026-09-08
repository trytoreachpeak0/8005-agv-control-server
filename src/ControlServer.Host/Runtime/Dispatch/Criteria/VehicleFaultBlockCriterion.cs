using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// A vehicle holding a fault fact takes no new work, at either level.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is REQ-0234's "立即禁止新派车", and it is passive on purpose.</b> The block is the fault
/// fact existing, not an action taken when it is recorded — so a vehicle is blocked from the
/// instant the fact is written, including across a restart, and there is no window in which the
/// fact is stored but something still has to remember to act on it.
/// </para>
/// <para>
/// <b>Both levels block, and they report different reasons.</b> REQ-0232 blocks new dispatch on
/// the suspected level already; the second level does not block harder, it means something
/// different to whoever reads the backlog. Collapsing them into one reason code would lose the
/// distinction the whole two-level model exists to draw.
/// </para>
/// <para>
/// <b>Order 15 — second, right after the demand's own eligibility.</b> A faulted vehicle cannot
/// take a candidate whatever else is true of it, so nothing more expensive should run first; the
/// only thing ahead of it is the in-memory check that this demand was already accepted.
/// </para>
/// <para>
/// <b>An identity this server cannot resolve blocks.</b> The fault fact is keyed on 8005's
/// <c>agvId</c> and dispatch runs on RIoT's <c>vehicleKey</c>; the two are different strings and
/// <c>remote-ops/fleet.md</c> is the register that maps them. Today the configuration carries
/// exactly one pair, so an unrecognised vehicle key means the round is deciding for a vehicle whose
/// fault state this criterion cannot read — and dispatching a vehicle whose fault state is unknown
/// is precisely what this exists to prevent. Ticket 09 replaces the lookup when there are several
/// vehicles; until then the fail-closed branch is unreachable in a correctly configured server and
/// will announce itself immediately if that stops being true.
/// </para>
/// </remarks>
public sealed class VehicleFaultBlockCriterion(
    IVehicleFaultStore faults,
    IOptions<JourneyRuntimeOptions> options) : IDispatchAdmissionCriterion
{
    /// <summary>The vehicle is suspected to be blocked; nothing has proven it faulty.</summary>
    public const string SuspectedReason = "VEHICLE_FAULT_SUSPECTED_BLOCK";

    /// <summary>Hard evidence, or a person, confirmed the isolation.</summary>
    public const string IsolatedReason = "VEHICLE_FAULT_ISOLATED";

    /// <summary>The round's vehicle key does not map onto an <c>agvId</c> this server knows.</summary>
    public const string IdentityUnresolvedReason = "VEHICLE_FAULT_IDENTITY_UNRESOLVED";

    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    public int Order => 15;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!string.Equals(
                evaluation.Vehicle.VehicleKey,
                runtimeOptions.VehicleKey,
                StringComparison.Ordinal))
        {
            return IdentityUnresolvedReason;
        }

        VehicleFaultFact? fault = await faults
            .ReadAsync(runtimeOptions.AgvId, cancellationToken).ConfigureAwait(false);
        return fault?.Level switch
        {
            VehicleFaultLevel.ConfirmedIsolated => IsolatedReason,
            VehicleFaultLevel.SuspectedBlocked => SuspectedReason,
            _ => DispatchAdmissionChain.Eligible
        };
    }
}
