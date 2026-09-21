using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;

namespace ControlServer.Tests;

public sealed class HttpMesIngestCatalogTests
{
    private const string ExpectedContractVersion = "2026.08.new-mes-ingest.v2.4";
    private const int ExpectedSchemaVersion = 29;

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

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task UsesOnlyFrozenV2GetContractAndReturnsFinalCatalogFact()
    {
        Guid historyEpoch = new("11111111-1111-4111-8111-111111111111");
        RecordingHandler handler = new(historyEpoch);
        HttpClient client = new(handler) { BaseAddress = new Uri("http://mes-ingest.test") };
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        HttpMesIngestCatalog catalog = new(client, new FixedTimeProvider(now));

        // The catalog reports the demand id unhyphenated, but every WIRE_TO_GATE payload and every
        // inbound demandId this server reads is a canonical UUID, so the id is normalised at this
        // boundary and callers address a demand by that one form.
        AcceptedDemandSnapshot? current = await catalog.ReadCurrentAsync(
            "94993971-b362-4edf-81bc-712d160e444a", TestContext.Current.CancellationToken);

        Assert.NotNull(current);
        Assert.Equal("94993971-b362-4edf-81bc-712d160e444a", current.DemandId);
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

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task RejectsContractWithoutTheExactSublotBoxCountCapabilityVersion()
    {
        Guid historyEpoch = new("11111111-1111-4111-8111-111111111111");
        (string Id, string Version)[] wrongCapabilities = Capabilities
            .Select(capability => capability.Id == "SUBLOT_BOX_COUNT"
                ? (capability.Id, "2.0")
                : capability)
            .ToArray();
        RecordingHandler handler = new(historyEpoch, wrongCapabilities);
        HttpClient client = new(handler) { BaseAddress = new Uri("http://mes-ingest.test") };
        HttpMesIngestCatalog catalog = new(client, TimeProvider.System);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.ReadCatalogAsync(TestContext.Current.CancellationToken));

        Assert.Contains("CONTRACT_VERSION_MISMATCH", error.Message, StringComparison.Ordinal);
        Assert.Equal([HttpMesIngestCatalog.ContractPath], handler.Paths);
    }

    /// <summary>
    /// 目录项缺 <c>createdAt</c>（批次7-09 审查低 3）：它现在决定派车次序与防饥饿告警，所以缺的时候不能悄悄变成
    /// 一个真实的时刻。适配器把它留成默认值（「MesIngest 没给」）并打一条 Warning；年龄按 0 算、不告警、不排到最前由
    /// <c>TaskStarvation</c> 与建单时刻那一层负责。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ACatalogItemWithoutCreatedAtIsKeptAsUnknownAndWarned()
    {
        Guid historyEpoch = new("11111111-1111-4111-8111-111111111111");
        RecordingHandler handler = new(historyEpoch, omitCreatedAt: true);
        HttpClient client = new(handler) { BaseAddress = new Uri("http://mes-ingest.test") };
        RecordingLogger<HttpMesIngestCatalog> log = new();
        HttpMesIngestCatalog catalog = new(client, TimeProvider.System, log);

        DemandCatalogSnapshot snapshot = await catalog.ReadCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(default, Assert.Single(snapshot.Items).CreatedAt);
        (LogLevel level, string message) = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("94993971-b362-4edf-81bc-712d160e444a", message, StringComparison.Ordinal);
        Assert.Contains("createdAt", message, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(
        Guid historyEpoch,
        IReadOnlyList<(string Id, string Version)>? capabilities = null,
        bool omitCreatedAt = false) : HttpMessageHandler
    {
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
                        contractVersion = ExpectedContractVersion,
                        schemaVersion = ExpectedSchemaVersion,
                        capabilities = (capabilities ?? Capabilities)
                            .Select(capability => new
                            {
                                id = capability.Id,
                                version = capability.Version
                            })
                    })
                });
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Shape(new
                {
                    contractVersion = ExpectedContractVersion,
                    historyEpoch,
                    catalogRevision = 21,
                    count = 1,
                    items = new[]
                    {
                        new
                        {
                            // MesIngest reports the id unhyphenated, the way the field catalog does.
                            demandId = "94993971b3624edf81bc712d160e444a",
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
                }))
            };
            response.Headers.ETag = new EntityTagHeaderValue($"\"catalog-h{historyEpoch:N}-r21\"", isWeak: true);
            return Task.FromResult(response);
        }

        private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

        private JsonNode Shape(object body)
        {
            JsonNode node = JsonSerializer.SerializeToNode(body, WebOptions)!;
            if (omitCreatedAt)
            {
                _ = node["items"]![0]!.AsObject().Remove("createdAt");
            }
            return node;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
