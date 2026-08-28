using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Infrastructure.Adapters;

public sealed class HttpMesIngestCatalog(HttpClient httpClient, TimeProvider timeProvider) : IMesIngestCatalog
{
    public const string ContractVersion = "2026.08.new-mes-ingest.v2.4";
    public const int SchemaVersion = 29;
    public const string ContractPath = "/api/v2/contract";
    public const string CatalogPath = "/api/v2/externally-readable-demand-catalog";

    private static readonly CapabilityDto[] RequiredCapabilities =
    [
        new("CONTRACT_DISCOVERY", "2.0"),
        new("CURRENT_INGEST_ATTENTION", "2.1"),
        new("DEMAND_SERIES", "2.1"),
        new("ERROR_SEARCH", "2.2"),
        new("EXTERNALLY_READABLE_DEMAND_CATALOG", "2.1"),
        new("POLL_HEALTH_AND_EVIDENCE", "2.0"),
        new("READABILITY_AUDIT", "2.0"),
        new("SERIES_ERROR_CATALOG", "2.0"),
        new("SUBLOT_BOX_COUNT", "1.0"),
        new("WATCH_OVERVIEW", "2.0")
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<AcceptedDemandSnapshot?> ReadCurrentAsync(
        string demandId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        DemandCatalogSnapshot catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        return catalog.Items.SingleOrDefault(candidate =>
            string.Equals(candidate.DemandId, demandId, StringComparison.Ordinal));
    }

    public async Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        await RequireExactContractAsync(cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage request = new(HttpMethod.Get, CatalogPath);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        CatalogDto body = await response.Content.ReadFromJsonAsync<CatalogDto>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("MesIngest catalog returned an empty body.");
        ValidateCatalog(body, response.Headers.ETag);

        string historyEpoch = RequireText(body.HistoryEpoch, "historyEpoch");
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        AcceptedDemandSnapshot[] items = body.Items.Select(item => MapItem(
            item,
            historyEpoch,
            body.CatalogRevision,
            observedAt)).ToArray();
        return new DemandCatalogSnapshot(historyEpoch, body.CatalogRevision, items);
    }

    private async Task RequireExactContractAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, ContractPath);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ContractDto body = await response.Content.ReadFromJsonAsync<ContractDto>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("MesIngest contract discovery returned an empty body.");
        CapabilityDto[] actual = body.Capabilities
            .Select(capability => new CapabilityDto(
                RequireText(capability.Id, "capabilities[].id"),
                RequireText(capability.Version, "capabilities[].version")))
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .ToArray();
        CapabilityDto[] expected = RequiredCapabilities
            .OrderBy(capability => capability.Id, StringComparer.Ordinal)
            .ToArray();
        if (body.ContractVersion != ContractVersion || body.SchemaVersion != SchemaVersion ||
            !actual.SequenceEqual(expected))
        {
            throw new InvalidDataException("CONTRACT_VERSION_MISMATCH: MesIngest V2 identity or capability set differs.");
        }
    }

    private static void ValidateCatalog(CatalogDto body, System.Net.Http.Headers.EntityTagHeaderValue? etag)
    {
        if (body.ContractVersion != ContractVersion)
        {
            throw new InvalidDataException("MesIngest catalog contractVersion differs from the frozen V2 identity.");
        }
        if (!Guid.TryParse(body.HistoryEpoch, out Guid historyEpoch) || historyEpoch == Guid.Empty)
        {
            throw new InvalidDataException("MesIngest catalog historyEpoch is invalid.");
        }
        if (body.Count != body.Items.Count)
        {
            throw new InvalidDataException("MesIngest catalog count does not match items.");
        }
        string[] demandIds = body.Items.Select(item => RequireText(item.DemandId, "demandId")).ToArray();
        if (!demandIds.SequenceEqual(demandIds.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            demandIds.Distinct(StringComparer.Ordinal).Count() != demandIds.Length)
        {
            throw new InvalidDataException("MesIngest catalog demands must be unique and ordinally sorted.");
        }
        string expectedEtag = $"\"catalog-h{historyEpoch:N}-r{body.CatalogRevision}\"";
        if (etag is null || !etag.IsWeak || !etag.Tag.Equals(expectedEtag, StringComparison.Ordinal))
        {
            throw new InvalidDataException("MesIngest catalog body identity does not match its ETag.");
        }
    }

    private static AcceptedDemandSnapshot MapItem(
        CatalogItemDto item,
        string historyEpoch,
        long catalogRevision,
        DateTimeOffset acceptedAt)
    {
        TransportDemandKeyDto key = item.TransportDemandKey
            ?? throw new InvalidDataException("MesIngest demand is missing transportDemandKey.");
        LiveMesFieldSetDto fields = item.LiveMesFields
            ?? throw new InvalidDataException("MesIngest demand is missing liveMesFields.");
        string workType = RequireText(key.WorkType, "transportDemandKey.workType");
        string sublot = RequireText(key.Sublot, "transportDemandKey.sublot");
        return new AcceptedDemandSnapshot(
            RequireText(item.DemandId, "demandId"),
            $"{sublot}|{workType}",
            item.DemandRevision,
            historyEpoch,
            catalogRevision,
            acceptedAt,
            RequireText(item.SeriesId, "seriesId"),
            workType,
            sublot,
            item.Generation,
            item.CreatedAt,
            item.ValueObservedAt,
            RequireText(item.ValuePollTraceId, "valuePollTraceId"),
            RequireText(item.ValueProjectionCommitId, "valueProjectionCommitId"),
            new LiveMesFieldSet(fields.Area, fields.Eqp, fields.Step, fields.MesSourceDate, fields.Package));
    }

    private static string RequireText(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"MesIngest field '{fieldName}' is required.")
            : value;

    private sealed record ContractDto(
        string? ContractVersion,
        int SchemaVersion,
        IReadOnlyList<CapabilityDto> Capabilities);

    private sealed record CapabilityDto(string? Id, string? Version);

    private sealed record CatalogDto(
        string? ContractVersion,
        string? HistoryEpoch,
        long CatalogRevision,
        int Count,
        IReadOnlyList<CatalogItemDto> Items);

    private sealed record CatalogItemDto(
        string? DemandId,
        string? SeriesId,
        TransportDemandKeyDto? TransportDemandKey,
        int Generation,
        long DemandRevision,
        DateTimeOffset CreatedAt,
        DateTimeOffset ValueObservedAt,
        string? ValuePollTraceId,
        string? ValueProjectionCommitId,
        LiveMesFieldSetDto? LiveMesFields);

    private sealed record TransportDemandKeyDto(string? WorkType, string? Sublot);

    private sealed record LiveMesFieldSetDto(
        string? Area,
        string? Eqp,
        string? Step,
        DateTimeOffset? MesSourceDate,
        string? Package);
}
