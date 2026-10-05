using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>
/// A request to close a vehicle's exception recovery session whose action's result will never come (control-server#483). The
/// vehicle has at most one open session and is what names it; the session id is optional, and must match when given.
/// </summary>
public sealed record RecoverySessionAdministratorCloseHttpRequest(
    string? AgvId,
    string? ExceptionRecoverySessionId,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole);

/// <summary>
/// The decision, closed or refused. <c>ExceptionRecoverySessionId</c> is the session judged -- the one closed, or the vehicle's
/// open one it refused to close -- and null when the vehicle has none. <c>Descriptions</c> carries the Chinese text of every
/// refusal code.
/// </summary>
public sealed record RecoverySessionAdministratorCloseResponse(
    string AgvId,
    string? ExceptionRecoverySessionId,
    string Outcome,
    IReadOnlyList<string> Codes,
    IReadOnlyDictionary<string, string> Descriptions,
    string AuditRecordId);

/// <summary>
/// The server-side entry out of an exception recovery session stuck in EXECUTING because its result will never come
/// (control-server#483): a named person on site says why, and the server closes the vehicle's open session if it is still
/// awaiting that result and the vehicle, when connected, does not report the result as still on its way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="RecoverySessionAdministratorClose"/></b>; this authenticates the call and maps the
/// decision onto HTTP. Same credential and same switch as <see cref="VehicleFaultRecoveryEndpoints"/>: it is the same kind of
/// act, a named person on site deciding about a stuck vehicle, and it loosens. Mapped only when
/// <c>VehicleFaultRecovery:enabled</c>.
/// </para>
/// <para>
/// 200 when closed, with the session closed; 409 with every code (and its Chinese description) when refused; 401 without the
/// credential; 422 when no vehicle is named; 503 when the credential variable is empty. Every request that gets past those
/// writes an administrator audit record, and so, on a best-effort basis, does one that fails with an exception.
/// </para>
/// </remarks>
public static class RecoverySessionAdministratorCloseEndpoints
{
    public const string Route = "/api/field-ops/v1/exception-recovery-session-closures";

    public static void MapRecoverySessionAdministratorClose(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("CloseExceptionRecoverySessionByHand")
            .WithSummary("Close an exception recovery session whose result will never come (control-server#483)")
            .WithDescription(
                "Checks the operator, the reason and the site verification, that the named vehicle's one open session is EXECUTING "
                + "a RESUME_AFTER_REPAIR still awaiting its result (and is the session named, when one is), and that a connected "
                + "vehicle does not report the resume's attempt or any result as pending; then judges the resume RecoveryRequired, "
                + "settles its command and "
                + "closes the session with a CLOSED snapshot. The demand, journey, lease and vehicle stay as they are, for a new "
                + "session opened on the vehicle.")
            .Produces<RecoverySessionAdministratorCloseResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<RecoverySessionAdministratorCloseResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        RecoverySessionAdministratorCloseHttpRequest request,
        RecoverySessionAdministratorClose closing,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(closing);
        ArgumentNullException.ThrowIfNull(recoveryOptions);

        context.Response.Headers.CacheControl = "no-store";
        string? expected = Environment.GetEnvironmentVariable(recoveryOptions.Value.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Exception recovery session closing unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        // A missing operator, reason or site verification is an unmet criterion, audited with the others; only a request that
        // names no vehicle has nothing to judge.
        if (request is null || string.IsNullOrWhiteSpace(request.AgvId))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete exception recovery session closing request",
                detail: "agvId is required.");
        }

        string agvId = request.AgvId.Trim();
        RecoverySessionAdministratorCloseResult result = await closing.CloseAsync(
            new RecoverySessionAdministratorCloseRequest(
                agvId, request.ExceptionRecoverySessionId, request.OperatorId, request.Reason, request.SiteVerification,
                request.ClaimedRole),
            cancellationToken).ConfigureAwait(false);

        RecoverySessionAdministratorCloseResponse body = new(
            agvId,
            result.ExceptionRecoverySessionId,
            result.Closed ? "CLOSED" : "REJECTED",
            result.Codes,
            result.Codes.Distinct(StringComparer.Ordinal).ToDictionary(
                code => code,
                code => RecoverySessionAdministratorCloseCodes.Descriptions.GetValueOrDefault(code, code),
                StringComparer.Ordinal),
            result.AuditRecordId);
        if (result.Closed)
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Exception recovery session closing refused",
            detail: string.Join(',', result.Codes),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agvId"] = body.AgvId,
                ["exceptionRecoverySessionId"] = body.ExceptionRecoverySessionId,
                ["outcome"] = body.Outcome,
                ["codes"] = body.Codes,
                ["descriptions"] = body.Descriptions,
                ["auditRecordId"] = body.AuditRecordId,
            });
    }
}
