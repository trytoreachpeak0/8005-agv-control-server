using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// control-server#661：路网的 6 个 RIoT 读取经本仓真实的 SDK 注册（<c>AddRiotSdkIntegration</c>）与真实的
/// <see cref="HttpRouteGraphSource"/>，钉住换到 <c>RIoT.Sdk.Facade 0.2.0-fp.6</c> 要买的行为。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么另立一个类。</b><see cref="RouteGraphRefreshTimeoutTests"/> 用替身源直接抛 <see cref="TaskCanceledException"/>，
/// FakeRiot 的 <c>NoResponse</c> 在响应头之前就挂住——两者都碰不到「响应头到了、响应体停住」这一种。fp.5 的门面在那种情况下
/// 自己读原始流，<c>HttpClient.Timeout</c> 只管到响应头，读取一直挂着，刷新挂在门内，全车队的引擎轮次跟着停住（riot-sdk#4，
/// #663 审查 S1）。钉包测试只挡降版本，挡不住以后某一版把响应体读取又改回不受超时约束；这里的用例会红。
/// </para>
/// <para>
/// 替身 handler 记录每个请求是否带了 Bearer：没配凭据时 SDK 会先抛「No bearer credential」，那也是
/// <see cref="InvalidOperationException"/>，会让 Content-Type 那条用例假绿（审查探针第一轮踩过）。
/// </para>
/// </remarks>
public sealed class RiotRouteGraphTransportTests
{
    private const int MapId = 26;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string EmptyEnvelope = """{"code":"0","message":"ok","result":[]}""";

    [Fact]
    public async Task AReadWhoseBodyStallsAfterTheHeadersTimesOutWithinTheConfiguredTimeout()
    {
        using StallingHandler handler = StallingHandler.StallBody();
        using Harness harness = Harness.Create(handler);
        using IServiceScope scope = harness.Provider.CreateScope();
        HttpRouteGraphSource source = new(scope.ServiceProvider.GetRequiredService<RiotSession>());

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => source.ReadEdgesAsync(MapId, Token).WaitAsync(Guard, Token));

