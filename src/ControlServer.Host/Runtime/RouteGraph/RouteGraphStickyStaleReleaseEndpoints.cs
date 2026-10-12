using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.RouteGraph;

/// <summary>
/// The server-side entry for releasing the route graph's sticky stale reasons by hand (control-server#659): the Map's edge groups
/// changed, or dynamic route cost appeared, and the graph stays out of dispatch until a person has checked the change on the RIoT
/// side and says the graph still answers reachability truthfully.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="RouteGraphStickyStaleRelease"/></b>; this authenticates the call and maps the decision onto
/// HTTP. Same credential and same switch as <see cref="VehicleFaultRecoveryEndpoints"/> and the station exclusivity release: a named
/// person deciding about a held state that loosens dispatch. Mapped only when <c>VehicleFaultRecovery:enabled</c>.
/// </para>
/// <para>
/// GET reads the snapshot header the release binds to -- the stale reason and since when, the edge-group fingerprint, whether
/// dynamic route cost is present, when the design state was last read -- and writes nothing. POST releases: 200 when released;
/// 409 with every code and its Chinese description when refused; 401 without the credential; 422 when no map is named; 503 when
/// the credential variable is empty. Every POST that gets past those writes an administrator audit record.
/// </para>
/// </remarks>
public static class RouteGraphStickyStaleReleaseEndpoints
{
    public const string Route = "/api/field-ops/v1/route-graph-sticky-stale-releases";

    public static void MapRouteGraphStickyStaleRelease(this WebApplication app)
    {
        app.MapGet(Route, ReadAsync)
            .WithName("ReadRouteGraphStickyStaleState")
            .WithSummary("Read the route graph snapshot header a sticky stale release binds to (control-server#659)")
            .Produces<RouteGraphStickyStaleStateResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        app.MapPost(Route, ReleaseAsync)
            .WithName("ReleaseRouteGraphStickyStaleByHand")
            .WithSummary("Release the route graph's sticky stale reason on a person's check of the RIoT map (control-server#659)")
            .WithDescription(
                "Checks the operator, the reason and the verification record, that the snapshot is stale for an edge-group "
                + "fingerprint change or for dynamic route cost appearing, that the stale reason, since-time, edge-group "
                + "fingerprint and dynamic route cost presence are still what the person acknowledged, and that the design state "
                + "was read again after the change; then clears the stale flag on exactly that state, so a refresh that changes "
                + "any of it at the same moment wins and nothing is cleared.")
            .Produces<RouteGraphStickyStaleReleaseResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<RouteGraphStickyStaleStateResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> ReadAsync(
        HttpContext context,
        int? mapId,
        ControlServerDbContext dbContext,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(recoveryOptions);

        context.Response.Headers.CacheControl = "no-store";
        switch (Authenticate(context, recoveryOptions.Value))
        {
            case Authentication.Unavailable:
                return Unavailable();
            case Authentication.Refused:
                return TypedResults.Unauthorized();
        }
        if (mapId is not > 0)
        {
            return MapRequired();
        }

        RouteGraphSnapshotHeader? snapshot = await RouteGraphStickyStaleRelease
            .ReadAsync(dbContext, mapId.Value, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "No route graph snapshot",
                detail: RouteGraphStickyStaleReleaseCodes.Descriptions[RouteGraphStickyStaleReleaseCodes.SnapshotNotFound]);
        }
        return TypedResults.Ok(new RouteGraphStickyStaleStateResponse(
            snapshot.MapId,
            snapshot.StaleReason,
            snapshot.StaleSince,
            RouteGraphStickyStaleReasons.IsSticky(snapshot.StaleReason),
            snapshot.EdgeGroupFingerprint,
            snapshot.EdgeGroupRefreshedAt,
            snapshot.DynamicRouteCostPresent,
            snapshot.DynamicRouteCostObservedAt,
            snapshot.DesignRevision,
            snapshot.DesignRefreshedAt));
    }

