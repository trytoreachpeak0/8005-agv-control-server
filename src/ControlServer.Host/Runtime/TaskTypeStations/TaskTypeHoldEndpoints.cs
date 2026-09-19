using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

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
/// for a second authentication or approval"). What narrows access is the source address alone: a request is taken only
/// from this machine -- loopback, or the address the connection arrived on. That is decided here rather than by the
/// listening address, because the production deployment binds the HTTP surface to the plant-facing interface and points
/// the loopback-only dashboard at that same address, so the dashboard's forwarded request arrives from it. A refused
/// request is audited by its addresses alone; the reason and the claimed role are length-limited.
/// </para>
/// <para>
/// <b>The source is judged before the body is read</b> (control-server#201). The handler takes no bound request
/// parameter: the framework binds those before the handler runs -- endpoint filters too see arguments already bound --
/// so a stranger's body, malformed or oversized, was parsed before the 403, and a malformed one never reached the 403
/// at all. The body is read here, after the source check, and never past <see cref="MaxRequestBodyBytes"/>.
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

    /// <summary>
    /// The largest request body read, in bytes: the longest reason and claimed role written as escaped JSON (six bytes a
    /// UTF-16 code unit at most) with room to spare for the rest of the request.
    /// </summary>
    public const int MaxRequestBodyBytes = 16 * 1024;

    /// <summary>The audit object of a request refused before its body was read: which Map it named is not known.</summary>
    public const string UnknownMapObjectId = "map-unknown";

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static void MapTaskTypeHolds(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(Route, HandleAsync)
            .Accepts<TaskTypeHoldRequest>("application/json")
            .WithName("HoldTaskTypeOnMap")
            .WithSummary("Hold one Map + TASK_TYPE at once on a person's word (REQ-0340, tightening only)")
            .Produces<TaskTypeHoldResponse>(StatusCodes.Status201Created)
            .Produces<TaskTypeHoldResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    public static async Task<IResult> HandleAsync(
        HttpContext context,
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

        if (!IsFromThisMachine(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress))
        {
            // Nothing the caller sent is read, let alone kept: a server bound to the plant interface is reachable from the
            // whole plant network, and neither its parser nor the audit table is for a refused stranger's text. Which Map
            // the request named is therefore not known either.
            await WriteAuditAsync(
                audit, UnknownMapObjectId, version: null, GovernanceActionOutcome.Failed, claimedRole: null, now,
                new
                {
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString(),
                    localAddress = context.Connection.LocalIpAddress?.ToString(),
                    result = "FORBIDDEN_NOT_LOCAL"
                },
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Hold requests are taken from this machine only",
                detail: "The task type hold entry answers requests from this machine only; use the dashboard on the control host.");
        }

        RequestBody read = await ReadRequestAsync(context, cancellationToken).ConfigureAwait(false);
        if (read.Refusal is { } refusal)
        {
            await WriteAuditAsync(
                audit, UnknownMapObjectId, version: null, GovernanceActionOutcome.Failed, claimedRole: null, now,
                new { codes = new[] { refusal.Code }, result = "REJECTED" },
                cancellationToken).ConfigureAwait(false);
            return refusal.StatusCode == StatusCodes.Status422UnprocessableEntity
                ? TypedResults.Problem(
                    statusCode: refusal.StatusCode,
                    title: "Hold request refused",
                    detail: refusal.Message,
                    extensions: new Dictionary<string, object?>
                    {
                        ["codes"] = new[] { refusal.Code },
                        ["problems"] = new[] { refusal.Message }
                    })
                : TypedResults.Problem(statusCode: refusal.StatusCode, title: refusal.Message);
        }

        TaskTypeHoldRequest? request = read.Request;
        int mapId = request?.MapId ?? 0;
        string? taskType = request?.TaskType;
        string? reason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim();
        string? claimedRole = string.IsNullOrWhiteSpace(request?.ClaimedRole) ? null : request.ClaimedRole.Trim();

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
        bool knownTaskType = !string.IsNullOrWhiteSpace(taskType)
            && ruleVersion?.Rules.Any(rule => string.Equals(rule.TaskType, taskType, StringComparison.Ordinal)) == true;
        List<(string Code, string Message)> problems = [];
        if (pointer is null)
        {
            problems.Add(("MAP_NOT_SERVED", $"Map {mapId} has no binding set activation; it is not a Map this server serves."));
        }
        if (!knownTaskType)
        {
            problems.Add(("TASK_TYPE_UNKNOWN", "The task type is not in the current rule table."));
        }
        if (reason is null)
        {
            problems.Add(("REASON_REQUIRED", "A reason for the hold is required."));
        }
        else if (reason.Length > MaxReasonLength)
        {
            problems.Add(("REASON_TOO_LONG", $"The reason is {reason.Length} characters; at most {MaxReasonLength} are taken."));
        }
        if (claimedRole?.Length > MaxClaimedRoleLength)
        {
            problems.Add((
                "CLAIMED_ROLE_TOO_LONG",
                $"The claimed role is {claimedRole.Length} characters; at most {MaxClaimedRoleLength} are taken."));
        }
        if (problems.Count > 0)
        {
            // Only what fits the limits is kept; anything longer is recorded by its length.
            bool reasonFits = reason is null || reason.Length <= MaxReasonLength;
            bool roleFits = claimedRole is null || claimedRole.Length <= MaxClaimedRoleLength;
            string[] codes = problems.Select(problem => problem.Code).ToArray();
            string[] messages = problems.Select(problem => problem.Message).ToArray();
            await WriteAuditAsync(
                audit, TaskTypeStationGovernance.BindingSetObjectId(mapId), active?.Version, GovernanceActionOutcome.Failed,
                roleFits ? claimedRole : null, now,
                new
                {
                    mapId,
                    taskType = knownTaskType ? taskType : null,
                    reason = reasonFits ? reason : null,
                    reasonLength = reason?.Length,
                    claimedRole = roleFits ? claimedRole : null,
                    claimedRoleLength = claimedRole?.Length,
                    codes,
                    result = "REJECTED"
                },
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Hold request refused",
                detail: string.Join(' ', messages),
                extensions: new Dictionary<string, object?> { ["codes"] = codes, ["problems"] = messages });
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
            audit, TaskTypeStationGovernance.BindingSetObjectId(mapId), active?.Version, GovernanceActionOutcome.Succeeded,
            claimedRole, now,
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

    /// <summary>
    /// A request from this machine: loopback, or the very address the connection arrived on -- which is what the
    /// dashboard's forwarded request looks like when the server is bound to the plant interface and the dashboard's
    /// <c>controlServerBaseUrl</c> names that address. IPv4 carried as IPv6 is compared as IPv4. No address at all is not
    /// this machine.
    /// </summary>
    internal static bool IsFromThisMachine(IPAddress? remote, IPAddress? local)
    {
        if (remote is null)
        {
            return false;
        }
        IPAddress caller = Normalize(remote);
        return IPAddress.IsLoopback(caller) || (local is not null && caller.Equals(Normalize(local)));
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>What the body said, or why it was not taken. An empty body is no request, as binding made it before.</summary>
    private sealed record RequestBody(TaskTypeHoldRequest? Request, BodyRefusal? Refusal);

    private sealed record BodyRefusal(int StatusCode, string Code, string Message);

    /// <summary>
    /// Reads the body of a request from this machine, never past <see cref="MaxRequestBodyBytes"/>, with the same JSON
    /// options the framework's binding used. The answers binding gave stay as they were -- 415 for a body that is not
    /// JSON, 400 for JSON that does not parse -- and an oversized body is refused in the 422 shape of the field limits.
    /// </summary>
    private static async Task<RequestBody> ReadRequestAsync(HttpContext context, CancellationToken cancellationToken)
    {
        BodyRefusal tooLarge = new(
            StatusCodes.Status422UnprocessableEntity,
            "REQUEST_BODY_TOO_LARGE",
            $"The request body is larger than {MaxRequestBodyBytes} bytes; the reason and the claimed role fit well within it.");
        if (context.Request.ContentLength > MaxRequestBodyBytes)
        {
            return new(null, tooLarge);
        }

        byte[] buffer = new byte[MaxRequestBodyBytes + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            length += read;
        }
        if (length > MaxRequestBodyBytes)
        {
            return new(null, tooLarge);
        }
        if (length == 0)
        {
            return new(null, null);
        }
        if (!context.Request.HasJsonContentType())
        {
            return new(null, new(
                StatusCodes.Status415UnsupportedMediaType,
                "REQUEST_BODY_NOT_JSON",
                "The request body is not JSON."));
        }

        JsonSerializerOptions options = context.RequestServices?.GetService<IOptions<HttpJsonOptions>>()?.Value.SerializerOptions
            ?? WebOptions;
        try
        {
            return new(JsonSerializer.Deserialize<TaskTypeHoldRequest>(buffer.AsSpan(0, length), options), null);
        }
        catch (JsonException)
        {
            return new(null, new(
                StatusCodes.Status400BadRequest,
                "REQUEST_BODY_MALFORMED",
                "The request body is not a hold request."));
        }
    }

    private static Task<string> WriteAuditAsync(
        IGovernanceAuditWriter audit,
        string objectId,
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
                objectId,
                version,
                outcome,
                JsonSerializer.Serialize(detail, DetailOptions),
                ClaimedAdministratorRole: claimedRole),
            now,
            cancellationToken);
}