        Assert.False(Token.IsCancellationRequested);
        Assert.True(handler.EveryRequestCarriedBearer);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ARefreshWhoseRiotBodyStallsReturnsAndMarksTheSnapshotRefreshFailed()
    {
        using StallingHandler handler = StallingHandler.StallBody();
        using Harness harness = Harness.Create(handler);
        await using GraphStore graph = await GraphStore.CreateAsync();
        using IServiceScope scope = harness.Provider.CreateScope();
        RouteGraphRefresher refresher = graph.Refresher(
            new HttpRouteGraphSource(scope.ServiceProvider.GetRequiredService<RiotSession>()));

        await refresher.RefreshOnceAsync(Token).WaitAsync(Guard, Token);

        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, (await graph.Store.ReadHeaderAsync(MapId, Token))?.StaleReason);
        Assert.True(handler.EveryRequestCarriedBearer);
    }

    [Fact]
    public async Task AReadAnsweredAsTextPlainFailsAndTheRefreshMarksTheSnapshotRefreshFailed()
    {
        // A well-formed success envelope, only labelled text/plain: before fp.6 the Facade parsed it regardless.
        using StallingHandler handler = StallingHandler.Answer(EmptyEnvelope, "text/plain");
        using Harness harness = Harness.Create(handler);
        await using GraphStore graph = await GraphStore.CreateAsync();
        using IServiceScope scope = harness.Provider.CreateScope();
        HttpRouteGraphSource source = new(scope.ServiceProvider.GetRequiredService<RiotSession>());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ReadEdgesAsync(MapId, Token).WaitAsync(Guard, Token));

        await graph.Refresher(source).RefreshOnceAsync(Token).WaitAsync(Guard, Token);

        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, (await graph.Store.ReadHeaderAsync(MapId, Token))?.StaleReason);
        Assert.True(handler.EveryRequestCarriedBearer);
    }

    /// <summary>The control: the same harness answering application/json reads cleanly, so the two failures above are the transport's.</summary>
    [Fact]
    public async Task TheSameHarnessAnsweringApplicationJsonReadsCleanly()
    {
        using StallingHandler handler = StallingHandler.Answer(EmptyEnvelope, "application/json");
        using Harness harness = Harness.Create(handler);
        using IServiceScope scope = harness.Provider.CreateScope();
        HttpRouteGraphSource source = new(scope.ServiceProvider.GetRequiredService<RiotSession>());

        Assert.Empty(await source.ReadEdgesAsync(MapId, Token).WaitAsync(Guard, Token));
        Assert.False(await source.ReadDynamicRouteCostPresentAsync(Token).WaitAsync(Guard, Token));
        Assert.True(handler.EveryRequestCarriedBearer);
    }

    /// <summary>The Host's own RIoT registration with a one-second <c>RIoT:timeoutSeconds</c> and a credential in the environment.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly string _credentialVariable;

        private Harness(ServiceProvider provider, string credentialVariable)
        {
            Provider = provider;
            _credentialVariable = credentialVariable;
        }

        public ServiceProvider Provider { get; }

        public static Harness Create(StallingHandler handler)
        {
            string variable = "CONTROL_SERVER_TEST_RIOT_SDK_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variable, "test-call-api-key");
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RIoT:baseUrl"] = "http://riot.test",
                    ["RIoT:timeoutSeconds"] = "1",
                    ["RIoT:callApiKeyEnvironmentVariable"] = variable,
                })
                .Build();
            ServiceCollection services = new();
            services.AddSingleton(configuration);
            services.AddSingleton(TimeProvider.System);
            services.AddRiotSdkIntegration(configuration, () => handler);
            return new Harness(services.BuildServiceProvider(validateScopes: true), variable);
        }

        public void Dispose()
        {
            Provider.Dispose();
            Environment.SetEnvironmentVariable(_credentialVariable, null);
        }
    }

    /// <summary>The route-graph store over one in-memory database.</summary>
    private sealed class GraphStore : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;

        private GraphStore(SqliteConnection connection, ControlServerDbContext context)
        {
            _connection = connection;
            _context = context;
            Store = new RouteGraphSnapshotStore(context);
        }

        public RouteGraphSnapshotStore Store { get; }

        public static async Task<GraphStore> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
            return new GraphStore(connection, context);
        }

        public RouteGraphRefresher Refresher(IRouteGraphSource source) => new(
            source,
            Store,
            Options.Create(new RouteGraphOptions
            {
                Enabled = true,
                MapId = MapId,
                DesignStateTtl = TimeSpan.FromMinutes(10),
                RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
                RuntimeStateMaxAge = TimeSpan.FromSeconds(45),
            }),
            TimeProvider.System,
            NullLogger<RouteGraphRefresher>.Instance);

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Answers every request with 200 and either a body that never arrives after the headers, or a fixed body under a
    /// given Content-Type. Disposing it releases any stalled body, so a read that never timed out does not outlive the test.
    /// </summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        private readonly string? _body;
        private readonly string _mediaType;
        private readonly CancellationTokenSource _release = new();
        private int _requests;
        private int _withoutBearer;
        private int _disposed;

        private StallingHandler(string? body, string mediaType)
        {
            _body = body;
            _mediaType = mediaType;
        }

        public static StallingHandler StallBody() => new(null, "application/json");

        public static StallingHandler Answer(string body, string mediaType) => new(body, mediaType);

        public int RequestCount => Volatile.Read(ref _requests);

        public bool EveryRequestCarriedBearer => RequestCount > 0 && Volatile.Read(ref _withoutBearer) == 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { Length: > 0 } })
            {
                Interlocked.Increment(ref _withoutBearer);
            }

            HttpContent content = _body is null
                ? new StreamContent(new StalledStream(_release.Token))
                : new StringContent(_body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue(_mediaType) { CharSet = "UTF-8" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }

        protected override void Dispose(bool disposing)
        {
            // The client factory may dispose the primary handler too; the test's own using must not throw on a second pass.
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _release.Cancel();
                _release.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A response body whose first read waits until the reader cancels or the test releases it.</summary>
    private sealed class StalledStream(CancellationToken release) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(release, cancellationToken);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
