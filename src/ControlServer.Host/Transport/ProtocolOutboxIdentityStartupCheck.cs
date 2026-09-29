using System.Globalization;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Host.Transport;

/// <summary>
/// Refuses to start when the protocol outbox still holds a line this build would replay under another protocol identity
/// (control-server#382, review M1).
/// </summary>
/// <remarks>
/// <para>
/// Why: an unacknowledged, unfenced line is replayed into every new session, and the replay
/// (<see cref="OnboardJourneyPublisher.ReplayPendingForSessionAsync"/>) rewrites only <c>sessionGeneration</c> and
/// <c>sentAt</c> -- the envelope's identity stays as it was written. A journey in flight also re-publishes its snapshots
/// under deterministic messageIds every round, and <c>WireToGateStore.RefreshOutboundEnvelopeAsync</c> compares the
/// identity field by field and throws <c>ProtocolContentConflictException</c>; the vehicle, which checks the identity,
/// rejects the replayed line with <c>PROTOCOL_RELEASE_IDENTITY_MISMATCH</c> and the session drops, on every reconnect.
/// Nothing but a database edit or an empty database gets out of that. Upgrading (2.0.0 to the 3.0.0 candidate) and
/// rolling back are symmetric, so the check asks only whether a line is this build's identity, not which one it is.
/// </para>
/// <para>
/// Acknowledged or fenced lines are never replayed and are left alone. The identity compared is the envelope's own four
/// fields -- <c>protocolVersion</c>, <c>profileId</c>, <c>protocolReleaseVersion</c> and
/// <c>protocolReleaseManifestSha256</c> -- the same four <c>OnboardMessageProcessor.ValidateEnvelopeIdentity</c> checks on the
/// way in. A line that cannot be read at all counts as a mismatch: it could not be replayed either.
/// </para>
/// </remarks>
public static class ProtocolOutboxIdentityStartupCheck
{
    public const string ReasonCode = "OUTBOX_PROTOCOL_IDENTITY_MISMATCH";

    private static readonly Action<ILogger, string, string, Exception?> Refused =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(9411, "OutboxProtocolIdentityStartupRefused"),
            "Protocol outbox refused: {ReasonCode} {Detail}");

    public static async Task EnsureAsync(
        ControlServerDbContext dbContext, ILogger logger, string? databasePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(logger);

        string[] pending = await dbContext.ProtocolOutbox.AsNoTracking()
            .Where(row => row.AcknowledgedAt == null && row.FencedAt == null)
            .Select(row => row.PayloadJson)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string current = Describe(
            ProtocolCandidateIdentity.ProfileId,
            ProtocolCandidateIdentity.ProtocolVersion.ToString(CultureInfo.InvariantCulture),
            ProtocolCandidateIdentity.ReleaseVersion,
            ProtocolCandidateIdentity.ManifestSha256);
        var foreign = pending
            .Select(Read)
            .Where(line => line.Identity != current)
            .GroupBy(line => (line.AgvId, line.Identity))
            .OrderBy(group => group.Key.AgvId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Identity, StringComparer.Ordinal)
            .Select(group => FormattableString.Invariant($"{group.Key.AgvId}: {group.Count()} line(s) under {group.Key.Identity}"))
            .ToArray();
        if (foreign.Length == 0)
        {
            return;
        }

        string detail = "The protocol outbox holds unacknowledged lines written under another protocol identity; this build "
            + "would replay them unchanged into every session, the vehicle would reject them and drop the session on every "
            + "reconnect. " + string.Join("; ", foreign) + ". This build speaks " + current + ". "
            + "Either start the build that wrote them, let every vehicle finish or clear its journeys and acknowledge "
            + "its outbox, then upgrade; or start this build on a new database directory"
            + (databasePath is null ? string.Empty : " instead of " + databasePath)
            + ". Do not edit the outbox by hand.";
        Refused(logger, ReasonCode, detail, null);
        throw new InvalidOperationException($"{ReasonCode}: {detail}");
    }

    /// <summary>The host's entry point: resolves the database and a logger from a fresh scope.</summary>
    public static async Task EnsureAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;
        ControlServerDbContext dbContext = provider.GetRequiredService<ControlServerDbContext>();
        string dataSource = dbContext.Database.GetDbConnection().DataSource;
        await EnsureAsync(
                dbContext,
                provider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ProtocolOutboxIdentityStartupCheck).FullName!)
                    ?? NullLogger.Instance,
                string.IsNullOrWhiteSpace(dataSource) ? null : Path.GetFullPath(dataSource),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static (string AgvId, string Identity) Read(string envelope)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(envelope);
            JsonElement root = document.RootElement;
            return (
                Text(root, "agvId") ?? "(no agvId)",
                Describe(
                    Text(root, "profileId"),
                    root.TryGetProperty("protocolVersion", out JsonElement version) ? version.GetRawText() : null,
                    Text(root, "protocolReleaseVersion"),
                    Text(root, "protocolReleaseManifestSha256")));
        }
        catch (JsonException)
        {
            return ("(unreadable line)", "an unreadable envelope");
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Describe(string? profileId, string? protocolVersion, string? releaseVersion, string? manifest) =>
        $"{profileId ?? "?"} protocolVersion {protocolVersion ?? "?"} release {releaseVersion ?? "?"} (manifest {manifest ?? "?"})";
}
