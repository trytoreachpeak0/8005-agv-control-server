using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>A request to hold one task type on one Map at once (REQ-0340, tightening half).</summary>
/// <param name="ClaimedRole">What the requester says they are. Recorded as said; it authenticates nothing.</param>
public sealed record TaskTypeHoldRequest(int MapId, string TaskType, string Reason, string? ClaimedRole);

/// <summary>The dashboard's tightening entry on the server (control-server#162).</summary>
public static class TaskTypeHoldEndpoints
{
    public const string Route = "/api/task-type-holds";

    /// <summary>The reason code every hold raised through this entry carries.</summary>
    public const string ManualHoldReasonCode = "DASHBOARD_MANUAL_HOLD";

    /// <summary>The administrator audit action written for every request.</summary>
    public const string HoldRequestedAction = "TASK_TYPE_STATION_HOLD_REQUESTED";

    public static void MapTaskTypeHolds(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(Route, HandleAsync);
    }

    public static Task<IResult> HandleAsync(
        HttpContext context,
        TaskTypeHoldRequest? request,
        ControlServerDbContext dbContext,
        ITaskTypeStationRuleStore rules,
        ITaskTypeStationBindingStore bindings,
        ITaskTypeStationHoldStore holds,
        IGovernanceAuditWriter audit,
        GovernanceDeploymentIdentity deployment,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        _ = (context, request, dbContext, rules, bindings, holds, audit, deployment, timeProvider, cancellationToken);
        return Task.FromResult<IResult>(TypedResults.StatusCode(StatusCodes.Status501NotImplemented));
    }
}
