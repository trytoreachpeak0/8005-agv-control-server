using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Recovery;

/// <summary>What a person asks for when granting.</summary>
/// <param name="Actions">The actions named; empty or null for <see cref="RecoveryGrantActions.Default"/>.</param>
/// <param name="Minutes">The duration; null for <see cref="RecoveryGrantOptions.DefaultMinutes"/>.</param>
/// <param name="ConfirmationCode">Null to rehearse; the rehearsal's code to grant.</param>
public sealed record RecoveryGrantRequest(
    string? VehicleKey,
    string? JourneyId,
    IReadOnlyList<string>? Actions,
    int? Minutes,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    string? ClaimedRole,
    string? ConfirmationCode);

/// <summary>What a person asks for when revoking.</summary>
public sealed record RecoveryGrantRevocationRequest(
    string? VehicleKey,
    string? OperatorId,
    string? Reason,
    string? ClaimedRole);

/// <summary>A journey as the person is shown it.</summary>
public sealed record RecoveryGrantJourney(string JourneyId, string Stage, string? BlockReasonCode);

/// <summary>
/// What a grant would do, as the rehearsal shows it and its code covers: the vehicle, the journey asked for and the one the vehicle
/// has now, the actions, the duration, who and why, and the grant it would replace (a renewal).
/// </summary>
public sealed record RecoveryGrantPlan(
    string? AgvId,
    string? VehicleKey,
    string? JourneyId,
    RecoveryGrantJourney? NamedJourney,
    RecoveryGrantJourney? VehicleJourney,
    IReadOnlyList<string> Actions,
    int Minutes,
    string? OperatorId,
    string? Reason,
    string? SiteVerification,
    RecoveryGrantReplaced? Replaces);

/// <summary>The grant a new one replaces.</summary>
public sealed record RecoveryGrantReplaced(string GrantId, string? JourneyId, IReadOnlyList<string> Actions, DateTimeOffset ExpiresAt);

/// <summary>What a grant or revocation came to.</summary>
/// <param name="Outcome"><c>PLANNED</c>, <c>GRANTED</c>, <c>REVOKED</c> or <c>REJECTED</c>.</param>
/// <param name="AuditRecordId">Null for a rehearsal, which writes nothing.</param>
public sealed record RecoveryGrantResult(
    string Outcome,
    IReadOnlyList<string> Codes,
    RecoveryGrantPlan? Plan,
    string? ConfirmationCode,
    RecoveryGrant? Grant,
    string? AuditRecordId)
{
    public bool UnknownVehicle => Codes.Contains(RecoveryGrantIssuance.VehicleUnknown);
}

/// <summary>
/// Gives, renews and revokes recovery grants (control-server#797, ADR-cross-0066 decisions 1 and 2): rehearse to see the plan
/// and its confirmation code, then grant with that code; every grant, revocation and refusal past the rehearsal writes an
/// administrator audit record. Rehearsing writes nothing.
/// </summary>
/// <remarks>
/// The confirmation code is a digest of the whole plan the person is shown, the journey's stage and the grant it would replace
/// included: if any of it changes between the rehearsal and the grant, the grant is refused with the new plan and code
/// (<see cref="ConfirmationStale"/>), as control-server#771's withdrawal does.
/// </remarks>
public sealed class RecoveryGrantIssuance(
    ControlServerDbContext dbContext,
    VehicleRoster roster,
    RecoveryGrantRegistry registry,
    IGovernanceAuditWriter audit,
    IOptions<RecoveryGrantOptions> options,
    TimeProvider timeProvider)
{
    public const string GrantAuditAction = "RECOVERY_GRANT";
    public const string RevokeAuditAction = "RECOVERY_GRANT_REVOCATION";

    public const string Planned = "PLANNED";
    public const string Granted = "GRANTED";
    public const string Revoked = "REVOKED";
    public const string Rejected = "REJECTED";

    public const string OperatorRequired = "RECOVERY_GRANT_OPERATOR_REQUIRED";
    public const string ReasonRequired = "RECOVERY_GRANT_REASON_REQUIRED";
    public const string SiteVerificationRequired = "RECOVERY_GRANT_SITE_VERIFICATION_REQUIRED";
    public const string VehicleRequired = "RECOVERY_GRANT_VEHICLE_REQUIRED";
    public const string VehicleUnknown = "RECOVERY_GRANT_VEHICLE_UNKNOWN";
    public const string ActionUnknown = "RECOVERY_GRANT_ACTION_UNKNOWN";
    public const string DurationOutOfRange = "RECOVERY_GRANT_DURATION_OUT_OF_RANGE";
    public const string JourneyNotFound = "RECOVERY_GRANT_JOURNEY_NOT_FOUND";
    public const string JourneyCompleted = "RECOVERY_GRANT_JOURNEY_COMPLETED";
    public const string FieldTooLong = "RECOVERY_GRANT_FIELD_TOO_LONG";
    public const string ConfirmationStale = "RECOVERY_GRANT_CONFIRMATION_STALE";
    public const string NotInEffect = "RECOVERY_GRANT_NOT_IN_EFFECT";

    public const int MaxTextLength = 500;
    public const int MaxIdentifierLength = 128;

    private static readonly JsonSerializerOptions DetailOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>control-server#797 stub: grants nothing yet.</summary>
    public Task<RecoveryGrantResult> GrantAsync(RecoveryGrantRequest request, CancellationToken cancellationToken)
    {
        _ = request; _ = cancellationToken; _ = dbContext; _ = roster; _ = registry; _ = audit; _ = options; _ = timeProvider;
        return Task.FromResult(new RecoveryGrantResult(Rejected, [], null, null, null, null));
    }

    /// <summary>control-server#797 stub: revokes nothing yet.</summary>
    public Task<RecoveryGrantResult> RevokeAsync(RecoveryGrantRevocationRequest request, CancellationToken cancellationToken)
    {
        _ = request; _ = cancellationToken;
        return Task.FromResult(new RecoveryGrantResult(Rejected, [], null, null, null, null));
    }
    private static int Length(string? text) => text?.Length ?? 0;

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
