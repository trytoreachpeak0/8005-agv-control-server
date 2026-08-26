using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;

namespace ControlServer.Tests;

public sealed class HttpMesIngestCatalogTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task UsesOnlyFrozenV2GetContractAndReturnsFinalCatalogFact()
    {
        Guid historyEpoch = new("11111111-1111-4111-8111-111111111111");
        RecordingHandler handler = new(historyEpoch);
        HttpClient client = new(handler) { BaseAddress = new Uri("http://mes-ingest.test") };
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        HttpMesIngestCatalog catalog = new(client, new FixedTimeProvider(now));

        AcceptedDemandSnapshot? current = await catalog.ReadCurrentAsync(
            "D-001", TestContext.Current.CancellationToken);

        Assert.NotNull(current);
        Assert.Equal("SUBLOT-001|WIRE_TO_GATE", current.TransportDemandKey);
        Assert.Equal(21, current.CatalogRevision);
        Assert.Equal(now, current.AcceptedAt);
        Assert.Equal("SERIES-001", current.SeriesId);
        Assert.Equal("AREA-01", current.LiveMesFields?.Area);
        Assert.Equal(
            [HttpMesIngestCatalog.ContractPath, HttpMesIngestCatalog.CatalogPath],
            handler.Paths);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    private sealed class RecordingHandler(Guid historyEpoch) : HttpMessageHandler
    {
        private static readonly string[] Capabilities =
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

        public List<string> Paths { get; } = [];
        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            Methods.Add(request.Method);
            if (request.RequestUri?.AbsolutePath == HttpMesIngestCatalog.ContractPath)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        contractVersion = HttpMesIngestCatalog.ContractVersion,
                        schemaVersion = HttpMesIngestCatalog.SchemaVersion,
                        capabilities = Capabilities.Select(id => new { id, version = "1.0" })
                    })
                });
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    contractVersion = HttpMesIngestCatalog.ContractVersion,
                    historyEpoch,
                    catalogRevision = 21,
                    count = 1,
                    items = new[]
                    {
                        new
                        {
                            demandId = "D-001",
                            seriesId = "SERIES-001",
                            transportDemandKey = new { workType = "WIRE_TO_GATE", sublot = "SUBLOT-001" },
                            generation = 1,
                            demandRevision = 7,
                            createdAt = new DateTimeOffset(2026, 8, 25, 8, 0, 0, TimeSpan.Zero),
                            valueObservedAt = new DateTimeOffset(2026, 8, 25, 8, 1, 0, TimeSpan.Zero),
                            valuePollTraceId = "TRACE-001",
                            valueProjectionCommitId = "COMMIT-001",
                            liveMesFields = new
                            {
                                area = "AREA-01",
                                eqp = "EQP-01",
                                step = "STEP-01",
                                mesSourceDate = new DateTimeOffset(2026, 8, 25, 7, 59, 0, TimeSpan.Zero),
                                package = "PKG-01"
                            }
                        }
                    }
                })
            };
            response.Headers.ETag = new EntityTagHeaderValue($"\"catalog-h{historyEpoch:N}-r21\"", isWeak: true);
            return Task.FromResult(response);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
