using System.Globalization;
using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>
/// Publishes a new version of a governed object: freeze the complete content, then record the
/// action. The two happen together and in that order, so an audit record can always name the
/// snapshot it describes.
/// </summary>
/// <remarks>
/// This is the mechanism, not a business rule. What a version of a slot template means, when one may
/// be published, and who may ask for it are decided by the callers in FP-C7, FP-C5 and FP-C9b -- all
/// three publish through here rather than each building their own.
/// </remarks>
public sealed class GovernedConfigurationPublisher(
    IConfigurationSnapshotStore snapshots,
    IGovernanceAuditWriter auditWriter)
{
    private readonly IConfigurationSnapshotStore _snapshots =
        snapshots ?? throw new ArgumentNullException(nameof(snapshots));
    private readonly IGovernanceAuditWriter _auditWriter =
        auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));

    public async Task<GovernedConfigurationSnapshot> PublishVersionAsync(
        GovernedObjectKind objectKind,
        string objectId,
        long version,
        string contentJson,
        string action,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken,
        string? claimedAdministratorRole = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        GovernedConfigurationSnapshot snapshot = await _snapshots.FreezeAsync(
            objectKind, objectId, version, contentJson, occurredAt, cancellationToken);

        GovernanceAuditEntry entry = new(
            action,
            objectKind,
            objectId,
            version,
            GovernanceActionOutcome.Succeeded,
            DetailFor(snapshot),
            snapshot.SnapshotId,
            claimedAdministratorRole);
        await _auditWriter.WriteBusinessAsync(entry, occurredAt, cancellationToken);
        if (claimedAdministratorRole is not null)
        {
            await _auditWriter.WriteAdministratorAsync(entry, occurredAt, cancellationToken);
        }
        return snapshot;
    }

    private static string DetailFor(GovernedConfigurationSnapshot snapshot) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"contentSha256":"{{snapshot.ContentSha256}}","frozenAt":"{{snapshot.FrozenAt:O}}"}""");
}
