using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace ControlServer.Host.Runtime.TaskTypeStations;

/// <summary>A request to hold one task type on one Map at once (REQ-0340, tightening half).</summary>
/// <param name="ClaimedRole">What the requester says they are. Recorded as said; it authenticates nothing.</param>
public sealed record TaskTypeHoldRequest(int MapId, string TaskType, string Reason, string? ClaimedRole);

/// <summary>What a hold request produced.</summary>
/// <param name="Created"><c>false</c> when a manual hold on this task type already stood and is the one returned.</param>
public sealed record TaskTypeHoldResponse(
    string HoldId,
    int MapId,
    string TaskType,
    string Source,
    string ReasonCode,
    DateTimeOffset RaisedAt,
    bool Created);

/// <summary>
/// The dashboard's tightening entry on the server (control-server#162): hold one <c>Map + TASK_TYPE</c> now, on a
/// person's word, with a reason.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only ever tightens.</b> There is no release here and there will be none: releasing is batch 6-05's FieldOps verb,
/// with a site check and a strict revalidation (specification 5.7). The route answers POST and nothing else.
/// </para>
/// <para>
/// <b>No credential and no switch, always mapped</b>: a fail-safe action gets no threshold (REQ-0340, "without waiting
/// for a second authentication or approval"). What narrows access is the source address alone -- a request whose remote
/// address is not loopback is refused, decided here rather than by the listening address, because the production
/// deployment binds the HTTP surface to the plant-facing interface.
/// </para>
/// <para>
/// <b>Immediate, and no further than that</b>: the hold is read by the next admission round and by the pre-create check
/// of any leg not yet created (batch 6-04). An order already created in RIoT is not changed, re-targeted or cancelled.
/// </para>
/// <para>
/// Every request that reaches the handler writes an administrator audit record (REQ-0348), refusals included. The
/// person field is the deployment identity marked as not attributable to a natural person; the role is recorded as
/// claimed.
/// </para>
/// </remarks>
public static class TaskTypeHoldEndpoints
{
    public const string Route = "/api/task-type-holds";

    /// <summary>The reason code every hold raised through this entry carries; the person's words go in the detail.</summary>
    public const string ManualHoldReasonCode = "DASHBOARD_MANUAL_HOLD";

    /// <summary>The administrator audit action written for every request.</summary>
    public const string HoldRequestedAction = "TASK_TYPE_STATION_HOLD_REQUESTED";

