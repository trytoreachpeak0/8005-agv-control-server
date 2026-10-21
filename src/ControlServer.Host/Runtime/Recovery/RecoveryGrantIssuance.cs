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
    public const string JourneyRequired = "RECOVERY_GRANT_JOURNEY_REQUIRED";
    public const string FieldTooLong = "RECOVERY_GRANT_FIELD_TOO_LONG";
    public const string ConfirmationStale = "RECOVERY_GRANT_CONFIRMATION_STALE";
    public const string NotInEffect = "RECOVERY_GRANT_NOT_IN_EFFECT";

    public const int MaxTextLength = 500;
    public const int MaxIdentifierLength = 128;

    private static readonly JsonSerializerOptions DetailOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>Rehearses, or -- with the rehearsal's code -- grants.</summary>
    public async Task<RecoveryGrantResult> GrantAsync(RecoveryGrantRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RecoveryGrantOptions settings = options.Value;
        string? vehicleKey = Trimmed(request.VehicleKey);
        string? journeyId = Trimmed(request.JourneyId);
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? siteVerification = Trimmed(request.SiteVerification);
        string? claimedRole = Trimmed(request.ClaimedRole);
        string? confirmation = Trimmed(request.ConfirmationCode);
        string[] named = [.. (request.Actions ?? []).Select(Trimmed).OfType<string>().Distinct(StringComparer.Ordinal)];
        ImmutableSortedSet<string> actions = named.Length == 0
            ? RecoveryGrantActions.Default
            : ImmutableSortedSet.Create(StringComparer.Ordinal, named);
        int minutes = request.Minutes ?? settings.DefaultMinutes;

        List<string> codes = [];
        if (operatorId is null) codes.Add(OperatorRequired);
        if (reason is null) codes.Add(ReasonRequired);
        if (siteVerification is null) codes.Add(SiteVerificationRequired);
        bool fits = Length(reason) <= MaxTextLength && Length(siteVerification) <= MaxTextLength &&
                    Length(operatorId) <= MaxIdentifierLength && Length(claimedRole) <= MaxIdentifierLength &&
                    Length(vehicleKey) <= MaxIdentifierLength && Length(journeyId) <= MaxIdentifierLength &&
                    Length(confirmation) <= MaxIdentifierLength && named.All(action => action.Length <= MaxIdentifierLength);
        if (!fits) codes.Add(FieldTooLong);
        if (actions.Any(action => !RecoveryGrantActions.All.Contains(action))) codes.Add(ActionUnknown);
        if (minutes < 1 || minutes > settings.MaximumMinutes) codes.Add(DurationOutOfRange);

        FleetVehicle? vehicle = null;
        if (vehicleKey is null) codes.Add(VehicleRequired);
        else if ((vehicle = roster.ByVehicleKey(vehicleKey)) is null) codes.Add(VehicleUnknown);

        RecoveryGrantJourney? namedJourney = null;
        RecoveryGrantJourney? vehicleJourney = null;
        if (vehicle is not null)
        {
            vehicleJourney = await UnfinishedJourneyAsync(vehicle.AgvId, cancellationToken).ConfigureAwait(false);
            // cs#797 review L3: an action on a journey, asked for without one while the vehicle has a journey, could only ever
            // be refused on that journey; say so now rather than grant something that covers nothing. A vehicle with no
            // unfinished journey may be granted those actions vehicle-wide (a session with no demand).
            if (journeyId is null && vehicleJourney is not null && actions.Any(RecoveryGrantActions.ActsOnJourney))
                codes.Add(JourneyRequired);
            if (journeyId is not null && fits)
            {
                JourneyRuntimeRow? row = await dbContext.JourneyRuntimes.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.JourneyId == journeyId && item.AgvId == vehicle.AgvId, cancellationToken)
                    .ConfigureAwait(false);
                if (row is null) codes.Add(JourneyNotFound);
                else
                {
                    namedJourney = new RecoveryGrantJourney(row.JourneyId, row.Stage.ToString(), row.BlockReasonCode);
                    if (row.Stage == JourneyRuntimeStage.Completed) codes.Add(JourneyCompleted);
                }
            }
        }
        RecoveryGrant? current = vehicle is null ? null : registry.Current(vehicle.AgvId);
        RecoveryGrantPlan plan = new(
            vehicle?.AgvId,
            fits ? vehicleKey : null,
            fits ? journeyId : null,
            namedJourney,
            vehicleJourney,
            fits ? [.. actions] : [],
            minutes,
            fits ? operatorId : null,
            fits ? reason : null,
            fits ? siteVerification : null,
            current is null ? null : new RecoveryGrantReplaced(current.GrantId, current.JourneyId, [.. current.Actions], current.ExpiresAt));
        string code = CodeOf(plan);
        DateTimeOffset now = timeProvider.GetUtcNow();

        if (codes.Count > 0)
        {
            if (confirmation is null)
            {
                return new RecoveryGrantResult(Rejected, codes, plan, null, null, null);
            }
            string refused = await WriteAuditAsync(
                GrantAuditAction, vehicleKey, fits, GovernanceActionOutcome.Failed, claimedRole, now,
                new { said = Said(), plan, codes, result = Rejected }, cancellationToken).ConfigureAwait(false);
            return new RecoveryGrantResult(Rejected, codes, plan, null, null, refused);
        }
        if (confirmation is null)
        {
            return new RecoveryGrantResult(Planned, [], plan, code, null, null);
        }
        if (!string.Equals(confirmation, code, StringComparison.Ordinal))
        {
            string stale = await WriteAuditAsync(
                GrantAuditAction, vehicleKey, fits, GovernanceActionOutcome.Failed, claimedRole, now,
                new { said = Said(), plan, codes = new[] { ConfirmationStale }, result = Rejected }, cancellationToken)
                .ConfigureAwait(false);
            return new RecoveryGrantResult(Rejected, [ConfirmationStale], plan, code, null, stale);
        }

        string grantId = Guid.NewGuid().ToString("D");
        DateTimeOffset expiresAt = now.AddMinutes(minutes);
        // The audit first: a grant that could not be recorded is not given.
        string auditRecordId = await WriteAuditAsync(
            GrantAuditAction, vehicleKey, fits, GovernanceActionOutcome.Succeeded, claimedRole, now,
            new { said = Said(), plan, grantId, expiresAt, result = Granted }, cancellationToken).ConfigureAwait(false);
        RecoveryGrant grant = new(
            grantId, vehicle!.AgvId, vehicle.VehicleKey, journeyId, actions, operatorId!, now, expiresAt, auditRecordId);
        registry.Grant(grant);
        return new RecoveryGrantResult(Granted, [], plan, code, grant, auditRecordId);

        object Said() => fits
            ? new { vehicleKey, journeyId, actions = named, minutes = request.Minutes, operatorId, reason, siteVerification, claimedRole, confirmationCode = confirmation }
            : new
            {
                vehicleKeyLength = vehicleKey?.Length,
                journeyIdLength = journeyId?.Length,
                operatorIdLength = operatorId?.Length,
                reasonLength = reason?.Length,
                siteVerificationLength = siteVerification?.Length
            };
    }

    /// <summary>Ends a vehicle's grant now.</summary>
    public async Task<RecoveryGrantResult> RevokeAsync(RecoveryGrantRevocationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? vehicleKey = Trimmed(request.VehicleKey);
        string? operatorId = Trimmed(request.OperatorId);
        string? reason = Trimmed(request.Reason);
        string? claimedRole = Trimmed(request.ClaimedRole);
        List<string> codes = [];
        if (operatorId is null) codes.Add(OperatorRequired);
        if (reason is null) codes.Add(ReasonRequired);
        bool fits = Length(reason) <= MaxTextLength && Length(operatorId) <= MaxIdentifierLength &&
                    Length(claimedRole) <= MaxIdentifierLength && Length(vehicleKey) <= MaxIdentifierLength;
        if (!fits) codes.Add(FieldTooLong);
        FleetVehicle? vehicle = null;
        if (vehicleKey is null) codes.Add(VehicleRequired);
        else if ((vehicle = roster.ByVehicleKey(vehicleKey)) is null) codes.Add(VehicleUnknown);
        RecoveryGrant? current = vehicle is null ? null : registry.Current(vehicle.AgvId);
        if (vehicle is not null && current is null) codes.Add(NotInEffect);

        DateTimeOffset now = timeProvider.GetUtcNow();
        object said = fits
            ? new { vehicleKey, operatorId, reason, claimedRole }
            : new { vehicleKeyLength = vehicleKey?.Length, operatorIdLength = operatorId?.Length, reasonLength = reason?.Length };
        if (codes.Count > 0)
        {
            string refused = await WriteAuditAsync(
                RevokeAuditAction, vehicleKey, fits, GovernanceActionOutcome.Failed, claimedRole, now,
                new { said, codes, result = Rejected }, cancellationToken).ConfigureAwait(false);
            return new RecoveryGrantResult(Rejected, codes, null, null, null, refused);
        }
        string auditRecordId = await WriteAuditAsync(
            RevokeAuditAction, vehicleKey, fits, GovernanceActionOutcome.Succeeded, claimedRole, now,
            new { said, revoked = current, result = Revoked }, cancellationToken).ConfigureAwait(false);
        registry.Revoke(vehicle!.AgvId);
        return new RecoveryGrantResult(Revoked, [], null, null, current, auditRecordId);
    }

    private async Task<RecoveryGrantJourney?> UnfinishedJourneyAsync(string agvId, CancellationToken cancellationToken)
    {
        JourneyRuntimeRow[] rows = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Length == 1 ? new RecoveryGrantJourney(rows[0].JourneyId, rows[0].Stage.ToString(), rows[0].BlockReasonCode) : null;
    }

    private static string CodeOf(RecoveryGrantPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan, DetailOptions)))[..12];

    private Task<string> WriteAuditAsync(
        string action,
        string? vehicleKey,
        bool fits,
        GovernanceActionOutcome outcome,
        string? claimedRole,
        DateTimeOffset now,
        object detail,
        CancellationToken cancellationToken) =>
        audit.WriteAdministratorAsync(
            new GovernanceAuditEntry(
                action,
                GovernedObjectKind.RecoveryGrant,
                fits && vehicleKey is not null ? "vehicle:" + vehicleKey : "(none)",
                null,
                outcome,
                JsonSerializer.Serialize(detail, DetailOptions),
                ClaimedAdministratorRole: fits ? claimedRole : null),
            now,
            cancellationToken);

    private static int Length(string? text) => text?.Length ?? 0;

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
