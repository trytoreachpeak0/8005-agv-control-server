using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>Stub (control-server#210): compiles, decides nothing yet.</summary>
public sealed class TransportDemandKeySuppressedCriterion(ITransportDemandSuppressionStore suppressions)
    : IDispatchAdmissionCriterion
{
    public const string Reason = DispatchReasonCodes.TransportDemandKeySuppressed;

    public int Order => 11;

    public Task<string> EvaluateAsync(DispatchCandidateEvaluation evaluation, CancellationToken cancellationToken)
    {
        _ = suppressions;
        return Task.FromResult(DispatchAdmissionChain.Eligible);
    }
}
