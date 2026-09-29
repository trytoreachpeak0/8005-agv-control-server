using System.Reflection;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// 启动校验（control-server#388；规格 5.4）：投运车辆数大于 1、而本图启用的等待点不够每辆车各分一个时拒绝启动，报错写明车辆数、点数与要做什么；
/// 单车不校验。外加覆盖口径（二分图匹配）与判定函数的单元用例。
/// </summary>
public sealed class WaitingPointStartupCheckTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(2, "26,214,等待点1,true,", false)]
    [InlineData(2, "26,214,等待点1,true,|26,215,等待点2,true,", true)]
    [InlineData(1, "", true)]
    [InlineData(2, "26,214,等待点1,true,|26,215,等待点2,false,|26,216,等待点3,false,", false)]
    public async Task TheFourCasesOfTheTicket(int vehicles, string rows, bool starts)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        if (rows.Length > 0)
        {
            await harness.ImportAsync(Csv(rows.Split('|')));
        }

        Exception? refused = await Record.ExceptionAsync(() => EnsureAsync(harness, Runtime(vehicles)));

        if (starts)
        {
            Assert.Null(refused);
        }
        else
        {
            InvalidOperationException error = Assert.IsType<InvalidOperationException>(refused);
            Assert.StartsWith(WaitingPointStartupCheck.ReasonCode, error.Message, StringComparison.Ordinal);
            Assert.Contains($"has {vehicles} vehicles", error.Message, StringComparison.Ordinal);
            Assert.Contains("gives only 1 of them a waiting point of their own on Map 26 (1 enabled there)", error.Message, StringComparison.Ordinal);
            Assert.Contains($"Short by {vehicles - 1}; left without a point: ", error.Message, StringComparison.Ordinal);
            Assert.Contains(
                $"import-waiting-points --database \"{harness.DatabasePath}\" --input <waiting-points.csv> --catalog <stations-26.json> --map 26 --fleet \"VK-A;VK-B\"",
                error.Message, StringComparison.Ordinal);
            Assert.Contains("No database edit is needed", error.Message, StringComparison.Ordinal);
            Assert.Contains("A single-vehicle deployment (JourneyRuntime:Fleet empty or one vehicle) is not subject to this check.", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 被拒之后的出路不需要改库：「两个点都只对 VK-A 开放」被拒，报错点名 VK-B 没有点；照报错里的命令（数据库路径、图号、车队都已填好）
    /// 经 FieldOps 进程导入一张补齐的表，再启动就通过。
    /// </summary>
    [Fact]
    public async Task ARefusalNamesTheVehicleWithoutAPointAndItsOwnCommandBringsTheServerUp()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv("26,214,等待点1,true,VK-A", "26,215,等待点2,true,VK-A"));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EnsureAsync(harness, Runtime(2)));
        Assert.Contains("Short by 1; left without a point: VK-B.", refused.Message, StringComparison.Ordinal);

        const string marker = "ControlServer.FieldOps.exe import-waiting-points ";
        string command = refused.Message[(refused.Message.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        command = command[..command.IndexOf(" (add --dry-run", StringComparison.Ordinal)];
        string table = harness.WriteCsv("fixed.csv", Csv("26,214,等待点1,true,VK-A", "26,215,等待点2,true,VK-A;VK-B"));
        string[] arguments =
        [
            "import-waiting-points",
            .. SplitCommand(command
                .Replace("<waiting-points.csv>", table, StringComparison.Ordinal)
                .Replace("<stations-26.json>", harness.CatalogPath, StringComparison.Ordinal))
        ];
        (int exit, string output) = await RunFieldOpsAsync(arguments);

        Assert.True(exit == 0, output);
        Assert.Null(await Record.ExceptionAsync(() => EnsureAsync(harness, Runtime(2))));
    }

    /// <summary>
    /// <c>--migrate-only</c>：迁移建库之后立刻退出、退出码 0，从不监听、不建单——空环境（没有 RIoT 密钥、没有车）也行；
    /// 迁移失败时退出码非 0。它只给编排器与首次部署用：多车服务端在登记之前起不来，FieldOps 要一个已迁移的库才能导入。
    /// </summary>
    [Fact]
    public async Task MigrateOnlyMigratesAndExitsWithoutListeningAndAFailureExitsNonZero()
    {
        string directory = Path.Combine(Path.GetTempPath(), "w2g-migrate-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string database = Path.Combine(directory, "controlserver.db");
            int port = FreePort();
            (int exit, string log, TimeSpan took) = await RunHostMigrateOnlyAsync(database, port);

            Assert.True(exit == 0, log);
            Assert.DoesNotContain("Now listening", log, StringComparison.Ordinal);
            Assert.True(took < TimeSpan.FromSeconds(60), $"--migrate-only took {took}");
            await using (ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>()
                    .UseSqlite(ControlServerSqlite.ForDatabaseFile(database, readOnly: true)).Options))
            {
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(Token));
                Assert.Equal(0, await context.Set<WaitingPointVersionRow>().CountAsync(Token));
                Assert.Equal(0, await context.OrderIntents.CountAsync(Token));
            }

            string notAFile = Path.Combine(directory, "a-directory.db");
            Directory.CreateDirectory(notAFile);
            (int failed, string failedLog, _) = await RunHostMigrateOnlyAsync(notAFile, port);
            Assert.True(failed != 0, failedLog);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task NoRegistrationAtAllRefusesAMultiVehicleStartAndSaysNoneWasImported()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EnsureAsync(harness, Runtime(3)));

        Assert.Contains("registration (none imported) gives only 0", error.Message, StringComparison.Ordinal);
    }

    /// <summary>v2 出厂仍配 25 号图，等待点登记在 26 号图：多车部署拒绝启动，报错说出点在哪张图。</summary>
    [Fact]
    public async Task PointsRegisteredOnAnotherMapDoNotCountAndTheRefusalSaysWhereTheyAre()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv("26,214,等待点1,true,", "26,215,等待点2,true,", "26,216,等待点3,true,"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EnsureAsync(harness, Runtime(2, mapId: 25)));

        Assert.Contains("on Map 25 (0 enabled there)", error.Message, StringComparison.Ordinal);
        Assert.Contains("Map 26 x3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJourneyRuntimeThatIsOffIsNotChecked()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        JourneyRuntimeOptions off = Runtime(3);
        off.Enabled = false;

        Assert.Null(await Record.ExceptionAsync(() => EnsureAsync(harness, off)));
    }

    [Fact]
    public async Task APointThatIsAFixedStationOfThisMapDoesNotCount()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportAsync(Csv("26,214,等待点1,true,", "26,305,派工待送取货,true,"));
        await harness.BindStagingStationAsync();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EnsureAsync(harness, Runtime(2)));

        Assert.Contains("gives only 1 of them", error.Message, StringComparison.Ordinal);
        Assert.Contains("fixed station: 305", error.Message, StringComparison.Ordinal);
    }

    /// <summary>白名单口径：按匹配，不按总数，也不按每辆车各自可用的点数。</summary>
    [Theory]
    [InlineData("VK-A|VK-A", 1)] // two points, both only for A: B has nowhere to go
    [InlineData("VK-A|VK-B", 2)]
    [InlineData("|VK-A", 2)] // A takes its own point, B the open one
    [InlineData("VK-B|VK-A;VK-B", 2)]
    [InlineData("VK-Z|", 1)] // a whitelist naming no vehicle of this fleet serves nobody
    public void CoverageIsAMatchingOfVehiclesToDistinctPointsTheirWhitelistsAdmit(string scopes, int assignable)
    {
        WaitingPointEntry[] points =
        [
            .. scopes.Split('|').Select((scope, index) => new WaitingPointEntry(
                Map, 214 + index, $"等待点{index + 1}", true, scope.Length == 0 ? [] : scope.Split(';')))
        ];

        WaitingPointCoverage coverage = WaitingPointCoverageCalculator.Evaluate(points, Map, Fleet, excludedStations: null);

        Assert.Equal((2, assignable, assignable == 2), (coverage.EnabledOnMap, coverage.Assignable, coverage.Sufficient));
        Assert.Equal(2 - assignable, coverage.Shortfall);
        Assert.Equal(2 - assignable, coverage.Unassigned.Count);
        Assert.All(coverage.Unassigned, vehicle => Assert.Contains(vehicle, Fleet));
    }

    [Fact]
    public void ThePredicateRefusesAPointMissingFromTheLiveCatalogOrRenamedThere()
    {
        WaitingPointRegistrationVersion current = new(
            3, "sha", "snapshot", Imported, WaitingPointSources.GovernedImport,
            [new WaitingPointEntry(Map, 214, "等待点1", true, [])]);

        Assert.True(WaitingPointEligibility.Judge(current, Catalog(), Map, 214, VehicleA).Accepts);
        Assert.Equal(
            WaitingPointEligibilityReasons.NotInLiveCatalog,
            WaitingPointEligibility.Judge(
                current, TaskTypeStationCatalogEvidence.Supplied(Map, [new RiotMapStation(214, "等待点一")], Imported), Map, 214, VehicleA).Reason);
        Assert.Equal(WaitingPointEligibilityReasons.NotInLiveCatalog, WaitingPointEligibility.Judge(current, null, Map, 214, VehicleA).Reason);
        Assert.Equal(WaitingPointEligibilityReasons.NotRegistered, WaitingPointEligibility.Judge(null, Catalog(), Map, 214, VehicleA).Reason);
        Assert.Equal(3L, WaitingPointEligibility.Judge(current, Catalog(), Map, 215, VehicleA).Version);
    }

    private static int FreePort()
    {
        System.Net.Sockets.TcpListener listener = new(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<(int ExitCode, string Log, TimeSpan Took)> RunHostMigrateOnlyAsync(string database, int port)
    {
        string host = typeof(WaitingPointStartupCheckTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ControlServer.Host.Path").Value!;
        Assert.True(File.Exists(host), $"The host was not built: {host}");
        System.Diagnostics.ProcessStartInfo start = new(host, "--migrate-only")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(host)!
        };
        start.Environment["ConnectionStrings__ControlServer"] = $"Data Source={database}";
        // Were it to listen, it would be here; a free port keeps a stray listener from colliding with anything.
        start.Environment["Health__url"] = $"http://127.0.0.1:{port}";
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(Token);
        limit.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout + await stderr, clock.Elapsed);
    }

    // The refusal quotes paths and the fleet; everything else is one token.
    private static IEnumerable<string> SplitCommand(string command) =>
        System.Text.RegularExpressions.Regex.Matches(command, "\"([^\"]*)\"|([^ ]+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);

    private static async Task<(int ExitCode, string Output)> RunFieldOpsAsync(string[] arguments)
    {
        string fieldOps = typeof(WaitingPointStartupCheckTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "ControlServer.FieldOps.Path").Value!;
        System.Diagnostics.ProcessStartInfo start = new(fieldOps)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static JourneyRuntimeOptions Runtime(int vehicles, int mapId = Map) => new()
    {
        Enabled = true,
        MapId = mapId,
        Fleet = vehicles <= 1
            ? []
            : [.. Enumerable.Range(0, vehicles).Select(index => new FleetVehicleOptions
            {
                AgvId = $"AGV-{index}",
                VehicleKey = index < Fleet.Length ? Fleet[index] : $"VK-{index}"
            })]
    };

    private static async Task EnsureAsync(WaitingPointImportHarness harness, JourneyRuntimeOptions options)
    {
        await using ControlServerDbContext context = harness.Open();
        GovernedConfigurationPublisher publisher = Publisher(context);
        await WaitingPointStartupCheck.EnsureAsync(
            options,
            new WaitingPointRegistry(context, publisher),
            new TaskTypeStationBindingStore(context, publisher),
            NullLogger.Instance,
            harness.DatabasePath,
            Token);
    }
}
