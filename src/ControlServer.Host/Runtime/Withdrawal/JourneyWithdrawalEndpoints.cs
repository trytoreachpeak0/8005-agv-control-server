using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Withdrawal;

/// <summary>What a person submits: rehearse without <c>confirmationCode</c>, withdraw with the rehearsal's.</summary>
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
public sealed record JourneyWithdrawalResponse(
    string Outcome,
    IReadOnlyList<string> Codes,
    JourneyWithdrawalPlan? Plan,
    string? ConfirmationCode,
    string? AuditRecordId,
    string? RevokedGrantId,
    DateTimeOffset? RiotObservedAt);

/// <summary>The control host's journey withdrawal entry (control-server#771 part 2).</summary>
public static class JourneyWithdrawalEndpoints
{
    public const string Route = "/api/field-ops/v1/journey-withdrawals";

    public static void MapJourneyWithdrawal(this WebApplication app)
    {
    }

    public static Task<Results<Ok<JourneyWithdrawalResponse>, UnauthorizedHttpResult, ProblemHttpResult>> HandleAsync(
        HttpContext context,
        JourneyWithdrawalHttpRequest request,
        [Microsoft.AspNetCore.Mvc.FromServices] JourneyWithdrawalService service,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
