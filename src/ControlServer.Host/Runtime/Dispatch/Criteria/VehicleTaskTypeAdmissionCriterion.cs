using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// This vehicle must be admitted for this task type by the fleet's dispatch policy.
/// </summary>
/// <remarks>
/// <para>
/// The <c>agvId × taskType</c> half of B2's vehicle filtering. Distinct from
/// <see cref="StationTaskTypeAdmissionCriterion"/>, which asks whether the work may happen at the
/// resolved station at all: this one asks whether <em>this vehicle</em> may do that work. Both are
/// fail-closed and both must pass.
/// </para>
/// <para>
/// <b>Fail-closed by construction, with two distinct reasons.</b> A vehicle the policy does not
/// mention and a vehicle whose allowed set omits this task type are both refusals, but they call
/// for different things from an operator — one is a vehicle nobody configured, the other a
/// deliberate restriction doing its job — so they do not share a reason code.
/// </para>
/// <para>
/// <b>Order 25 — early, and free.</b> It is a set lookup against the policy the round already
/// read, so running it before the MES field, area and station work costs nothing and saves all of
/// it for a vehicle that could never have taken the candidate.
/// </para>
/// </remarks>
public sealed class VehicleTaskTypeAdmissionCriterion : IDispatchAdmissionCriterion
{
    /// <summary>The dispatch policy has no profile for this vehicle at all.</summary>
    public const string VehicleNotInPolicyReason = "VEHICLE_NOT_IN_DISPATCH_POLICY";

    /// <summary>The vehicle is configured, and this task type is not among the ones it may take.</summary>
    public const string TaskTypeNotAdmittedReason = "VEHICLE_TASK_TYPE_NOT_ADMITTED";

    public int Order => 25;

    public Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        _ = cancellationToken;

        return Task.FromResult(Evaluate(evaluation.Round.Policy, evaluation.Vehicle.AgvId, evaluation.Candidate.WorkType));
    }

    /// <summary>The same verdict, callable without an evaluation.</summary>
    public static string Evaluate(VehicleDispatchPolicy policy, string agvId, string taskType)
    {
        ArgumentNullException.ThrowIfNull(policy);

        VehicleDispatchProfile? profile = policy.Vehicles
            .FirstOrDefault(vehicle => string.Equals(vehicle.AgvId, agvId, StringComparison.Ordinal));
        if (profile is null)
        {
            return VehicleNotInPolicyReason;
        }

        return profile.AllowedTaskTypes.Contains(taskType)
            ? DispatchAdmissionChain.Eligible
            : TaskTypeNotAdmittedReason;
    }
}
