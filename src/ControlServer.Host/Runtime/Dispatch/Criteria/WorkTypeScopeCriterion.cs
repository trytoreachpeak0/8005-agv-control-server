using ControlServer.Host.Runtime.TaskTypeStations;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Admission by task type (control-server#160, scope specification 5.3): the deployment must allow the task type,
/// the round's rules and bindings must give it a fixed station, and this build must be able to execute it.
/// </summary>
/// <remarks>
/// <para>
/// In this order, each with its own reason: <c>OUT_OF_SCOPE_WORK_TYPE</c> for a task type the deployment's
/// <c>AllowedWorkTypes</c> leaves out or the rule table does not know; then whatever the round's fixed station view
/// refuses the task type with -- a missing binding, a bound station missing from the catalog, a hold; and only
/// then <see cref="DispatchReasonCodes.TaskTypeNotYetExecutable"/>. A missing binding is named ahead of "not yet
/// executable", so an unbound STAGING_TO_WIRE demand is reported as unbound today rather than lumped out of scope.
/// </para>
/// <para>
/// <b>Order 20, ahead of every vehicle criterion.</b> All of these are facts about the demand and the configuration,
/// the same for every vehicle, so they are decided before any vehicle fact is read. Each refuses this task type
/// only: the view answers per task type, so a candidate of another task type in the same round is judged as if
/// nothing had happened (REQ-0335, REQ-0342). None of them is a fault; they reach the backlog and the dashboard,
/// never <c>VehicleBusinessStateSnapshot.blockingFacts</c> (scope specification 5.3).
/// </para>
/// </remarks>
public sealed class WorkTypeScopeCriterion(IOptions<JourneyRuntimeOptions> options)
    : IDispatchAdmissionCriterion
{
    private readonly JourneyRuntimeOptions _options = options.Value;

    public int Order => 20;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        string taskType = evaluation.Candidate.WorkType;
        if (!_options.AllowedWorkTypes.Contains(taskType, StringComparer.Ordinal))
        {
            return Task.FromResult(DispatchReasonCodes.OutOfScopeWorkType);
        }

        FixedTaskStationResolution fixedStation = evaluation.Round.FixedStations.Resolve(taskType);
        evaluation.FixedStation = fixedStation;
        if (fixedStation.RefusalReasonCode is { } refusal)
        {
            return Task.FromResult(refusal);
        }

        return Task.FromResult(ExecutableTaskTypes.Contains(taskType)
            ? DispatchAdmissionChain.Eligible
            : DispatchReasonCodes.TaskTypeNotYetExecutable);
    }
}
