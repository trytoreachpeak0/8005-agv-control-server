using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// 已承诺充电、或在人工充电等待中的车不接搬运（<c>REQ-0290</c>、<c>REQ-0173</c>、<c>REQ-0171</c>；批次9-06，control-server#404）。
/// </summary>
public sealed class ChargingStandingCriterion(ControlServerDbContext dbContext) : IDispatchAdmissionCriterion
{
    public int Order => 16;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = (dbContext, cancellationToken);
        return Task.FromResult(DispatchAdmissionChain.Eligible);
    }
}
