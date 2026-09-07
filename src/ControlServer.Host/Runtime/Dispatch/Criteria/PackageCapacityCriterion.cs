using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Resolves and freezes the candidate's PACKAGE capacity — how many boxes fit in one basket.
/// </summary>
/// <remarks>
/// The store both resolves and tracks: an unresolvable PACKAGE is recorded so the gap is visible
/// rather than only showing up as a demand that never dispatches. A null or non-positive capacity
/// blocks — the basket count below divides by it, and a wrong divisor is a vehicle sent out with
/// the wrong number of baskets.
/// </remarks>
public sealed class PackageCapacityCriterion(IPackageCapacityStore packageCapacityStore)
    : IDispatchAdmissionCriterion
{
    public int Order => 70;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        int? capacity = await packageCapacityStore.ResolveAndTrackAsync(
            evaluation.Candidate.LiveMesFields!.Package!,
            evaluation.Round.Now,
            cancellationToken).ConfigureAwait(false);
        evaluation.PackageCapacity = capacity;

        return capacity is null or <= 0
            ? "PACKAGE_CAPACITY_NOT_UNIQUE"
            : DispatchAdmissionChain.Eligible;
    }
}
