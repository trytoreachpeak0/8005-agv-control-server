using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace ControlServer.Host.Runtime;

/// <summary>维修暂停一个充电桩的请求（<c>REQ-0288</c>，收紧）。</summary>
/// <param name="ClaimedRole">请求者自述的角色，照录，不认证任何东西。</param>
public sealed record ChargingStationHoldHttpRequest(int MapId, int StationId, string? Reason, string? ClaimedRole);

/// <summary>恢复一个充电桩分配资格的请求（<c>ChargingStationRecoveryConfirmation</c>，放宽）。</summary>
/// <param name="Basis">恢复的依据：维修单号、检查记录。</param>
public sealed record ChargingStationRecoveryHttpRequest(
    int MapId, int StationId, string? OperatorId, string? Basis, string? ClaimedRole);

/// <summary>人工清桩确认的请求（Host 入口，放宽）。<c>clearedCondition</c> 取协议的三个值之一，缺省 <c>STATION_EMPTY</c>。</summary>
public sealed record ChargingStationClearanceHttpRequest(
    string? AgvId, string? StationId, string? OperatorId, string? ClearedCondition);

/// <summary>一次暂停或恢复的结果。</summary>
public sealed record ChargingStationHoldResponse(
    int MapId,
    int StationId,
    string Outcome,
    IReadOnlyList<string> HoldIds,
    IReadOnlyList<string> Codes);

/// <summary>一次 Host 人工清桩确认的结果，与线上 <c>ManualStationClearanceConfirmationResult</c> 同义。</summary>
public sealed record ChargingStationClearanceResponse(
    string AgvId,
    string ConfirmationRequestId,
    string Outcome,
    string? ReasonCode,
    string? DisplayMessage,
    bool StationReleased);

/// <summary>
/// 充电桩的三个 Host 入口（批次9-08，control-server#406；票面第 8 条）：维修暂停、恢复确认、人工清桩。看板不加表单。
/// </summary>
/// <remarks>
/// <para>
/// <b>维修暂停是收紧类</b>，照 <see cref="TaskTypeHoldEndpoints"/> 的形状：不要凭据、不挂开关、总是映射（失效保护动作不设门槛），只收本机来源，
/// 来源在读请求体之前判；角色按自述记下；每一次请求（拒绝也算）写管理员审计。它写一条触发来源为 <see cref="ChargingStationHoldTriggers.Maintenance"/>
/// 的暂停（<c>REQ-0288</c>：暂停分配而不删名册身份）：桩退出新分配的候选，<b>已在用这个桩的周期不受打断</b>——分配只在开新周期时看暂停，
/// 正在去桩、在充、充满待离桩的车照常走完，离桩后才不再分给任何车（出发前复核会撤回一个还没出发过的承诺：那不是打断一个在进行中的周期）。
/// 同一个桩已有一条未恢复的维修暂停时答 200 与那一条，不写第二条。
/// </para>
/// <para>
/// <b>恢复确认与人工清桩是放宽类</b>，照 <see cref="VehicleFaultRecoveryEndpoints"/> 的形状：同一把 Bearer 凭据、同一个开关
/// （<c>VehicleFaultRecovery:enabled</c>，关着时这两个都不映射）、具名人员，每一次请求写管理员审计。恢复把这个桩此刻每一条未恢复的暂停
/// （维修、充不上……）各记一条恢复，名册不改；人工清桩与车载端的线上确认走同一个判定（<see cref="ManualStationClearance"/>）——车载端入口
/// （hmi#221）没合入、或车连不上时就走这里。
/// </para>
/// </remarks>
public static class ChargingStationEndpoints
{
    public const string HoldRoute = "/api/charging/v1/station-holds";
    public const string RecoveryRoute = "/api/charging/v1/station-recoveries";
    public const string ClearanceRoute = "/api/charging/v1/station-clearances";

    public const string HoldAuditAction = "CHARGING_STATION_MAINTENANCE_HOLD_REQUESTED";
    public const string RecoveryAuditAction = "CHARGING_STATION_RECOVERY_CONFIRMATION";

    public const string ChargerNotInRoster = "CHARGER_NOT_IN_ROSTER";
    public const string ReasonRequired = "REASON_REQUIRED";
    public const string OperatorRequired = "OPERATOR_REQUIRED";
    public const string BasisRequired = "BASIS_REQUIRED";
    public const string FieldTooLong = "FIELD_TOO_LONG";
    public const string NoActiveHold = "NO_ACTIVE_HOLD";

    /// <summary>理由与依据的最长长度（去掉首尾空白后的 UTF-16 码元）。</summary>
    public const int MaxTextLength = 500;

    /// <summary>人员与自述角色的最长长度。</summary>
    public const int MaxIdentifierLength = 64;

    private const int MaxRequestBodyBytes = 16 * 1024;

