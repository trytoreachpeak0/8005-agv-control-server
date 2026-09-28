using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ControlServer.Domain;

namespace ControlServer.Application;

/// <summary>The outcome of accepting a Map's new name as its baseline.</summary>
public sealed record MapNameBaselineAcceptResult(
    bool Accepted,
    int MapId,
    string? PreviousName,
    string? AcceptedName,
    IReadOnlyList<TaskTypeStationViolation> Violations,
    string AuditRecordId);

/// <summary>
/// The first half of recovering from a Map rename (control-server#186, REQ-0340 recovery half): the name read since the
/// rename becomes the Map's baseline, after the field has checked the Map on site. The holds the rename raised are
/// released afterwards, task type by task type, by the batch 6-05 release verb, which refuses while a rename is pending.
/// </summary>
/// <remarks>
/// FieldOps cannot reach RIoT, so the name is typed in. It must be byte for byte the pending name the server read:
/// anything else is refused and audited, so a typo can never become a baseline. Every outcome is audited, refusals too.
/// </remarks>
public sealed class MapNameBaselineAcceptanceService(IMapNameBaselineStore baselines, IGovernanceAuditWriter audit)
{
    /// <summary>The prefix of <see cref="MapNameBaseline.AcceptedBy"/>, followed by the acceptance's audit record id.</summary>
    public const string AcceptedByPrefix = "fieldops:accept-map-name:";

    private static readonly JsonSerializerOptions AuditJson = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly IMapNameBaselineStore _baselines = baselines ?? throw new ArgumentNullException(nameof(baselines));
    private readonly IGovernanceAuditWriter _audit = audit ?? throw new ArgumentNullException(nameof(audit));

    public async Task<MapNameBaselineAcceptResult> AcceptAsync(
        int mapId,
        string? mapName,
        TaskTypeStationChangeRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Reason);

        MapNameBaseline? baseline = await _baselines.ReadAsync(mapId, cancellationToken);
        TaskTypeStationViolation? refusal = Judge(mapId, mapName, baseline);
        if (refusal is null && mapName is not null)
        {
            string? auditId = await _baselines.AcceptAsync(
                mapId,
                mapName,
                AcceptedByPrefix,
                current => Entry(mapId, mapName, current, request, []),
                now,
                cancellationToken);
            if (auditId is not null)
            {
                return new MapNameBaselineAcceptResult(true, mapId, baseline!.Name, mapName, [], auditId);
            }
            // The pending name moved between the read above and the write: judge again on what is there now.
            baseline = await _baselines.ReadAsync(mapId, cancellationToken);
            refusal = Judge(mapId, mapName, baseline) ?? new TaskTypeStationViolation(
                MapNameBaselineReasonCodes.NameMismatch, null, null,
                Invariant($"Map {mapId}'s pending name changed while it was being accepted; nothing was accepted."));
        }

        TaskTypeStationViolation[] violations = [refusal!];
        string rejectedId = await _audit.WriteBusinessAsync(Entry(mapId, mapName, baseline, request, violations), now, cancellationToken);
        return new MapNameBaselineAcceptResult(false, mapId, baseline?.Name, null, violations, rejectedId);
    }

    private static TaskTypeStationViolation? Judge(int mapId, string? mapName, MapNameBaseline? baseline)
    {
        if (baseline is null)
        {
            return new(MapNameBaselineReasonCodes.BaselineMissing, null, null,
                Invariant($"Map {mapId} has no name baseline yet: the server has not read its name, so there is nothing to accept."));
        }
        if (baseline.PendingName is null)
        {
            return new(MapNameBaselineReasonCodes.NoPendingRename, null, null,
                Invariant($"Map {mapId} has no rename waiting to be accepted; its baseline name is '{baseline.Name}'."));
        }
        if (!string.Equals(baseline.PendingName, mapName, StringComparison.Ordinal))
        {
            return new(MapNameBaselineReasonCodes.NameMismatch, null, null,
                Invariant($"Map {mapId}'s name waiting to be accepted is '{baseline.PendingName}', not '{mapName}'; the names must match byte for byte."));
        }
        return null;
    }

    private static GovernanceAuditEntry Entry(
        int mapId,
        string? mapName,
        MapNameBaseline? baseline,
        TaskTypeStationChangeRequest request,
        IReadOnlyList<TaskTypeStationViolation> refused)
    {
        bool ok = refused.Count == 0;
        return new GovernanceAuditEntry(
            ok ? MapNameBaselineAuditActions.Accepted : MapNameBaselineAuditActions.AcceptRejected,
            GovernedObjectKind.PublicStationBinding,
            TaskTypeStationGovernance.BindingSetObjectId(mapId),
            null,
            ok ? GovernanceActionOutcome.Succeeded : GovernanceActionOutcome.Failed,
            JsonSerializer.Serialize(
                new
                {
                    mapId,
                    requestCategory = "ACCEPT_MAP_NAME",
                    reason = request.Reason,
                    selfReportedRole = request.SelfReportedRole,
                    previousName = baseline?.Name,
                    pendingName = baseline?.PendingName,
                    pendingSince = baseline?.PendingSince,
                    requestedName = mapName,
                    acceptedName = ok ? mapName : null,
                    validation = new
                    {
                        passed = ok,
                        violations = refused.Select(violation => new { reasonCode = violation.ReasonCode, detail = violation.Detail })
                    },
                    conclusion = ok ? "ACCEPTED" : "REJECTED"
                },
                AuditJson));
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