    /// <summary>The longest reason taken, in UTF-16 code units after trimming.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>The longest claimed role taken, in UTF-16 code units after trimming.</summary>
    public const int MaxClaimedRoleLength = 64;

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static void MapTaskTypeHolds(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(Route, HandleAsync)
            .WithName("HoldTaskTypeOnMap")
            .WithSummary("Hold one Map + TASK_TYPE at once on a person's word (REQ-0340, tightening only)")
            .Produces<TaskTypeHoldResponse>(StatusCodes.Status201Created)
            .Produces<TaskTypeHoldResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    public static async Task<IResult> HandleAsync(
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
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(timeProvider);

        context.Response.Headers.CacheControl = "no-store";
        DateTimeOffset now = timeProvider.GetUtcNow();
        int mapId = request?.MapId ?? 0;
        string? taskType = request?.TaskType;
        string? reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim();
        string? claimedRole = string.IsNullOrWhiteSpace(request?.ClaimedRole) ? null : request.ClaimedRole.Trim();

        if (!IsLoopback(context.Connection.RemoteIpAddress))
        {
            await WriteAuditAsync(
                audit, mapId, version: null, GovernanceActionOutcome.Failed, claimedRole, now,
                new
                {
                    mapId,
                    taskType,
                    reason,
                    claimedRole,
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString(),
                    result = "FORBIDDEN_NOT_LOOPBACK"
                },
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Hold requests are taken from this machine only",
                detail: "The task type hold entry answers loopback requests only; use the dashboard on the control host.");
        }

        // A Map this server serves is one with an activation pointer, whatever its state. After a manual close
        // (#161's CLOSED_MANUALLY tombstone) the pointer names no active version, and the hold is still taken: an
        // activation does not lift a manual hold, so it is how a person keeps the task type stopped through the next one.
        TaskTypeStationActivePointer? pointer = request is null
            ? null
            : await bindings.ReadActivePointerAsync(mapId, cancellationToken).ConfigureAwait(false);
        TaskTypeStationBindingSetVersion? active = pointer?.ActiveVersion is null
            ? null
            : await bindings.ReadActiveAsync(mapId, cancellationToken).ConfigureAwait(false);
        TaskTypeStationRuleVersion? ruleVersion = await rules.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        List<string> problems = [];
        if (pointer is null)
        {
            problems.Add($"Map {mapId} has no binding set activation; it is not a Map this server serves.");
        }
        if (string.IsNullOrWhiteSpace(taskType)
            || ruleVersion?.Rules.Any(rule => string.Equals(rule.TaskType, taskType, StringComparison.Ordinal)) != true)
        {
            problems.Add($"'{taskType}' is not a task type in the current rule table.");
        }
        if (reason is null)
        {
            problems.Add("A reason for the hold is required.");
        }
        if (problems.Count > 0)
        {
            await WriteAuditAsync(
                audit, mapId, active?.Version, GovernanceActionOutcome.Failed, claimedRole, now,
                new { mapId, taskType, reason, claimedRole, problems, result = "REJECTED" },
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Hold request refused",
                detail: string.Join(' ', problems),
                extensions: new Dictionary<string, object?> { ["problems"] = problems });
        }

        TaskTypeStationBinding? binding = active?.Bindings
            .SingleOrDefault(candidate => string.Equals(candidate.TaskType, taskType, StringComparison.Ordinal));
        int inFlight = await TaskTypeInFlightDemands.CountAsync(dbContext, mapId, taskType!, cancellationToken)
            .ConfigureAwait(false);

        await using IDbContextTransaction? transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        TaskTypeStationHoldRaise raised = await holds.RaiseAsync(
            mapId,
            taskType!,
            TaskTypeStationHoldSource.Manual,
            ManualHoldReasonCode,
            JsonSerializer.Serialize(
                new
                {
                    reason,
                    claimedRole,
                    stationRiotId = binding?.StationRiotId,
                    stationName = binding?.StationName,
                    bindingSetVersion = active?.Version,
                    activationState = pointer!.State,
                    inFlightDemands = inFlight
                },
                DetailOptions),
            deployment.Value,
            now,
            cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            audit, mapId, active?.Version, GovernanceActionOutcome.Succeeded, claimedRole, now,
            new
            {
                mapId,
                taskType,
                stationRiotId = binding?.StationRiotId,
                stationName = binding?.StationName,
                bindingSetVersion = active?.Version,
                activationState = pointer.State,
                reason,
                claimedRole,
                inFlightDemands = inFlight,
                holdId = raised.Hold.HoldId,
                result = raised.Created ? "CREATED" : "ALREADY_HELD"
            },
            cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        TaskTypeHoldResponse body = new(
            raised.Hold.HoldId,
            raised.Hold.MapId,
            raised.Hold.TaskType,
            raised.Hold.Source,
            raised.Hold.ReasonCode,
            raised.Hold.RaisedAt,
            raised.Created);
        // No Location: there is no hold resource to act on over HTTP, on purpose.
        return raised.Created ? TypedResults.Created((string?)null, body) : TypedResults.Ok(body);
    }

    /// <summary>Loopback, including an IPv4 loopback address carried as IPv6. No address at all is not loopback.</summary>
    internal static bool IsLoopback(IPAddress? remote) =>
        remote is not null
        && IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote);

    private static Task<string> WriteAuditAsync(
        IGovernanceAuditWriter audit,
        int mapId,
        long? version,
        GovernanceActionOutcome outcome,
        string? claimedRole,
        DateTimeOffset now,
        object detail,
        CancellationToken cancellationToken) =>
        audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                HoldRequestedAction,
                GovernedObjectKind.PublicStationBinding,
                TaskTypeStationGovernance.BindingSetObjectId(mapId),
                version,
                outcome,
                JsonSerializer.Serialize(detail, DetailOptions),
                ClaimedAdministratorRole: claimedRole),
            now,
            cancellationToken);
}
