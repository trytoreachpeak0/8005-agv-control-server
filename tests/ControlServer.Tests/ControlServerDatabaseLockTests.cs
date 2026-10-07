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
/// 「探的那一刻服务端停着」，探完、写库前服务端恰好启动的那个时间窗，只有锁关得上。这里起的是真的 Host 进程与真的 FieldOps 进程；
/// Host 能连的外部地址（RIoT、MesIngest）都钉在回环上没人监听的端口，旅程运行时显式关着。
/// </summary>
public sealed class ControlServerDatabaseLockTests : IAsyncDisposable
{
    private const string KeyA = "KEY-A";

    /// <summary>The FieldOps test seams' variables; see <c>Program.PauseAfterProbeVariable</c> and <c>PauseAfterLockVariable</c>.</summary>
    private const string PauseAfterProbe = "CONTROL_SERVER_FIELDOPS_TEST_PAUSE_AFTER_PROBE";

    private const string PauseAfterLock = "CONTROL_SERVER_FIELDOPS_TEST_PAUSE_AFTER_LOCK";

    /// <summary>What the host logs while it waits for the lock (<c>DatabaseLockWaiting</c>, event 9404).</summary>
    private const string LockWaitingLog = "Another process holds the lock on database";

    private static readonly DateTimeOffset At = Batch7JourneyFixture.Now;

    private static readonly string FieldOps = BuiltPath("ControlServer.FieldOps.Path");

