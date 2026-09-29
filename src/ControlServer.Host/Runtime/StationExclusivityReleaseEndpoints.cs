using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// The server-side entry for releasing a public station's or a waiting point's exclusivity by hand (control-server#419): the
/// holder went offline, was towed away or retired, and the departure sweep -- which needs the vehicle online and reported at
/// another station -- will never let go.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="StationExclusivityManualRelease"/></b>, the same code FieldOps runs against the
/// database while the server is stopped. This authenticates the call and maps the decision onto HTTP; what it adds is the
/// RIoT cross-check, since only a running server reads RIoT.
/// </para>
/// <para>
/// <b>Same credential, same switch as <see cref="VehicleFaultRecoveryEndpoints"/></b>: it is the same kind of act -- a named
/// person on site deciding about a stuck vehicle -- and it loosens, so it takes the bearer credential and a named operator
/// rather than the tightening entries' no-threshold rule. It is mapped only when <c>VehicleFaultRecovery:enabled</c>.
/// </para>
/// <para>
/// 200 when released; 409 with every code when refused, the race with the sweep included; 401 without the credential; 503
/// when the credential variable is empty. Every request that gets past authentication writes an administrator audit record.
/// </para>
/// </remarks>
public static class StationExclusivityReleaseEndpoints
{
    public const string Route = "/api/field-ops/v1/station-exclusivity-releases";

    public static void MapStationExclusivityRelease(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("ReleaseStationExclusivityByHand")
            .WithSummary("Release a public station's or waiting point's exclusivity on a person's site check (control-server#419)")
            .WithDescription(
                "Checks the operator, the reason and the site verification that the vehicle is not at the station, that the named "
                + "vehicle holds a FIXED_TASK_STATION or WAITING_POINT row there, that its journey no longer has the station as an "
                + "unfinished stop (unless blocked) and that RIoT does not report the vehicle online at that station, then deletes "
                + "exactly the holding it read -- the departure sweep's delete condition, so only one of the two takes effect.")
            .Produces<StationExclusivityReleaseResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<StationExclusivityReleaseResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        StationExclusivityReleaseHttpRequest request,
        ControlServerDbContext dbContext,
        IGovernanceAuditWriter audit,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(recoveryOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        context.Response.Headers.CacheControl = "no-store";

        string? expected = Environment.GetEnvironmentVariable(recoveryOptions.Value.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Station exclusivity release unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        // A missing operator, reason or site verification is an unmet criterion, audited with the others; only a request that
        // names no station at all has nothing to judge.
        if (request is null || request.MapId <= 0 || request.StationId <= 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete station exclusivity release request",
                detail: "mapId and stationId are both required and positive.");
        }

        // Resolved here rather than bound: a server without RIoT configured still releases, without the cross-check.
        IRiotVehicleFacts? vehicleFacts = context.RequestServices.GetService<IRiotVehicleFacts>();
        StationExclusivityManualReleaseResult result = await StationExclusivityManualRelease.ReleaseAsync(
            dbContext,
            audit,
            vehicleFacts,
            new StationExclusivityManualReleaseRequest(
                request.MapId,
                request.StationId,
                request.VehicleKey,
                request.OperatorId,
                request.Reason,
                request.SiteVerification,
                request.ClaimedRole),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        StationExclusivityReleaseResponse body = new(
            request.MapId,
            request.StationId,
            result.Released ? "RELEASED" : "REJECTED",
            result.Codes,
            result.Holder?.StationKind,
            result.Holder?.VehicleKey,
            result.Holder?.JourneyId,
            result.RiotCrossCheck,
            result.AuditRecordId);
        if (result.Released)
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Station exclusivity release refused",
            detail: string.Join(',', result.Codes),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mapId"] = body.MapId,
                ["stationId"] = body.StationId,
                ["outcome"] = body.Outcome,
                ["codes"] = body.Codes,
                ["stationKind"] = body.StationKind,
                ["holderVehicleKey"] = body.HolderVehicleKey,
                ["holderJourneyId"] = body.HolderJourneyId,
                ["riotCrossCheck"] = body.RiotCrossCheck,
                ["auditRecordId"] = body.AuditRecordId,
            });
    }
}

/// <summary>What a person submits. The holder is named by its RIoT <c>VehicleKey</c>, as the exclusivity row stores it: a
/// retired vehicle need not be in the roster any more.</summary>
public sealed record StationExclusivityReleaseHttpRequest(
    int MapId,
    int StationId,
    string? VehicleKey,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole);

/// <summary>What the request came to, with the audit record to look it up by.</summary>
public sealed record StationExclusivityReleaseResponse(
    int MapId,
    int StationId,
    string Outcome,
    IReadOnlyList<string> Codes,
    string? StationKind,
    string? HolderVehicleKey,
    string? HolderJourneyId,
    string RiotCrossCheck,
    string AuditRecordId);
