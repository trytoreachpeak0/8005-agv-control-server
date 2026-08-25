using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Infrastructure.Adapters;

public sealed class HttpMesIngestCatalog(HttpClient httpClient, TimeProvider timeProvider) : IMesIngestCatalog
{
    public const string ContractVersion = "2026.08.new-mes-ingest.v2.0";
    public const int SchemaVersion = 21;
    public const string ContractPath = "/api/v2/contract";
    public const string CatalogPath = "/api/v2/externally-readable-demand-catalog";

    private static readonly string[] RequiredCapabilities =
    [
        "CONTRACT_DISCOVERY",
        "CURRENT_INGEST_ATTENTION",
        "DEMAND_SERIES",
        "ERROR_SEARCH",
        "EXTERNALLY_READABLE_DEMAND_CATALOG",
        "POLL_HEALTH_AND_EVIDENCE",
        "READABILITY_AUDIT",
        "SERIES_ERROR_CATALOG",
        "WATCH_OVERVIEW"
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<AcceptedDemandSnapshot?> ReadCurrentAsync(
        string demandId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demandId);
        await RequireExactContractAsync(cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage request = new(HttpMethod.Get, CatalogPath);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        CatalogDto body = await response.Content.ReadFromJsonAsync<CatalogDto>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("MesIngest catalog returned an empty body.");
        ValidateCatalog(body, response.Headers.ETag?.Tag);

        CatalogItemDto? item = body.Items.SingleOrDefault(candidate =>
            string.Equals(candidate.DemandId, demandId, StringComparison.Ordinal));
        if (item is null)
        {
            return null;
        }
        TransportDemandKeyDto key = item.TransportDemandKey
            ?? throw new InvalidDataException("MesIngest demand is missing transportDemandKey.");
        string workType = RequireText(key.WorkType, "transportDemandKey.workType");
        string sublot = RequireText(key.Sublot, "transportDemandKey.sublot");
        return new AcceptedDemandSnapshot(
            RequireText(item.DemandId, "demandId"),
            $"{sublot}|{workType}",
            item.DemandRevision,
            RequireText(body.HistoryEpoch, "historyEpoch"),
            body.CatalogRevision,
            timeProvider.GetUtcNow());
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
        string[] actualIds = body.Capabilities
            .Select(capability => RequireText(capability.Id, "capabilities[].id"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedIds = RequiredCapabilities.Order(StringComparer.Ordinal).ToArray();
        bool versionsMatch = body.Capabilities.All(capability => capability.Version == "1.0");
        if (body.ContractVersion != ContractVersion || body.SchemaVersion != SchemaVersion ||
            !versionsMatch || !actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal))
        {
            throw new InvalidDataException("CONTRACT_VERSION_MISMATCH: MesIngest V2 identity or capability set differs.");
        }
    }

    private static void ValidateCatalog(CatalogDto body, string? etag)
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
        if (etag is null || !etag.Equals(expectedEtag, StringComparison.Ordinal))
        {
            throw new InvalidDataException("MesIngest catalog body identity does not match its ETag.");
        }
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
        TransportDemandKeyDto? TransportDemandKey,
        long DemandRevision);

    private sealed record TransportDemandKeyDto(string? WorkType, string? Sublot);
}
