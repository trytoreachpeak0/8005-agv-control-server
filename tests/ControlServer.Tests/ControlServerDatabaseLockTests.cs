using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 库级互斥（control-server#473）：服务端运行期间持有一把与它的库文件绑定的 OS 级独占锁，FieldOps 直接写库前拿同一把锁。探测只能说明
/// 「探的那一刻服务端停着」，探完、写库前服务端恰好启动的那个时间窗，只有锁关得上。这里起的是真的 Host 进程与真的 FieldOps 进程。
/// </summary>
public sealed class ControlServerDatabaseLockTests : IAsyncDisposable
{
    private const string KeyA = "KEY-A";

    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;

    private static readonly string FieldOps = BuiltPath("ControlServer.FieldOps.Path");

    private static readonly string HostExecutable = BuiltPath("ControlServer.Host.Path");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "w2g-database-lock-" + Guid.NewGuid().ToString("N"));

    private readonly List<Process> _hosts = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 票面第 4 条：FieldOps 探测时服务端确实停着（连接被拒），探完、写库前服务端起来了。FieldOps 必须拒绝写库——一行不写、不写审计。
    /// 暂停点是 FieldOps 的测试接缝，时序因此是确定的，不靠赛跑。
    /// </summary>
    [Fact]
    public async Task AServerThatStartsBetweenTheProbeAndTheWriteMakesFieldOpsRefuse()
    {
        string database = await SeedAsync("a");
        int healthPort = FreePort();
        string probe = $"http://127.0.0.1:{healthPort}/";
        string pause = Path.Combine(_root, "pause");

        Task<(int ExitCode, JsonElement Output)> fieldOps = RunFieldOpsAsync(
            [(ControlServerFieldOpsPause, pause)], [.. ReleaseArguments(), "--database", database, "--probe-server", probe]);
        await WaitForFileAsync(pause + ".reached", fieldOps);
        await StartHostAsync(database, healthPort);
        await File.WriteAllTextAsync(pause + ".go", string.Empty, Token);
        (int exit, JsonElement output) = await fieldOps;

        Assert.Equal((1, "DATABASE_IN_USE"), (exit, Outcome(output)));
        Assert.Contains(Path.GetDirectoryName(Path.GetFullPath(database))!, output.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        await AssertNothingWrittenAsync(database);
    }

    /// <summary>
    /// 票面第 2 条：锁绑的是库，不是机器。一个服务端在库目录 a 上跑着；FieldOps 对库目录 b 直接写库照常放行，另一个服务端在 b 上照常起来。
    /// </summary>
    [Fact]
    public async Task ADifferentDatabaseDirectoryIsNotBlocked()
    {
        string a = await SeedAsync("a");
        string b = await SeedAsync("b");
        await StartHostAsync(a, FreePort());

        (int exit, JsonElement output) = await RunFieldOpsAsync(
            [], [.. ReleaseArguments(), "--database", b, "--probe-server", $"http://127.0.0.1:{FreePort()}/"]);
        await StartHostAsync(b, FreePort());

        Assert.Equal((0, "OK"), (exit, Outcome(output)));
        await AssertReleasedAsync(b);
        await AssertNothingWrittenAsync(a);
    }

    /// <summary>
    /// 票面第 3 条：持有锁的服务端进程被杀（不是正常退出），锁随之释放，不留一把要手工删文件才能解开的锁。锁文件还在，照样拿得到。
    /// </summary>
    [Fact]
    public async Task KillingTheHolderReleasesTheLock()
    {
        string database = await SeedAsync("a");
        Process host = await StartHostAsync(database, FreePort());
        using (ControlServerDatabaseLock? whileRunning = ControlServerDatabaseLock.TryAcquire(database))
        {
            Assert.Null(whileRunning);
        }

        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync(Token);

        Assert.True(File.Exists(ControlServerDatabaseLock.LockFileFor(database)));
        using (ControlServerDatabaseLock? afterKill = ControlServerDatabaseLock.TryAcquire(database))
        {
            Assert.NotNull(afterKill);
        }
        (int exit, JsonElement output) = await RunFieldOpsAsync(
            [], [.. ReleaseArguments(), "--database", database, "--probe-server", $"http://127.0.0.1:{FreePort()}/"]);
        Assert.Equal((0, "OK"), (exit, Outcome(output)));
        await AssertReleasedAsync(database);
    }

    /// <summary>
    /// 票面第 5 条：同一个库上已经有一个服务端在跑，第二个（换了端口）等过 <see cref="DatabaseLockStartup.Wait"/> 仍拿不到锁，拒绝启动、
    /// 退出码非 0，从不监听；输出里有理由码和被锁的库目录。第一个照常应答。
    /// </summary>
    [Fact]
    public async Task ASecondServerOnTheSameDatabaseRefusesToStart()
    {
        string database = await SeedAsync("a");
        int firstPort = FreePort();
        await StartHostAsync(database, firstPort);
        int secondPort = FreePort();

        (int exit, string log, TimeSpan took) = await RunToExitAsync(HostStart(database, secondPort), TimeSpan.FromSeconds(90));

        Assert.True(exit != 0, log);
        Assert.Contains(DatabaseLockStartup.ReasonCode, log, StringComparison.Ordinal);
        Assert.Contains(Path.GetDirectoryName(Path.GetFullPath(database))!, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Now listening", log, StringComparison.Ordinal);
        Assert.True(took >= DatabaseLockStartup.Wait, $"It refused after {took}, before waiting {DatabaseLockStartup.Wait}.");
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });
        Assert.True((await client.GetAsync($"http://127.0.0.1:{firstPort}/health/live", Token)).IsSuccessStatusCode);
    }

    /// <summary>
    /// 重启不能因为锁变脆：上一个持有者（服务管理器报「已停止」后还在收尾的进程、一次 FieldOps 写库）几秒后放手，新起的服务端等到它，
    /// 照常起来，而不是拒绝启动。
    /// </summary>
    [Fact]
    public async Task AServerStartWaitsOutAHolderThatLetsGoWithinTheWait()
    {
        string database = await SeedAsync("a");
        ControlServerDatabaseLock briefly = ControlServerDatabaseLock.TryAcquire(database)!;
        Assert.NotNull(briefly);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            briefly.Dispose();
        }, Token);

        await StartHostAsync(database, FreePort());

        using ControlServerDatabaseLock? whileRunning = ControlServerDatabaseLock.TryAcquire(database);
        Assert.Null(whileRunning);
    }

    /// <summary>
    /// 包容量导入（<c>--import-package-capacity</c>）按设计在服务端运行时对同一个库执行（control-server#87），它不拿锁，照常导入。
    /// </summary>
    [Fact]
    public async Task ThePackageCapacityImportStillRunsBesideALiveServer()
    {
        string database = await SeedAsync("a");
        await StartHostAsync(database, FreePort());
        string csv = Path.Combine(_root, "package-capacity.csv");
        await File.WriteAllLinesAsync(
            csv,
            ["pattern,match_type,max_boxes_per_basket,source,status,note", "PKG-473,exact,4,cs473-test,active,beside a live server"],
            Token);
        ProcessStartInfo import = HostStart(database, FreePort());
        foreach (string argument in new[] { "--import-package-capacity", "--input", csv, "--version", "1" })
        {
            import.ArgumentList.Add(argument);
        }

        (int exit, string log, _) = await RunToExitAsync(import, TimeSpan.FromSeconds(120));

        Assert.True(exit == 0, log);
        Assert.Contains("Package capacity import complete", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 锁的身份是文件本身，不是路径字符串：同一个库换一种写法（多一段 <c>.</c>、大小写不同）照样被挡；同一进程里第二次也拿不到；
    /// 旁边另一个库不受影响。
    /// </summary>
    [Fact]
    public void TheLockIsTheFileNotItsSpelling()
    {
        string directory = Path.Combine(_root, "spelling");
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "controlserver.db");

        using ControlServerDatabaseLock? first = ControlServerDatabaseLock.TryAcquire(database);
        using ControlServerDatabaseLock? again = ControlServerDatabaseLock.TryAcquire(database);
        using ControlServerDatabaseLock? dotted = ControlServerDatabaseLock.TryAcquire(Path.Combine(directory, ".", "controlserver.db"));
        using ControlServerDatabaseLock? upper = ControlServerDatabaseLock.TryAcquire(Path.Combine(directory, "CONTROLSERVER.DB"));
        using ControlServerDatabaseLock? neighbour = ControlServerDatabaseLock.TryAcquire(Path.Combine(directory, "other.db"));

        Assert.NotNull(first);
        Assert.Null(again);
        Assert.Null(dotted);
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(upper);
        }
        Assert.NotNull(neighbour);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Process host in _hosts)
        {
            await StopAsync(host);
            host.Dispose();
        }
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>The FieldOps test seam's variable; see <c>Program.PauseAfterProbeVariable</c> in ControlServer.FieldOps.</summary>
    private const string ControlServerFieldOpsPause = "CONTROL_SERVER_FIELDOPS_TEST_PAUSE_AFTER_PROBE";

    private static string BuiltPath(string key)
    {
        string path = typeof(ControlServerDatabaseLockTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == key).Value!;
        return path;
    }

    private static string[] ReleaseArguments() =>
    [
        "release-station-exclusivity", "--map", "25", "--station", "202", "--vehicle-key", KeyA, "--operator", "OP-7",
        "--reason", "A 车离线，已拖离关卡", "--site-verification", "SITE-2026-1006-01"
    ];

    private static string? Outcome(JsonElement output) =>
        output.ValueKind == JsonValueKind.Object ? output.GetProperty("outcome").GetString() : null;

    /// <summary>A migrated database in its own directory under this test's root, with station 202 held by vehicle A.</summary>
    private async Task<string> SeedAsync(string directoryName)
    {
        string directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "controlserver.db");
        await using ControlServerDbContext context = Open(database);
        await context.Database.MigrateAsync(Token);
        await new StationExclusivityStore(context).TryAcquireAsync(
            new StationExclusivityRequest(25, 202, StationExclusivityKinds.FixedTaskStation, StationExclusivityStates.Occupied, null),
            KeyA, "journey:a", At, Token);
        return database;
    }

    private static ControlServerDbContext Open(string database) => new(
        new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(ControlServerSqlite.ForDatabaseFile(database, readOnly: false))
            .Options);

    private static async Task AssertNothingWrittenAsync(string database)
    {
        await using ControlServerDbContext read = Open(database);
        Assert.Single(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
        Assert.Equal(
            0, await read.Set<AdministratorAuditRecordRow>().CountAsync(row => row.Action == StationExclusivityManualRelease.AuditAction, Token));
    }

    private static async Task AssertReleasedAsync(string database)
    {
        await using ControlServerDbContext read = Open(database);
        Assert.Empty(await read.Set<StationExclusivityRow>().ToArrayAsync(Token));
    }

    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForFileAsync(string path, Task stillRunning)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.False(stillRunning.IsCompleted, $"FieldOps finished before it reached the pause ({path}).");
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"FieldOps did not reach the pause within 60 s ({path}).");
            await Task.Delay(50, Token);
        }
    }

    private static ProcessStartInfo HostStart(string database, int healthPort)
    {
        Assert.True(File.Exists(HostExecutable), $"The host was not built: {HostExecutable}");
        ProcessStartInfo start = new(HostExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(HostExecutable)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["ConnectionStrings__ControlServer"] = $"Data Source={database}";
        start.Environment["Health__url"] = $"http://127.0.0.1:{healthPort}";
        // Every host here gets its own ports: the onboard listener's default (58005) is the field's, and two hosts in one test
        // must not collide on anything but the database they are pointed at.
        start.Environment["OnboardTransport__listenAddress"] = "127.0.0.1";
        start.Environment["OnboardTransport__port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        return start;
    }

    /// <summary>Starts a real host on the database and returns once its <c>/health/live</c> answers.</summary>
    private async Task<Process> StartHostAsync(string database, int healthPort)
    {
        Process host = Process.Start(HostStart(database, healthPort))!;
        _hosts.Add(host);
        StringBuilder log = new();
        host.OutputDataReceived += (_, line) => { lock (log) { log.AppendLine(line.Data); } };
        host.ErrorDataReceived += (_, line) => { lock (log) { log.AppendLine(line.Data); } };
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (host.HasExited)
            {
                break;
            }
            try
            {
                using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{healthPort}/health/live", Token);
                if (response.IsSuccessStatusCode)
                {
                    return host;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!Token.IsCancellationRequested)
            {
            }
            await Task.Delay(100, Token);
        }
        string text;
        lock (log)
        {
            text = log.ToString();
        }
        Assert.Fail($"The host on {database} did not come up (exited: {host.HasExited}).{Environment.NewLine}{text}");
        return host;
    }

    private static async Task<(int ExitCode, string Log, TimeSpan Took)> RunToExitAsync(ProcessStartInfo start, TimeSpan limit)
    {
        Stopwatch clock = Stopwatch.StartNew();
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bounded.CancelAfter(limit);
        try
        {
            await process.WaitForExitAsync(bounded.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        TimeSpan took = clock.Elapsed;
        return (process.ExitCode, await stdout + await stderr, took);
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Never started, or already reaped.
        }
    }

    /// <summary>Runs FieldOps; each variable is set in the child's environment.</summary>
    private static async Task<(int ExitCode, JsonElement Output)> RunFieldOpsAsync(
        (string Name, string Value)[] environment, string[] arguments)
    {
        Assert.True(File.Exists(FieldOps), $"FieldOps was not built next to the tests: {FieldOps}");
        ProcessStartInfo start = new(FieldOps)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token);
        string output = (await stdout).Trim();
        _ = await stderr;
        if (output.Length == 0)
        {
            return (process.ExitCode, default);
        }
        using JsonDocument document = JsonDocument.Parse(output);
        return (process.ExitCode, document.RootElement.Clone());
    }
}
