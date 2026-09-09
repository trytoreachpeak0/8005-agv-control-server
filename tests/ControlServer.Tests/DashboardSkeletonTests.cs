using System.Text.Json;
using ControlServer.Dashboard;
using ControlServer.Domain;
using ControlServer.Host.Dashboard;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 看板骨架：2 秒节奏、失联直述、只有 fail-safe 方向、卡片自注册。
/// </summary>
public sealed class DashboardSkeletonTests
{
    private static readonly string[] StaleWords =
    [
        "已过期", "过期", "陈旧", "上次更新", "上一次更新", "最后更新",
        "stale", "expired", "lastUpdated", "LastUpdated", "last updated",
    ];

    private static readonly string[] FailUnsafeWords =
    [
        "恢复", "激活", "回滚", "投运",
        "restore", "activate", "rollback", "commission",
    ];

    [Fact]
    public void BothViewsShareOneTwoSecondCadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), DashboardRefresh.Interval);

        // 页面上只有一个刷新声明，两个视图在同一页里，所以它们不可能各刷各的。
        string page = DashboardPageRenderer.RenderPage(
            DashboardCardCatalog.Discovered, new Dictionary<string, DashboardCardData>());
        Assert.Single(Occurrences(page, "http-equiv=\"refresh\""));
        Assert.Contains("content=\"2\"", page, StringComparison.Ordinal);
        Assert.Contains("车队", page, StringComparison.Ordinal);
        Assert.Contains("仓位", page, StringComparison.Ordinal);
    }

    [Fact]
    public void AtLeastOneRealDataCardExistsInEachView()
    {
        DashboardCardCatalog catalog = DashboardCardCatalog.Discovered;
        Assert.NotEmpty(catalog.For(DashboardView.Fleet));
        Assert.NotEmpty(catalog.For(DashboardView.Slots));

        // 每张卡片的数据源都对得上服务端真实存在的一个只读端点。
        DashboardQueryEndpointCatalog endpoints =
            DashboardQueryEndpointCatalog.Discover(typeof(FleetSessionsQueryEndpoint).Assembly);
        string[] served = [.. endpoints.Endpoints.Select(endpoint => endpoint.Path)];
        foreach (IDashboardCard card in catalog.Cards)
        {
            Assert.Contains(card.SourcePath, served);
        }
    }

    [Fact]
    public void WhenTheDataSourceIsUnavailableTheCardSaysWhyAndShowsNoOldValue()
    {
        IDashboardCard card = DashboardCardCatalog.Discovered.Cards[0];
        string rendered = DashboardPageRenderer.RenderCard(
            card, DashboardCardData.Unavailable("设备连接失败：连接被拒绝。"));

        Assert.Contains("设备连接失败：连接被拒绝。", rendered, StringComparison.Ordinal);
        Assert.Contains("unavailable", rendered, StringComparison.Ordinal);
        // 失败分支里没有任何一个数字或字段来自上一轮——渲染它的那份数据根本没有那一项。
        Assert.Null(DashboardCardData.Unavailable("x").Fact);
        foreach (string word in StaleWords)
        {
            Assert.DoesNotContain(word, rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoStaleOrLastUpdatedPresentationExistsAnywhereInTheDashboard()
    {
        foreach (string file in DashboardSourceFiles())
        {
            string source = ExecutableLines(file);
            foreach (string word in StaleWords)
            {
                Assert.DoesNotContain(word, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void NoRestoreActivateRollbackOrCommissionEntryPointExistsOnTheDashboard()
    {
        // 无人员认证的前提下界面只允许 fail-safe 方向的动作。这条守卫盯的是「界面上有没有这些
        // 入口」，所以它同时扫动作词与任何可以提交的控件。
        foreach (string file in DashboardSourceFiles())
        {
            string source = ExecutableLines(file);
            Assert.DoesNotContain("<form", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<button", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MapPost", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MapPut", source, StringComparison.Ordinal);
            Assert.DoesNotContain("MapDelete", source, StringComparison.Ordinal);
            foreach (string word in FailUnsafeWords)
            {
                Assert.DoesNotContain(word, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TheDashboardDataContractIsDisjointFromTheProtocol()
    {
        // 看板整个只经 HTTP 读 ControlServer：它没有链接 Domain / Application / Infrastructure
        // 中的任何一个，所以协议的消息类型、schema 与错误码根本不在它的可达面里。
        string[] referenced = [.. typeof(IDashboardCard).Assembly.GetReferencedAssemblies()
            .Select(name => name.Name!)];
        Assert.DoesNotContain("ControlServer.Domain", referenced);
        Assert.DoesNotContain("ControlServer.Application", referenced);
        Assert.DoesNotContain("ControlServer.Infrastructure", referenced);
        Assert.DoesNotContain("ControlServer.Host", referenced);

        // 服务端这半：看板端点全部在 /api/dashboard/ 之下，与协议消息面无交集。
        DashboardQueryEndpointCatalog endpoints =
            DashboardQueryEndpointCatalog.Discover(typeof(FleetSessionsQueryEndpoint).Assembly);
        Assert.NotEmpty(endpoints.Endpoints);
        Assert.All(endpoints.Endpoints, endpoint =>
            Assert.StartsWith(DashboardPaths.QueryPrefix, endpoint.Path, StringComparison.Ordinal));
        Assert.Equal(DashboardPaths.QueryPrefix, DashboardQueryEndpointCatalog.QueryPrefix);
    }

    [Fact]
    public void ANewCardIsAddedByAddingAFileAndTheDashboardMainFileIsNotTouched()
    {
        // 这张示例卡片整个定义在本文件里。目录接住它、主渲染器渲染它，都没有碰
        // DashboardPageRenderer.cs 或 Program.cs 一个字节——这就是 #12 立的那条约定。
        DashboardCardCatalog catalog = new([new ExampleCard()]);
        using JsonDocument fact = JsonDocument.Parse("""{"value":"42"}""");

        string page = DashboardPageRenderer.RenderPage(
            catalog,
            new Dictionary<string, DashboardCardData>(StringComparer.Ordinal)
            {
                ["example"] = DashboardCardData.Available(fact.RootElement.Clone())
            });

        Assert.Contains("示例卡片", page, StringComparison.Ordinal);
        Assert.Contains("42", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTwoQueryEndpointsServeRealRowsAndTheCardsRenderThem()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (connection)
        {
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            await using ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.SessionRecoveries.Add(new SessionRecoveryRow
            {
                AgvId = "AGV-01",
                ProtocolCommit = "c",
                ManifestSha256 = "m",
                ProfileId = "WIRE_TO_GATE_MVP",
                ProtocolVersion = 3,
                Readiness = SessionReadiness.Ready,
                ReasonCode = "READY",
                UpdatedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero)
            });
            context.Set<ActiveSlotConfigurationRow>().Add(new ActiveSlotConfigurationRow
            {
                AgvId = "AGV-01",
                SlotModelVersionId = "model-1",
                ConfigurationVersion = 7,
                Fingerprint = "abc123",
                ActivatedAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
                ActivationId = "act-1",
                SnapshotId = "snap-1"
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Dictionary<string, DashboardCardData> data = new(StringComparer.Ordinal);
            DashboardQueryEndpointCatalog endpoints =
                DashboardQueryEndpointCatalog.Discover(typeof(FleetSessionsQueryEndpoint).Assembly);
            foreach (IDashboardCard card in DashboardCardCatalog.Discovered.Cards)
            {
                IDashboardQueryEndpoint endpoint =
                    endpoints.Endpoints.Single(candidate => candidate.Path == card.SourcePath);
                object rows = await endpoint.ReadAsync(context, TestContext.Current.CancellationToken);
                using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(rows));
                data[card.CardId] = DashboardCardData.Available(document.RootElement.Clone());
            }

            string page = DashboardPageRenderer.RenderPage(DashboardCardCatalog.Discovered, data);
            Assert.Contains("AGV-01", page, StringComparison.Ordinal);
            Assert.Contains("Ready", page, StringComparison.Ordinal);
            Assert.Contains("abc123", page, StringComparison.Ordinal);
            Assert.DoesNotContain("unavailable", page, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACardWhoseDataSourceIsOutsideTheReadOnlyQueryPrefixIsRefused()
    {
        InvalidOperationException failure =
            Assert.Throws<InvalidOperationException>(() => new DashboardCardCatalog([new OffContractCard()]));
        Assert.Contains(DashboardPaths.QueryPrefix, failure.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Occurrences(string haystack, string needle)
    {
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            yield return needle;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 去掉注释后的源码。守卫盯的是看板**渲染出来**的东西，不是解释某个词为什么被禁的那句注释。
    /// </summary>
    private static string ExecutableLines(string file) =>
        string.Join(
            Environment.NewLine,
            File.ReadAllLines(file)
                .Where(line =>
                {
                    string trimmed = line.TrimStart();
                    return !trimmed.StartsWith("//", StringComparison.Ordinal)
                        && !trimmed.StartsWith("/*", StringComparison.Ordinal)
                        && !trimmed.StartsWith('*');
                }));

    private static string[] DashboardSourceFiles()
    {
        string root = Path.Combine(FindRepositoryRoot(), "src", "ControlServer.Dashboard");
        return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }

    private sealed class ExampleCard : IDashboardCard
    {
        public string CardId => "example";

        public string Title => "示例卡片";

        public DashboardView View => DashboardView.Fleet;

        public string SourcePath => DashboardPaths.QueryPrefix + "example";

        public string RenderFact(JsonElement fact) => $"<p>{DashboardPageRenderer.Text(fact, "value")}</p>";
    }

    private sealed class OffContractCard : IDashboardCard
    {
        public string CardId => "off-contract";

        public string Title => "越界卡片";

        public DashboardView View => DashboardView.Fleet;

        public string SourcePath => "/api/onboard/v1/vehicle-safety";

        public string RenderFact(JsonElement fact) => "<p></p>";
    }
}