    private static readonly string HostExecutable = BuiltPath("ControlServer.Host.Path");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "w2g-database-lock-" + Guid.NewGuid().ToString("N"));

    /// <summary>Every host and FieldOps process this test started, killed by their own handles on dispose.</summary>
    private readonly List<Process> _children = [];

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
            [(PauseAfterProbe, pause)], [.. ReleaseArguments(), "--database", database, "--probe-server", probe]);
        await WaitForFileAsync(pause + ".reached", fieldOps);
        await StartHostAsync(database, healthPort);
        await File.WriteAllTextAsync(pause + ".go", string.Empty, Token);
        (int exit, JsonElement output) = await fieldOps;

        Assert.Equal((1, "DATABASE_IN_USE"), (exit, Outcome(output)));
        Assert.Contains(Path.GetDirectoryName(Path.GetFullPath(database))!, output.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
        await AssertNothingWrittenAsync(database);
    }

    /// <summary>
    /// FieldOps 拿到锁之后一直持有到写完（审查 M7：拿到就放、照常写库，其余用例全绿）。它停在「锁已拿到、还没写」时起一个服务端：
    /// 服务端在等锁（日志里有 <c>DatabaseLockWaiting</c>）、不应答；FieldOps 写完退出之后它才起来。
    /// </summary>
    [Fact]
    public async Task AFieldOpsWriteHoldsTheLockUntilItIsDone()
    {
        string database = await SeedAsync("a");
        string pause = Path.Combine(_root, "pause");
        int healthPort = FreePort();

        Task<(int ExitCode, JsonElement Output)> fieldOps = RunFieldOpsAsync(
            [(PauseAfterLock, pause)],
            [.. ReleaseArguments(), "--database", database, "--probe-server", $"http://127.0.0.1:{FreePort()}/"]);
        await WaitForFileAsync(pause + ".reached", fieldOps);
        HostRun host = LaunchHost(database, healthPort);
        await WaitForLogAsync(host, LockWaitingLog, TimeSpan.FromSeconds(30));
        bool answeredWhileFieldOpsHeldTheLock = await AnswersAsync(healthPort);
        await File.WriteAllTextAsync(pause + ".go", string.Empty, Token);
        (int exit, JsonElement output) = await fieldOps;
        await WaitUntilLiveAsync(host, healthPort);

        Assert.False(answeredWhileFieldOpsHeldTheLock);
        Assert.Equal((0, "OK"), (exit, Outcome(output)));
        await AssertReleasedAsync(database);
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
        Assert.Contains("Startup refused", log, StringComparison.Ordinal);
        Assert.Contains(Path.GetDirectoryName(Path.GetFullPath(database))!, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Now listening", log, StringComparison.Ordinal);
        Assert.True(took >= DatabaseLockStartup.Wait, $"It refused after {took}, before waiting {DatabaseLockStartup.Wait}.");
        Assert.True(await AnswersAsync(firstPort));
    }

    /// <summary>
    /// <c>--migrate-only</c> 撞上运行中的服务端：迁移被拒（说的是迁移，不是「服务端拒绝启动」），退出码非 0。
    /// </summary>
    [Fact]
    public async Task MigrateOnlyAgainstADatabaseInUseIsRefusedAsAMigration()
    {
        string database = await SeedAsync("a");
        await StartHostAsync(database, FreePort());
        ProcessStartInfo migrate = HostStart(database, FreePort());
        migrate.ArgumentList.Add("--migrate-only");

        (int exit, string log, _) = await RunToExitAsync(migrate, TimeSpan.FromSeconds(90));

        Assert.True(exit != 0, log);
        Assert.Contains("Migration refused", log, StringComparison.Ordinal);
        Assert.Contains(DatabaseLockStartup.ReasonCode, log, StringComparison.Ordinal);
        Assert.DoesNotContain("Startup refused", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// 锁文件根本打不开（这里是被设成只读；没有权限同理），不是「有人占着」：服务端不崩成一段堆栈，而是以
    /// <c>DATABASE_LOCK_FILE_UNUSABLE</c> 拒绝启动并写出是哪个文件；FieldOps 同样拒绝、一行不写。
    /// </summary>
    [Fact]
    public async Task ALockFileThatCannotBeOpenedIsRefusedWithItsReason()
    {
        string database = await SeedAsync("a");
        string lockFile = ControlServerDatabaseLock.LockFileFor(database);
        await File.WriteAllTextAsync(lockFile, string.Empty, Token);
        File.SetAttributes(lockFile, FileAttributes.ReadOnly);

        ControlServerDatabaseLockException thrown =
            Assert.Throws<ControlServerDatabaseLockException>(() => ControlServerDatabaseLock.TryAcquire(database));
        (int hostExit, string log, _) = await RunToExitAsync(HostStart(database, FreePort()), TimeSpan.FromSeconds(90));
        (int exit, JsonElement output) = await RunFieldOpsAsync(
            [], [.. ReleaseArguments(), "--database", database, "--probe-server", $"http://127.0.0.1:{FreePort()}/"]);

        Assert.Equal(lockFile, thrown.LockFile);
        Assert.Contains(lockFile, thrown.Message, StringComparison.Ordinal);
        Assert.True(hostExit != 0, log);
        Assert.Contains("Startup refused", log, StringComparison.Ordinal);
        Assert.Contains(DatabaseLockStartup.UnusableReasonCode, log, StringComparison.Ordinal);
        Assert.Contains(lockFile, log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Now listening", log, StringComparison.Ordinal);
        Assert.Equal((1, "DATABASE_LOCK_FILE_UNUSABLE", lockFile), (exit, Outcome(output), output.GetProperty("lockFile").GetString()));
        await AssertNothingWrittenAsync(database);
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
        foreach (Process child in _children)
        {
            await StopAsync(child);
            child.Dispose();
        }
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(_root))
        {
            return;
        }
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
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

    private static string BuiltPath(string key) =>
        typeof(ControlServerDatabaseLockTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == key).Value!;

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

    /// <summary>Whether anything answers <c>/health/live</c> on the port right now.</summary>
    private static async Task<bool> AnswersAsync(int healthPort)
    {
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{healthPort}/health/live", Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!Token.IsCancellationRequested)
        {
            return false;
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
        string invariant(int port) => port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["ConnectionStrings__ControlServer"] = $"Data Source={database}";
        start.Environment["Health__url"] = $"http://127.0.0.1:{invariant(healthPort)}";
        // Every host here gets its own ports: the onboard listener's default (58005) is the field's, and two hosts in one test
        // must not collide on anything but the database they are pointed at.
        start.Environment["OnboardTransport__listenAddress"] = "127.0.0.1";
        start.Environment["OnboardTransport__port"] = invariant(FreePort());
        // appsettings.json points RIoT at the real one (172.19.206.222). Nothing here calls it today, but that must not rest on
        // luck: both external addresses go to a loopback port nothing listens on, the runtime is off, and no RIoT key is passed on.
        start.Environment["RIoT__baseUrl"] = $"http://127.0.0.1:{invariant(FreePort())}";
        start.Environment["MesIngest__baseUrl"] = $"http://127.0.0.1:{invariant(FreePort())}";
        start.Environment["JourneyRuntime__enabled"] = "false";
        start.Environment.Remove("CONTROL_SERVER_RIOT_CALL_API_KEY");
        return start;
    }

    /// <summary>A host process with its output collected as it runs.</summary>
    private sealed record HostRun(Process Process, StringBuilder Log)
    {
        public string Text
        {
            get
            {
                lock (Log)
                {
                    return Log.ToString();
                }
            }
        }
    }

    private HostRun LaunchHost(string database, int healthPort)
    {
        Process host = Process.Start(HostStart(database, healthPort))!;
        _children.Add(host);
        StringBuilder log = new();
        host.OutputDataReceived += (_, line) => { lock (log) { log.AppendLine(line.Data); } };
        host.ErrorDataReceived += (_, line) => { lock (log) { log.AppendLine(line.Data); } };
        host.BeginOutputReadLine();
        host.BeginErrorReadLine();
        return new HostRun(host, log);
    }

    private static async Task WaitUntilLiveAsync(HostRun host, int healthPort)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90) && !host.Process.HasExited)
        {
            if (await AnswersAsync(healthPort))
            {
                return;
            }
            await Task.Delay(100, Token);
        }
        Assert.Fail($"The host did not come up (exited: {host.Process.HasExited}).{Environment.NewLine}{host.Text}");
    }

    private static async Task WaitForLogAsync(HostRun host, string text, TimeSpan limit)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!host.Text.Contains(text, StringComparison.Ordinal))
        {
            Assert.True(
                clock.Elapsed < limit && !host.Process.HasExited,
                $"The host never logged '{text}' (exited: {host.Process.HasExited}).{Environment.NewLine}{host.Text}");
            await Task.Delay(50, Token);
        }
    }

    /// <summary>Starts a real host on the database and returns once its <c>/health/live</c> answers.</summary>
    private async Task<Process> StartHostAsync(string database, int healthPort)
    {
        HostRun host = LaunchHost(database, healthPort);
        await WaitUntilLiveAsync(host, healthPort);
        return host.Process;
    }

    private async Task<(int ExitCode, string Log, TimeSpan Took)> RunToExitAsync(ProcessStartInfo start, TimeSpan limit)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Process process = Process.Start(start)!;
        _children.Add(process);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(Token);
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bounded.CancelAfter(limit);
        await process.WaitForExitAsync(bounded.Token);
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
    private async Task<(int ExitCode, JsonElement Output)> RunFieldOpsAsync(
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
        Process process = Process.Start(start)!;
        _children.Add(process);
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
