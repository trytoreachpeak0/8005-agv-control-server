using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>What a person submits to grant: rehearse without <c>confirmationCode</c>, grant with the rehearsal's.</summary>
public sealed record RecoveryGrantHttpRequest(
    string? VehicleKey,
    string? JourneyId,
    IReadOnlyList<string>? Actions,
    int? Minutes,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole,
    string? ConfirmationCode);

/// <summary>What a person submits to revoke.</summary>
public sealed record RecoveryGrantRevocationHttpRequest(
    string? VehicleKey,
    string? OperatorId,
    string? Reason,
    string? ClaimedRole);

/// <summary>What a grant, a rehearsal or a revocation came to.</summary>
public sealed record RecoveryGrantResponse(
    string Outcome,
    IReadOnlyList<string> Codes,
    RecoveryGrantPlan? Plan,
    string? ConfirmationCode,
    RecoveryGrantView? Grant,
    string? AuditRecordId);

/// <summary>A grant as the entries show it.</summary>
public sealed record RecoveryGrantView(
    string GrantId,
    string AgvId,
    string VehicleKey,
    string? JourneyId,
    IReadOnlyList<string> Actions,
    string OperatorId,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    string AuditRecordId)
{
    public static RecoveryGrantView Of(RecoveryGrant grant) => new(
        grant.GrantId, grant.AgvId, grant.VehicleKey, grant.JourneyId, [.. grant.Actions], grant.OperatorId, grant.GrantedAt,
        grant.ExpiresAt, grant.AuditRecordId);
}

/// <summary>
/// The control host's grant entries (control-server#797, ADR-cross-0066 decision 1), in the FieldOps group beside the station
/// exclusivity release and under the same credential, mapped with it when <c>VehicleFaultRecovery:enabled</c>.
/// </summary>
/// <remarks>
/// 200 for a rehearsal (<c>PLANNED</c>, with the code), a grant and a revocation; 409 with every code when refused, a stale
/// confirmation included (with the new plan and code); 404 for a vehicle this server does not drive; 401 without the credential;
/// 503 when the credential variable is empty. The list is the grants in effect now.
/// </remarks>
public static class RecoveryGrantEndpoints
{
    public const string GrantRoute = "/api/field-ops/v1/recovery-grants";
    public const string RevocationRoute = "/api/field-ops/v1/recovery-grant-revocations";

    public static void MapRecoveryGrants(this WebApplication app)
    {
        app.MapPost(GrantRoute, GrantAsync)
            .WithName("GrantVehicleRecovery")
            .WithSummary("Grant one vehicle, for a while, the recovery actions named (control-server#797, ADR-cross-0066)")
            .Produces<RecoveryGrantResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        app.MapGet(GrantRoute, ListAsync)
            .WithName("ListVehicleRecoveryGrants")
            .WithSummary("The recovery grants in effect (control-server#797)")
            .Produces<IReadOnlyList<RecoveryGrantView>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        app.MapPost(RevocationRoute, RevokeAsync)
            .WithName("RevokeVehicleRecoveryGrant")
            .WithSummary("End a vehicle's recovery grant now (control-server#797)")
            .Produces<RecoveryGrantResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<Ok<RecoveryGrantResponse>, UnauthorizedHttpResult, ProblemHttpResult>> GrantAsync(
        HttpContext context,
        RecoveryGrantHttpRequest request,
        RecoveryGrantIssuance issuance,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(recoveryOptions);
        if (Authenticate(context, recoveryOptions.Value) is { } refused)
        {
            return refused;
        }
        RecoveryGrantResult result = await issuance.GrantAsync(
            new RecoveryGrantRequest(
                request?.VehicleKey,
                request?.JourneyId,
                request?.Actions,
                request?.Minutes,
                request?.OperatorId,
                request?.Reason,
                request?.SiteVerification,
                request?.ClaimedRole,
                request?.ConfirmationCode),
            cancellationToken).ConfigureAwait(false);
        return Answer(result, "Recovery grant refused");
    }

    public static async Task<Results<Ok<RecoveryGrantResponse>, UnauthorizedHttpResult, ProblemHttpResult>> RevokeAsync(
        HttpContext context,
        RecoveryGrantRevocationHttpRequest request,
        RecoveryGrantIssuance issuance,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(recoveryOptions);
        if (Authenticate(context, recoveryOptions.Value) is { } refused)
        {
            return refused;
        }
        RecoveryGrantResult result = await issuance.RevokeAsync(
            new RecoveryGrantRevocationRequest(request?.VehicleKey, request?.OperatorId, request?.Reason, request?.ClaimedRole),
            cancellationToken).ConfigureAwait(false);
        return Answer(result, "Recovery grant revocation refused");
    }

    public static Results<Ok<IReadOnlyList<RecoveryGrantView>>, UnauthorizedHttpResult, ProblemHttpResult> ListAsync(
        HttpContext context,
        RecoveryGrantRegistry registry,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(recoveryOptions);
        context.Response.Headers.CacheControl = "no-store";
        string? expected = Environment.GetEnvironmentVariable(recoveryOptions.Value.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recovery grants unavailable");
        if (!Presented(context, expected))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }
        return TypedResults.Ok<IReadOnlyList<RecoveryGrantView>>([.. registry.InEffect().Select(RecoveryGrantView.Of)]);
    }

    private static Results<Ok<RecoveryGrantResponse>, UnauthorizedHttpResult, ProblemHttpResult> Answer(
        RecoveryGrantResult result,
        string refusedTitle)
    {
        RecoveryGrantResponse body = new(
            result.Outcome,
            result.Codes,
            result.Plan,
            result.ConfirmationCode,
            result.Grant is null ? null : RecoveryGrantView.Of(result.Grant),
            result.AuditRecordId);
        if (result.Outcome != RecoveryGrantIssuance.Rejected)
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: result.UnknownVehicle ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict,
            title: refusedTitle,
            detail: string.Join(',', result.Codes),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["outcome"] = body.Outcome,
                ["codes"] = body.Codes,
                ["plan"] = body.Plan,
                ["confirmationCode"] = body.ConfirmationCode,
                ["auditRecordId"] = body.AuditRecordId,
            });
    }

    private static Results<Ok<RecoveryGrantResponse>, UnauthorizedHttpResult, ProblemHttpResult>? Authenticate(
        HttpContext context,
        VehicleFaultRecoveryOptions options)
    {
        context.Response.Headers.CacheControl = "no-store";
        string? expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recovery grants unavailable");
        if (!Presented(context, expected))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }
        return null;
    }

    private static bool Presented(HttpContext context, string expected) =>
        AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) &&
        string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(header.Parameter) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter));
}
