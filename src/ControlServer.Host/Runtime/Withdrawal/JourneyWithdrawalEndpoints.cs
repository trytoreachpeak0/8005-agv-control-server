using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Withdrawal;

/// <summary>What a person submits: rehearse without <c>confirmationCode</c>, withdraw with the rehearsal's.</summary>
/// <param name="RiotVerification">Optional online and recorded as said: the server reads RIoT for itself.</param>
public sealed record JourneyWithdrawalHttpRequest(
    string? JourneyId,
    string? VehicleKey,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? RiotVerification,
    string? SlotFacts,
    string? CargoLocation,
    string? ClaimedRole,
    string? ConfirmationCode);

/// <summary>What a rehearsal or a withdrawal came to.</summary>
/// <param name="RevokedGrantId">The recovery grant naming this journey that went with it, if there was one.</param>
/// <param name="RiotObservedAt">When the server read RIoT for this request.</param>
public sealed record JourneyWithdrawalResponse(
    string Outcome,
    IReadOnlyList<string> Codes,
    JourneyWithdrawalPlan? Plan,
    string? ConfirmationCode,
    string? AuditRecordId,
    string? RevokedGrantId,
    DateTimeOffset? RiotObservedAt);

/// <summary>
/// The control host's journey withdrawal entry (control-server#771 part 2): the same withdrawal FieldOps writes to the database
/// while the server is stopped, made while it runs -- no stop, no gate to close, the other vehicles untouched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="JourneyWithdrawalService"/></b> over <see cref="JourneyWithdrawal"/>; this authenticates
/// the call and maps the answer onto HTTP. Without <c>confirmationCode</c> it rehearses; with it, it withdraws.
/// </para>
/// <para>
/// <b>Same credential, same switch as the recovery grants and the station exclusivity release</b>
/// (<see cref="VehicleFaultRecoveryEndpoints.MapVehicleFaultRecoveryEntriesWhenEnabled"/>): the switch is on in production since
/// control-server#844, so the entry is there without a restart (control-server#797 Q4). It does not check a recovery grant:
/// ADR-cross-0066 leaves the withdrawal outside the grant -- it closes a journey and opens no recovery session.
/// </para>
/// <para>
/// 200 for a rehearsal (<c>PLANNED</c>, with the code), a withdrawal (<c>WITHDRAWN</c>) and a repeat (<c>ALREADY_DONE</c>); 409 with
/// every code when refused, a stale confirmation included (with the plan as it now stands and its code); 503 when the runtime
/// round did not let go in time or the credential variable is empty; 401 without the credential.
/// </para>
/// </remarks>
public static class JourneyWithdrawalEndpoints
{
    public const string Route = "/api/field-ops/v1/journey-withdrawals";

    public static void MapJourneyWithdrawal(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("WithdrawJourneyByHand")
            .WithSummary("Withdraw a stuck journey with nothing on board while the server runs (control-server#771 part 2)")
            .WithDescription(
                "Without confirmationCode, rehearses: reads RIoT for the vehicle's unfinished orders, judges the journey as the "
                + "offline withdrawal does and answers the plan and its confirmation code, writing nothing. With it, withdraws under "
                + "the runtime round's gate: the one unresolved load is cancelled, every open demand goes back to the backlog "
                + "unsuppressed, the journey closes WITHDRAWN_BY_OPERATOR, the closure snapshots go to the vehicle, and a recovery "
                + "grant naming this journey is revoked with it.")
            .Produces<JourneyWithdrawalResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<Ok<JourneyWithdrawalResponse>, UnauthorizedHttpResult, ProblemHttpResult>> HandleAsync(
        HttpContext context,
        JourneyWithdrawalHttpRequest request,
        [Microsoft.AspNetCore.Mvc.FromServices] JourneyWithdrawalService service,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(recoveryOptions);

        context.Response.Headers.CacheControl = "no-store";
        string? expected = Environment.GetEnvironmentVariable(recoveryOptions.Value.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Journey withdrawal unavailable");
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        JourneyWithdrawalRequest asked = new(
            request?.JourneyId,
            request?.VehicleKey,
            request?.OperatorId,
            request?.Reason,
            request?.SiteVerification,
            request?.RiotVerification,
            request?.SlotFacts,
            request?.CargoLocation,
            request?.ClaimedRole,
            request?.ConfirmationCode);
        JourneyWithdrawalResult result = string.IsNullOrWhiteSpace(asked.ConfirmationCode)
            ? await service.PlanAsync(asked, cancellationToken).ConfigureAwait(false)
            : await service.WithdrawAsync(asked, cancellationToken).ConfigureAwait(false);

        JourneyWithdrawalResponse body = new(
            result.Outcome,
            result.Codes,
            result.Plan,
            result.ConfirmationCode,
            result.AuditRecordId,
            result.RevokedGrantId,
            result.RiotObservedAt);
        if (result.Outcome != "REJECTED")
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: result.Codes is [JourneyWithdrawal.RuntimeBusy]
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status409Conflict,
            title: "Journey withdrawal refused",
            detail: string.Join(',', result.Codes),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["outcome"] = body.Outcome,
                ["codes"] = body.Codes,
                ["plan"] = body.Plan,
                ["confirmationCode"] = body.ConfirmationCode,
                ["auditRecordId"] = body.AuditRecordId,
                ["riotObservedAt"] = body.RiotObservedAt,
            });
    }
}