    public static async Task<Results<
        Ok<RouteGraphStickyStaleReleaseResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> ReleaseAsync(
        HttpContext context,
        RouteGraphStickyStaleReleaseHttpRequest request,
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
        switch (Authenticate(context, recoveryOptions.Value))
        {
            case Authentication.Unavailable:
                return Unavailable();
            case Authentication.Refused:
                return TypedResults.Unauthorized();
        }

        // A missing operator, reason, verification or acknowledgement is an unmet criterion, audited with the others; only a
        // request that names no map has nothing to judge.
        if (request is null || request.MapId <= 0)
        {
            return MapRequired();
        }

        RouteGraphStickyStaleReleaseResult result = await RouteGraphStickyStaleRelease.ReleaseAsync(
            dbContext,
            audit,
            new RouteGraphStickyStaleReleaseRequest(
                request.MapId,
                request.OperatorId,
                request.Reason,
                request.SiteVerification,
                request.ClaimedRole,
                request.ExpectedStaleReason,
                request.ExpectedStaleSince,
                request.AcknowledgedEdgeGroupFingerprint,
                request.AcknowledgedDynamicRouteCostPresent),
            timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> descriptions = result.Codes.ToDictionary(
            code => code, code => RouteGraphStickyStaleReleaseCodes.Descriptions[code], StringComparer.Ordinal);
        RouteGraphStickyStaleReleaseResponse body = new(
            request.MapId,
            result.Released ? "RELEASED" : "REJECTED",
            result.Codes,
            descriptions,
            result.Snapshot?.StaleReason,
            result.Snapshot?.StaleSince,
            result.Snapshot?.EdgeGroupFingerprint,
            result.Snapshot?.DynamicRouteCostPresent,
            result.AuditRecordId);
        if (result.Released)
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Route graph sticky stale release refused",
            detail: string.Join(',', result.Codes),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mapId"] = body.MapId,
                ["outcome"] = body.Outcome,
                ["codes"] = body.Codes,
                ["descriptions"] = body.Descriptions,
                ["staleReason"] = body.StaleReason,
                ["staleSince"] = body.StaleSince,
                ["edgeGroupFingerprint"] = body.EdgeGroupFingerprint,
                ["dynamicRouteCostPresent"] = body.DynamicRouteCostPresent,
                ["auditRecordId"] = body.AuditRecordId,
            });
    }

    private static ProblemHttpResult MapRequired() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Incomplete route graph sticky stale release request",
            detail: "mapId is required and positive.");

    private enum Authentication
    {
        Passed,
        Unavailable,
        Refused,
    }

    /// <summary>The fault recovery entry's bearer credential, read from the variable it names; never logged or echoed.</summary>
    private static Authentication Authenticate(HttpContext context, VehicleFaultRecoveryOptions options)
    {
        string? expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return Authentication.Unavailable;
        }

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Authentication.Refused;
        }
        return Authentication.Passed;
    }

    private static ProblemHttpResult Unavailable() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Route graph sticky stale release unavailable");
}

/// <summary>What a person submits: who, why, the verification record, and the snapshot state they read with GET and checked.</summary>
public sealed record RouteGraphStickyStaleReleaseHttpRequest(
    int MapId,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole,
    string? ExpectedStaleReason,
    DateTimeOffset? ExpectedStaleSince,
    string? AcknowledgedEdgeGroupFingerprint,
    bool? AcknowledgedDynamicRouteCostPresent);

/// <summary>
/// The snapshot header a release binds to. <c>Sticky</c> says whether the current reason is one only a person releases;
/// <c>DynamicRouteCostPresent</c> is shown so that releasing over dynamic cost still in play is a decision made with it in view.
/// </summary>
public sealed record RouteGraphStickyStaleStateResponse(
    int MapId,
    string? StaleReason,
    DateTimeOffset? StaleSince,
    bool Sticky,
    string EdgeGroupFingerprint,
    DateTimeOffset? EdgeGroupRefreshedAt,
    bool DynamicRouteCostPresent,
    DateTimeOffset? DynamicRouteCostObservedAt,
    long DesignRevision,
    DateTimeOffset? DesignRefreshedAt);

/// <summary>What the request came to, the state it was judged against, and the audit record to look it up by.</summary>
public sealed record RouteGraphStickyStaleReleaseResponse(
    int MapId,
    string Outcome,
    IReadOnlyList<string> Codes,
    IReadOnlyDictionary<string, string> Descriptions,
    string? StaleReason,
    DateTimeOffset? StaleSince,
    string? EdgeGroupFingerprint,
    bool? DynamicRouteCostPresent,
    string AuditRecordId);
