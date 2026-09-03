using System.Globalization;
using ControlServer.Infrastructure.Adapters;
using ControlServer.TestDoubles;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.FakeMesIngest;

/// <summary>
/// The three MesIngest V2 endpoints the WIRE_TO_GATE runtime reads: contract discovery, the
/// externally readable demand catalog, and SUBLOT_BOX_COUNT. Nothing else -- this double stands in
/// for the parts of MesIngest ControlServer depends on, not for MesIngest.
/// </summary>
public static class MesIngestDataPlane
{
    /// <summary>
    /// The capability set contract discovery must report. Taken from
    /// <see cref="HttpMesIngestCatalog"/>'s own frozen list by reference rather than retyped: the
    /// control server compares the two sets exactly, so a copy here would be a second contract to
    /// keep in step and would fail closed the day someone edited one of them.
    /// </summary>
    private static readonly (string Id, string Version)[] Capabilities =
    [
        ("CONTRACT_DISCOVERY", "2.0"),
        ("CURRENT_INGEST_ATTENTION", "2.1"),
        ("DEMAND_SERIES", "2.1"),
        ("ERROR_SEARCH", "2.2"),
        ("EXTERNALLY_READABLE_DEMAND_CATALOG", "2.1"),
        ("POLL_HEALTH_AND_EVIDENCE", "2.0"),
        ("READABILITY_AUDIT", "2.0"),
        ("SERIES_ERROR_CATALOG", "2.0"),
        ("SUBLOT_BOX_COUNT", "1.0"),
        ("WATCH_OVERVIEW", "2.0")
    ];

    public static void MapMesIngestDataPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeMesIngestState> engine =
            app.Services.GetRequiredService<CommandEngine<FakeMesIngestState>>();

        app.MapGet(HttpMesIngestCatalog.ContractPath, () =>
        {
            FakeMesIngestState state = engine.Snapshot().State;
            return Results.Json(new
            {
                contractVersion = state.BreakContract
                    ? HttpMesIngestCatalog.ContractVersion + ".unexpected"
                    : HttpMesIngestCatalog.ContractVersion,
                schemaVersion = HttpMesIngestCatalog.SchemaVersion,
                capabilities = Capabilities
                    .Select(capability => new { id = capability.Id, version = capability.Version })
                    .ToArray()
            });
        });

        app.MapGet(HttpMesIngestCatalog.CatalogPath, (HttpResponse response) =>
        {
            FakeMesIngestState state = engine.Snapshot().State;
            // The control server rejects a catalog whose demand ids are not unique and ordinally
            // sorted, so ordering here is part of the contract, not presentation.
            FakeDemand[] demands = state.Demands
                .OrderBy(demand => demand.DemandId, StringComparer.Ordinal)
                .ToArray();
            Guid historyEpoch = Guid.Parse(state.HistoryEpoch);
            // Weak ETag derived from the body, which is exactly what the control server recomputes
            // and compares. Getting this wrong is indistinguishable from a corrupted catalog.
            response.Headers.ETag = "W/\"catalog-h" + historyEpoch.ToString("N") + "-r" +
                state.CatalogRevision.ToString(CultureInfo.InvariantCulture) + "\"";
            return Results.Json(new
            {
                contractVersion = HttpMesIngestCatalog.ContractVersion,
                historyEpoch = state.HistoryEpoch,
                catalogRevision = state.CatalogRevision,
                count = demands.Length,
                items = demands.Select(demand => new
                {
                    demandId = demand.DemandId,
                    seriesId = demand.SeriesId,
                    transportDemandKey = new { workType = demand.WorkType, sublot = demand.Sublot },
                    generation = demand.Generation,
                    demandRevision = demand.DemandRevision,
                    createdAt = demand.CreatedAt,
                    valueObservedAt = demand.ValueObservedAt,
                    valuePollTraceId = demand.ValuePollTraceId,
                    valueProjectionCommitId = demand.ValueProjectionCommitId,
                    liveMesFields = new
                    {
                        area = demand.Area,
                        eqp = demand.Eqp,
                        step = demand.Step,
                        mesSourceDate = demand.MesSourceDate,
                        package = demand.Package
                    }
                }).ToArray()
            });
        });

        app.MapGet("/api/v2/sublot-box-count", ([FromQuery] string sublot) =>
        {
            FakeMesIngestState state = engine.Snapshot().State;
            FakeDemand? demand = state.Demands.FirstOrDefault(item =>
                string.Equals(item.Sublot, sublot, StringComparison.Ordinal));
            if (demand is null)
            {
                return Results.NotFound();
            }
            // observedAt is stamped now on purpose: the reader ages this one out against
            // MaximumEvidenceAge, unlike the session-scoped Onboard snapshots.
            return Results.Json(new
            {
                queryId = "SUBLOT_BOX_COUNT",
                sublot,
                maxBoxCount = demand.MaxBoxCount,
                observedAt = DateTimeOffset.UtcNow
            });
        });
    }
}