    private static readonly string[] ClearedConditions = ["STATION_EMPTY", "OBSTRUCTION_REMOVED", "CARGO_RELOCATED"];

    private static readonly JsonSerializerOptions DetailOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, int, int, string, Exception?> LogMaintenanceHold =
        LoggerMessage.Define<int, int, string>(
            LogLevel.Warning,
            new EventId(2268, nameof(LogMaintenanceHold)),
            "Charger {MapId}/{StationId} is put on maintenance allocation hold ({Reason}): no new charging cycle is given it; a " +
            "cycle already using it goes on to its end. It returns only with a ChargingStationRecoveryConfirmation.");

    private static readonly Action<ILogger, int, int, string, int, Exception?> LogRecovered =
        LoggerMessage.Define<int, int, string, int>(
            LogLevel.Warning,
            new EventId(2269, nameof(LogRecovered)),
            "Charger {MapId}/{StationId} is back in allocation: {OperatorId} confirmed its recovery, closing {Count} hold(s).");

    /// <summary>
    /// 映射三个入口：维修暂停总是映射；恢复确认与人工清桩只在 <see cref="VehicleFaultRecoveryEndpoints.EnabledKey"/> 打开时映射。答后两者有没有映射。
    /// </summary>
    public static bool MapChargingStationEntries(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost(HoldRoute, HoldAsync)
            .WithName("HoldChargingStationForMaintenance")
            .WithSummary("Pause one charger's 8005 allocation for maintenance at once (REQ-0288, tightening only)")
            .Produces<ChargingStationHoldResponse>(StatusCodes.Status201Created)
            .Produces<ChargingStationHoldResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        if (!app.Configuration.GetValue<bool>(VehicleFaultRecoveryEndpoints.EnabledKey))
        {
            return false;
        }
        app.MapPost(RecoveryRoute, RecoverAsync)
            .WithName("ConfirmChargingStationRecovery")
            .WithSummary("ChargingStationRecoveryConfirmation: return a paused charger to allocation (REQ-0288)")
            .Produces<ChargingStationHoldResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        app.MapPost(ClearanceRoute, ClearAsync)
            .WithName("ConfirmChargingStationClearance")
            .WithSummary("Confirm a charger clear by hand (REQ-0179), the decision the onboard entry also uses")
            .Produces<ChargingStationClearanceResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return true;
    }

    // ---- 维修暂停（收紧）--------------------------------------------------------------------------------------------------

    public static async Task<IResult> HoldAsync(
        HttpContext context,
        IChargerRoster roster,
        IChargingHoldStore holds,
        IGovernanceAuditWriter audit,
        GovernanceDeploymentIdentity deployment,
        TimeProvider timeProvider,
        ILogger<ChargingStationHoldHttpRequest> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(timeProvider);

        context.Response.Headers.CacheControl = "no-store";
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!TaskTypeHoldEndpoints.IsFromThisMachine(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress))
        {
            // Nothing the caller sent is read: which charger it named is not known either.
            await WriteAuditAsync(
                audit, HoldAuditAction, "charger-unknown", GovernanceActionOutcome.Failed, null, now,
                new
                {
                    remoteAddress = context.Connection.RemoteIpAddress?.ToString(),
                    localAddress = context.Connection.LocalIpAddress?.ToString(),
                    result = "FORBIDDEN_NOT_LOCAL"
                },
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Maintenance hold requests are taken from this machine only");
        }

        ChargingStationHoldHttpRequest? request = await ReadBodyAsync<ChargingStationHoldHttpRequest>(context, cancellationToken)
            .ConfigureAwait(false);
        string? reason = Trimmed(request?.Reason);
        string? claimedRole = Trimmed(request?.ClaimedRole);
        int mapId = request?.MapId ?? 0;
        int stationId = request?.StationId ?? 0;
        List<string> codes = [];
        if (reason is null) codes.Add(ReasonRequired);
        bool fits = (reason?.Length ?? 0) <= MaxTextLength && (claimedRole?.Length ?? 0) <= MaxIdentifierLength;
        if (!fits) codes.Add(FieldTooLong);
        ChargerRosterVersion? current = await roster.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        ChargerRosterEntry? charger = (current?.Chargers ?? [])
            .FirstOrDefault(entry => entry.MapId == mapId && entry.StationId == stationId);
        if (charger is null) codes.Add(ChargerNotInRoster);
        string objectId = StationExclusivityManualRelease.ObjectId(mapId, stationId);
        if (codes.Count > 0)
        {
            await WriteAuditAsync(
                audit, HoldAuditAction, objectId, GovernanceActionOutcome.Failed, fits ? claimedRole : null, now,
                new { mapId, stationId, reason = fits ? reason : null, codes, result = "REJECTED" },
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Maintenance hold refused",
                detail: string.Join(',', codes),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["codes"] = codes });
        }

