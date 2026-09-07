using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.CreateGate;

/// <summary>
/// The two values REQ-0302 requires to be approved before commissioning, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>Enabled</c> flag here on purpose.</b> The route-graph engine has one because a
/// deployment may legitimately not have adopted it yet; this gate may not be opted out of.
/// Specification 8.6 lists REQ-0302 as one of three hard blocks: absent approved values,
/// Map/Station-dependent business must not start. A switch that turned the check off would be the
/// exact configuration the requirement forbids, dressed as a feature.
/// </para>
/// <para>
/// Absence is therefore expressed by leaving both values unset, and it is not a startup failure:
/// REQ-0303 says the system still starts, still shows its state, still diagnoses and retries the
/// sync — it just does not resolve stations or create orders. What <em>is</em> a startup failure is
/// a configuration that is present and wrong, which is a different thing from one that is absent.
/// </para>
/// </remarks>
public sealed class MapStationCatalogOptions
{
    public const string SectionName = "MapStationCatalog";

    /// <summary>How often the Map/Station catalog is expected to be re-confirmed.</summary>
    public TimeSpan? ApprovedSyncPeriod { get; set; }

    /// <summary>
    /// How long the catalog may stand without a complete confirmation before it is unusable.
    /// </summary>
    /// <remarks>
    /// Must exceed the sync period. Equal would mean the catalog is stale the instant one cycle
    /// is missed, which turns every hiccup into a production outage while the configuration looks
    /// perfectly approved.
    /// </remarks>
    public TimeSpan? ApprovedMaxUnconfirmed { get; set; }

    /// <summary>Whether both approved values are present. Neither one alone is an approval.</summary>
    public bool IsApproved => ApprovedSyncPeriod is not null && ApprovedMaxUnconfirmed is not null;
}

/// <summary>
/// Refuses a catalog configuration that is present and self-contradictory.
/// </summary>
/// <remarks>
/// It deliberately accepts the empty configuration. "Not approved yet" is a state the product has
/// to be able to boot into and report — that is REQ-0303 — and the hard block that follows from it
/// lives in the admission chain, where it can name itself per demand instead of being a startup
/// stack trace nobody reads.
/// </remarks>
public sealed class MapStationCatalogOptionsValidator : IValidateOptions<MapStationCatalogOptions>
{
    public ValidateOptionsResult Validate(string? name, MapStationCatalogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = name;

        if (options.ApprovedSyncPeriod is null && options.ApprovedMaxUnconfirmed is null)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        if (options.ApprovedSyncPeriod is null || options.ApprovedMaxUnconfirmed is null)
        {
            // The two are approved separately, so exactly one of them being configured is the
            // shape of an approval that is half done — not of one that was never sought.
            failures.Add(
                "MapStationCatalog:ApprovedSyncPeriod and MapStationCatalog:ApprovedMaxUnconfirmed " +
                "must be approved together; configuring one without the other is not an approval.");
            return ValidateOptionsResult.Fail(failures);
        }

        if (options.ApprovedSyncPeriod <= TimeSpan.Zero)
        {
            failures.Add("MapStationCatalog:ApprovedSyncPeriod must be positive.");
        }

        if (options.ApprovedMaxUnconfirmed <= options.ApprovedSyncPeriod)
        {
            failures.Add(
                $"MapStationCatalog:ApprovedMaxUnconfirmed ({options.ApprovedMaxUnconfirmed}) must " +
                $"exceed MapStationCatalog:ApprovedSyncPeriod ({options.ApprovedSyncPeriod}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
