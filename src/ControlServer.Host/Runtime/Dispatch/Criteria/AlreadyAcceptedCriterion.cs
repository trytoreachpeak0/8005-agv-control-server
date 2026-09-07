namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// A demand this server has already accepted is never a candidate again.
/// </summary>
/// <remarks>
/// First in the chain, and deliberately so. MesIngest's catalog is MES's own list of open
/// transport demands, and a journey of ours reaching Completed does not take the demand out of it,
/// so a finished demand is scored as a fresh candidate on the very next round. It is also the
/// oldest thing in the backlog, so the ranker would pick it ahead of everything else, intake would
/// then meet its own AcceptedDemands row and refuse the replay — failing the whole round closed
/// and starving every other eligible demand for as long as the finished one stayed in the catalog.
/// Running this first also saves the route, package, box-count and vehicle reads below it, which
/// would be spent on a decision that is already made.
/// </remarks>
public sealed class AlreadyAcceptedCriterion : IDispatchAdmissionCriterion
{
    public int Order => 10;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        return Task.FromResult(
            evaluation.Round.AcceptedDemandIds.Contains(evaluation.Candidate.DemandId)
                ? "DEMAND_ALREADY_ACCEPTED"
                : DispatchAdmissionChain.Eligible);
    }
}