        ChargingStationAllocationHold? standing = (await holds.ListActiveStationHoldsAsync(mapId, stationId, cancellationToken)
                .ConfigureAwait(false))
            .FirstOrDefault(hold => hold.Trigger == ChargingStationHoldTriggers.Maintenance);
        bool created = standing is null;
        if (standing is null)
        {
            string holdId = Guid.NewGuid().ToString("D");
            HoldWriteResult<ChargingStationAllocationHold> written = await holds.RecordStationHoldAsync(
                new ChargingStationAllocationHold(
                    holdId, JourneyPlanBuilder.StableGuid(holdId, "maintenance-hold"), ChargingStationHoldTriggers.Maintenance,
                    mapId, stationId, current!.Version, null, null, null, null, null, null, null, null, null, now, now,
                    null, null, null, null, $"host:{HoldRoute}", null, null,
                    deployment.Value, claimedRole, null, reason),
                cancellationToken).ConfigureAwait(false);
            standing = written.Hold;
            LogMaintenanceHold(logger, mapId, stationId, reason!, null);
        }

        await WriteAuditAsync(
            audit, HoldAuditAction, objectId, GovernanceActionOutcome.Succeeded, claimedRole, now,
            new { mapId, stationId, reason, claimedRole, holdId = standing.HoldId, result = created ? "CREATED" : "ALREADY_HELD" },
            cancellationToken).ConfigureAwait(false);
        ChargingStationHoldResponse body = new(mapId, stationId, created ? "CREATED" : "ALREADY_HELD", [standing.HoldId], []);
        return created ? TypedResults.Created((string?)null, body) : TypedResults.Ok(body);
    }

    // ---- 恢复确认（放宽）--------------------------------------------------------------------------------------------------

    public static async Task<Results<Ok<ChargingStationHoldResponse>, UnauthorizedHttpResult, ProblemHttpResult>> RecoverAsync(
        HttpContext context,
        ChargingStationRecoveryHttpRequest request,
        IChargingHoldStore holds,
        IGovernanceAuditWriter audit,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        TimeProvider timeProvider,
        ILogger<ChargingStationRecoveryHttpRequest> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(holds);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(recoveryOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        context.Response.Headers.CacheControl = "no-store";
        if (Authenticate(context, recoveryOptions.Value) is { } refused)
        {
            return refused;
        }
        if (request is null || request.MapId <= 0 || request.StationId <= 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete charger recovery request",
                detail: "mapId and stationId are both required and positive.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string? operatorId = Trimmed(request.OperatorId);
        string? basis = Trimmed(request.Basis);
        string? claimedRole = Trimmed(request.ClaimedRole);
        List<string> codes = [];
        if (operatorId is null) codes.Add(OperatorRequired);
        if (basis is null) codes.Add(BasisRequired);
        bool fits = (basis?.Length ?? 0) <= MaxTextLength && (operatorId?.Length ?? 0) <= MaxIdentifierLength &&
                    (claimedRole?.Length ?? 0) <= MaxIdentifierLength;
        if (!fits) codes.Add(FieldTooLong);
        IReadOnlyList<ChargingStationAllocationHold> active =
            await holds.ListActiveStationHoldsAsync(request.MapId, request.StationId, cancellationToken).ConfigureAwait(false);
        if (active.Count == 0) codes.Add(NoActiveHold);
        string objectId = StationExclusivityManualRelease.ObjectId(request.MapId, request.StationId);
        if (codes.Count > 0)
        {
            await WriteAuditAsync(
                audit, RecoveryAuditAction, objectId, GovernanceActionOutcome.Failed, fits ? claimedRole : null, now,
                new { request.MapId, request.StationId, operatorId = fits ? operatorId : null, basis = fits ? basis : null, codes, result = "REJECTED" },
                cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Charger recovery refused",
                detail: string.Join(',', codes),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["codes"] = codes });
        }

        List<string> recovered = [];
        foreach (ChargingStationAllocationHold hold in active)
        {
            ChargingHoldRecovery recovery = await holds.RecoverStationHoldAsync(
                new ChargingHoldRecovery(
                    Guid.NewGuid().ToString("D"), hold.HoldId, operatorId!, claimedRole ?? "UNSTATED", now, basis!),
                cancellationToken).ConfigureAwait(false);
            recovered.Add(recovery.HoldId);
        }
        await WriteAuditAsync(
            audit, RecoveryAuditAction, objectId, GovernanceActionOutcome.Succeeded, claimedRole, now,
            new { request.MapId, request.StationId, operatorId, basis, claimedRole, recovered, result = "RECOVERED" },
            cancellationToken).ConfigureAwait(false);
        LogRecovered(logger, request.MapId, request.StationId, operatorId!, recovered.Count, null);
        return TypedResults.Ok(new ChargingStationHoldResponse(request.MapId, request.StationId, "RECOVERED", recovered, []));
    }

    // ---- 人工清桩（放宽）--------------------------------------------------------------------------------------------------

    public static async Task<Results<Ok<ChargingStationClearanceResponse>, UnauthorizedHttpResult, ProblemHttpResult>> ClearAsync(
        HttpContext context,
        ChargingStationClearanceHttpRequest request,
        ManualStationClearance clearance,
        VehicleRoster fleet,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clearance);
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(recoveryOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        context.Response.Headers.CacheControl = "no-store";
        if (Authenticate<ChargingStationClearanceResponse>(context, recoveryOptions.Value) is { } refused)
        {
            return refused;
        }
        string condition = Trimmed(request?.ClearedCondition) ?? "STATION_EMPTY";
        if (request is null || Trimmed(request.AgvId) is null || Trimmed(request.StationId) is null ||
            !ClearedConditions.Contains(condition, StringComparer.Ordinal))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete charger clearance request",
                detail: $"agvId and stationId are required, and clearedCondition must be one of {string.Join(", ", ClearedConditions)}.");
        }
        if (fleet.ByAgvId(request.AgvId!.Trim()) is not FleetVehicle vehicle)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown vehicle",
                detail: $"'{request.AgvId}' is not a vehicle this server drives.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        string confirmationRequestId = Guid.NewGuid().ToString("D");
        string payload = JsonSerializer.Serialize(request, WebOptions);
        ManualStationClearanceConfirmation decision = await clearance.DecideAsync(
            new ManualStationClearanceRequest(
                ManualStationClearanceSources.Host,
                vehicle.AgvId,
                vehicle.VehicleKey,
                confirmationRequestId,
                0,
                "host:" + confirmationRequestId,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
                request.StationId!.Trim(),
                null,
                condition,
                Trimmed(request.OperatorId),
                // Who is behind the call is the named operator with the shared credential: a server session, not a badge.
                "SESSION",
                now,
                now),
            cancellationToken).ConfigureAwait(false);
        ChargingStationClearanceResponse body = new(
            vehicle.AgvId, confirmationRequestId, decision.Decision.Outcome, decision.Decision.ProblemReasonCode,
            decision.Decision.ProblemDisplayMessage, decision.StationReleased);
        if (decision.Decision.Outcome == FieldConfirmationDecision.Confirmed)
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Charger clearance refused",
            detail: decision.Decision.ProblemReasonCode,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agvId"] = body.AgvId,
                ["confirmationRequestId"] = body.ConfirmationRequestId,
                ["reasonCode"] = body.ReasonCode,
                ["displayMessage"] = body.DisplayMessage,
                ["stationReleased"] = body.StationReleased,
            });
    }

    // ---- 共用 -------------------------------------------------------------------------------------------------------------

    private static ProblemHttpResult? AuthenticateProblem(VehicleFaultRecoveryOptions options) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable))
            ? TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Charger entry unavailable")
            : null;

    /// <summary>与 <see cref="VehicleFaultRecoveryEndpoints"/> 同一把凭据：没配答 503，不对答 401，对了答空。</summary>
    private static Results<Ok<T>, UnauthorizedHttpResult, ProblemHttpResult>? Authenticate<T>(
        HttpContext context, VehicleFaultRecoveryOptions options)
    {
        if (AuthenticateProblem(options) is { } unavailable)
        {
            return unavailable;
        }
        string expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable)!;
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }
        return null;
    }

    private static Results<Ok<ChargingStationHoldResponse>, UnauthorizedHttpResult, ProblemHttpResult>? Authenticate(
        HttpContext context, VehicleFaultRecoveryOptions options) =>
        Authenticate<ChargingStationHoldResponse>(context, options);

    private static async Task<T?> ReadBodyAsync<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        if (context.Request.ContentLength > MaxRequestBodyBytes || !context.Request.HasJsonContentType())
        {
            return null;
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
        if (length == 0 || length > MaxRequestBodyBytes)
        {
            return null;
        }
        JsonSerializerOptions options = context.RequestServices?.GetService<IOptions<HttpJsonOptions>>()?.Value.SerializerOptions
            ?? WebOptions;
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, length), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static Task<string> WriteAuditAsync(
        IGovernanceAuditWriter audit,
        string action,
        string objectId,
        GovernanceActionOutcome outcome,
        string? claimedRole,
        DateTimeOffset now,
        object detail,
        CancellationToken cancellationToken) =>
        audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                action,
                GovernedObjectKind.StationExclusivity,
                objectId,
                null,
                outcome,
                JsonSerializer.Serialize(detail, DetailOptions),
                ClaimedAdministratorRole: claimedRole),
            now,
            cancellationToken);
}
