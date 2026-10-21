using ControlServer.Domain;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// One AREA must map to exactly one EQP across the whole catalog, and it must be this candidate's.
/// </summary>
/// <remarks>
/// <para>
/// The check looks at every open demand in the round's catalog, not just this candidate: an AREA
/// serving two different EQPs means the pickup station this AREA resolves to is ambiguous, and
/// picking either one would send a vehicle to a machine that is not the one the demand came from.
/// Fail-closed on ambiguity rather than on absence.
/// </para>
/// <para>
/// <b>Every task type's rows, not this candidate's alone</b> (REQ-0187, control-server#160): an AREA is one place
/// on the floor whatever MES asks to be carried from it, so two EQPs under one AREA in any task type make the AREA
/// ambiguous for all of them.
/// </para>
/// <para>
/// <b>Except the rows the catalog age window refuses</b> (REQ-0366, control-server#827 review M2, the coordinator's ruling):
/// an unaccepted demand MesIngest first read too long ago, or with no first-read time, is still in the catalog but is never
/// dispatched, and counting its EQP would hold every fresh demand of the same AREA under <c>AREA_EQP_NOT_UNIQUE</c> until MES
/// closes the old one. REQ-0187 asks for a monitor over the equipment master data; this criterion stands in for it with the
/// catalog's rows, and only that stand-in is narrowed. The cost: an old row's EQP may be the real evidence that one AREA
/// serves two machines, and it is no longer seen here. Accepted demands are not subject to the window, so their rows stay in.
/// </para>
/// </remarks>
public sealed class AreaEqpUniqueCriterion : IDispatchAdmissionCriterion
{
    public int Order => 50;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        string area = evaluation.Candidate.LiveMesFields!.Area!;
        DispatchRoundFacts round = evaluation.Round;
        string[] areaEqps = round.Catalog.Items
            .Where(item =>
                string.Equals(item.LiveMesFields?.Area, area, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.LiveMesFields?.Eqp) &&
                (round.AcceptedDemandIds.Contains(item.DemandId) || CatalogAgeWindow.Refusal(round, item, round.Now) is null))
            .Select(item => item.LiveMesFields!.Eqp!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        bool unique = areaEqps.Length == 1 &&
            string.Equals(areaEqps[0], evaluation.Candidate.LiveMesFields.Eqp, StringComparison.Ordinal);

        return Task.FromResult(unique ? DispatchAdmissionChain.Eligible : "AREA_EQP_NOT_UNIQUE");
    }
}
