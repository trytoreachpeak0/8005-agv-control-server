using ControlServer.Host.Runtime.CreateGate;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// The Map/Station catalog must be approved and fresh before any of it is used.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order 55, ahead of station resolution, and that placement is the requirement rather than an
/// optimisation.</b> REQ-0303 forbids resolving execution stations for a new TransportDemand while
/// the catalog is unusable — not merely acting on the result. Putting this after
/// <see cref="StationResolutionCriterion"/> would resolve first and discard after, which is the
/// behaviour the entry rules out.
/// </para>
/// <para>
/// Absent the two approved REQ-0302 values this blocks every round, forever, by design:
/// specification 8.6 lists it as one of three hard blocks. The server still starts, still reports
/// its catalog state and still retries the sync — REQ-0303 requires that too — it simply creates
/// nothing. There is no switch that turns this off.
/// </para>
/// </remarks>
public sealed class CatalogAvailabilityCriterion(CatalogAvailabilityAccess catalog) : IDispatchAdmissionCriterion
{
    /// <summary>Before station resolution (60), after the cheap scope checks.</summary>
    public int Order => 55;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        CatalogAvailability availability = await catalog
            .ReadAsync(evaluation.Round.Map.MapId, cancellationToken).ConfigureAwait(false);
        if (!availability.IsUsable)
        {
            return availability.BlockReason!;
        }

        // Carried so the endpoints this demand freezes are stamped with the revision they were
        // actually taken from, rather than whatever the catalog says by the time they are written.
        evaluation.CatalogRevision = availability.CatalogRevision;
        return DispatchAdmissionChain.Eligible;
    }
}
