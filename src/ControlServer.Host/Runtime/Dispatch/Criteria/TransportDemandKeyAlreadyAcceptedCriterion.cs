using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>Stub (control-server#210): compiles, decides nothing yet.</summary>
public sealed class TransportDemandKeyAlreadyAcceptedCriterion(ControlServerDbContext dbContext)
    : IDispatchAdmissionCriterion
{
    public const string Reason = DispatchReasonCodes.TransportDemandKeyAlreadyAccepted;

    public int Order => 12;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        _ = dbContext;
        return Task.FromResult(DispatchAdmissionChain.Eligible);
    }
}
