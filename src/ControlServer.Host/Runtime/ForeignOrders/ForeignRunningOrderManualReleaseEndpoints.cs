using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.ForeignOrders;

/// <summary>A request to release a vehicle held by a foreign order that left RIoT's running listing unread as ended (control-server#617).</summary>
public sealed record ForeignRunningOrderManualReleaseHttpRequest(
    string? AgvId,
    string? RiotOrderId,
    string? OperatorId,
    string? Reason,
    string? SiteVerification);

/// <summary>The decision, released or refused; <c>Descriptions</c> carries the Chinese text of every refusal code.</summary>
public sealed record ForeignRunningOrderManualReleaseResponse(
    string AgvId,
    string RiotOrderId,
    string Outcome,
    IReadOnlyList<string> Codes,
    IReadOnlyDictionary<string, string> Descriptions,
    string AuditRecordId);

/// <summary>
/// The server-side entry out of <see cref="Infrastructure.Persistence.ForeignRiotOrderStates.Unsettled"/> (control-server#617): a named
/// R-11 or R-13 person releases the vehicle once RIoT, read again, no longer lists the order unfinished.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="ForeignRunningOrderManualRelease"/></b>; this authenticates the call, resolves the named
/// vehicle and maps the decision onto HTTP. Same credential as <see cref="VehicleFaultRecoveryEndpoints"/>: it is the same kind of
/// act, a named person deciding about a stuck vehicle, and it loosens. Its switch is its own,
/// <see cref="ForeignRunningOrderReleaseOptions.EnabledKey"/> (review M1): the production definitions keep fault recovery off,
/// and the exit has to be there all the same.
/// </para>
/// <para>
/// 200 when released; 409 with every code (and its Chinese description) when refused; 401 without the credential; 404 for a vehicle
/// this server does not drive; 422 when no vehicle or order is named at all; 503 when the credential variable is empty. Every request
/// that gets past those writes an administrator audit record.
/// </para>
/// </remarks>
public static class ForeignRunningOrderManualReleaseEndpoints
{
    public const string Route = "/api/field-ops/v1/foreign-running-order-releases";

    /// <summary>
    /// Maps the entry when <see cref="ForeignRunningOrderReleaseOptions.EnabledKey"/> is on, whatever fault recovery's switch says
    /// (review M1), and says whether it did.
    /// </summary>
    public static bool MapForeignRunningOrderManualReleaseWhenEnabled(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Configuration.GetValue<bool>(ForeignRunningOrderReleaseOptions.EnabledKey))
        {
            return false;
        }
        app.MapForeignRunningOrderManualRelease();
        return true;
    }

    public static void MapForeignRunningOrderManualRelease(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("ReleaseForeignRunningOrderByHand")
            .WithSummary("Release a vehicle held by a foreign order that left RIoT's running listing unread as ended (control-server#617)")
            .WithDescription(
                "Checks the R-11/R-13 operator, the reason and the site verification, that the vehicle's foreign order row is "
                + "UNSETTLED, that RIoT's unfinished-order listing reads complete without the order, and that the order's own read "
                + "is no unfinished state; then stops holding the vehicle for it.")
            .Produces<ForeignRunningOrderManualReleaseResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<ForeignRunningOrderManualReleaseResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        ForeignRunningOrderManualReleaseHttpRequest request,
        ForeignRunningOrderManualRelease release,
        VehicleRoster roster,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(recoveryOptions);

        context.Response.Headers.CacheControl = "no-store";
        string? expected = Environment.GetEnvironmentVariable(recoveryOptions.Value.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Foreign running order release unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        // A missing operator, reason or site verification is an unmet criterion, audited with the others; only a request that names
        // no vehicle or order has nothing to judge.
        if (request is null || string.IsNullOrWhiteSpace(request.AgvId) || string.IsNullOrWhiteSpace(request.RiotOrderId))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete foreign running order release request",
                detail: "agvId and riotOrderId are both required.");
        }
        if (roster.ByAgvId(request.AgvId.Trim()) is not FleetVehicle vehicle)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown vehicle",
                detail: $"'{request.AgvId}' is not a vehicle this server drives.");
        }

        ForeignRunningOrderManualReleaseResult result = await release.DecideAsync(
            new ForeignRunningOrderManualReleaseRequest(
                vehicle.AgvId,
                vehicle.VehicleKey,
                request.RiotOrderId,
                request.OperatorId,
                request.Reason,
                request.SiteVerification),
            cancellationToken).ConfigureAwait(false);

        ForeignRunningOrderManualReleaseResponse body = new(
            vehicle.AgvId,
            request.RiotOrderId.Trim(),
            result.Released ? "RELEASED" : "REJECTED",
            result.Codes,
            result.Codes.Distinct(StringComparer.Ordinal).ToDictionary(
                code => code,
                code => ForeignRunningOrderManualRelease.Descriptions.GetValueOrDefault(code, code),
                StringComparer.Ordinal),
            result.AuditRecordId);
        if (result.Released)
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Foreign running order release refused",
            detail: string.Join(',', result.Codes),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agvId"] = body.AgvId,
                ["riotOrderId"] = body.RiotOrderId,
                ["outcome"] = body.Outcome,
                ["codes"] = body.Codes,
                ["descriptions"] = body.Descriptions,
                ["auditRecordId"] = body.AuditRecordId,
            });
    }
}
