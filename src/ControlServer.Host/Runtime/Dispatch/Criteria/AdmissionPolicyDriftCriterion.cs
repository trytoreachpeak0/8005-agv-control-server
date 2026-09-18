namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// No further demand is taken on while the admission policy the configuration names is not the one
/// bound in the store.
/// </summary>
/// <remarks>
/// <para>
/// The runtime re-derives the admission policy from the live Map every round and binds it to
/// <c>JourneyRuntime:admissionPolicyVersion</c>. Map 25 is shared, so another RIoT user adding,
/// renaming or removing an area-named station changes that set under the same version, and the
/// store refuses to rebind it. The engine keeps the bound policy in force for every journey already
/// under way -- ADR-cross-0050 and 0051 confine a policy change to what is not yet committed -- and
/// reports the refusal on <see cref="DispatchRoundFacts.AdmissionPolicyDrifted"/>. This criterion is
/// where that stops new work. Raising the version is the deliberate re-import intake waits for.
/// </para>
/// <para>
/// <b>Order 75, before the vehicle's dynamic facts (80) and the station admission (90).</b> The
/// station admission answers from the bound policy, which is exactly what is no longer known to be
/// the configured one. And a drift lasts until someone changes the Map or the version, so it
/// outranks the transient vehicle facts a reader of the backlog would otherwise see in its place.
/// Demand-specific reasons ahead of it -- scope, station resolution, package capacity -- still name
/// themselves.
/// </para>
/// </remarks>
public sealed class AdmissionPolicyDriftCriterion : IDispatchAdmissionCriterion
{
    /// <summary>The live Map's area-named stations differ from the set the configured version is bound to.</summary>
    public const string Reason = "ADMISSION_POLICY_DRIFT";

    public int Order => 75;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        return Task.FromResult(evaluation.Round.AdmissionPolicyDrifted
            ? Reason
            : DispatchAdmissionChain.Eligible);
    }
}
