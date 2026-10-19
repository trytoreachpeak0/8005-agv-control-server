using System.Collections.Concurrent;
using System.Data;
using System.Reflection;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 主机的每个 DbContext 用一条开关互斥的连接（control-server#779，dotnet/efcore#39008）。
/// </summary>
/// <remarks>
/// <b>这里证不到的事。</b>原竞态在本机按上游复现程序跑到 1500 轮 × 64 线程也没打中，所以没有一条用例能在去掉锁后变红。
/// 这里证的是锁确实在每一条开关的路上、拿住它就挡得住开与关（S1）：异步开、异步关、同步与异步释放都经过重写的两个方法（读源码之外的证据）；主机的注册
/// 方式让每个作用域有自己的一条连接；以及并发下开关串行之后，同时开着的两个上下文从不共用一个底层句柄。
/// </remarks>
public sealed class SerializedSqliteConnectionTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AsyncOpenCloseAndDisposeAllGoThroughTheOverriddenMethods()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        using TemporaryDatabase database = new();

        CountingConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(token);
        Assert.Equal((1, 0), (connection.Opens, connection.Closes));
        await connection.CloseAsync();
        Assert.Equal((1, 1), (connection.Opens, connection.Closes));
        await connection.OpenAsync(token);
        await connection.DisposeAsync();
        Assert.Equal((2, 2), (connection.Opens, connection.Closes));

        CountingConnection disposed = new(database.ConnectionString);
        disposed.Open();
        disposed.Dispose();
        Assert.Equal((1, 1), (disposed.Opens, disposed.Closes));

        // And the way EF drives it: opened and closed around an async query, closed again when the context is disposed.
        CountingConnection driven = new(database.ConnectionString);
        await using (ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
                         .UseSqlite(driven, contextOwnsConnection: true).Options))
        {
            await context.Database.EnsureCreatedAsync(token);
            int before = driven.Opens;
            _ = await context.ProtocolInbox.AsNoTracking().AnyAsync(token);
            Assert.True(driven.Opens > before, "EF 的异步查询没有经过重写的 Open()。");
            Assert.Equal(driven.Opens, driven.Closes);
        }
        Assert.Equal(driven.Opens, driven.Closes);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void EachScopeOfTheHostsRegistrationGetsItsOwnSerializedConnection()
    {
        using TemporaryDatabase database = new();
        ServiceCollection services = new();
        services.AddDbContext<ControlServerDbContext>(options => options.UseSerializedSqlite(database.ConnectionString));
        using ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();
        System.Data.Common.DbConnection a = first.ServiceProvider.GetRequiredService<ControlServerDbContext>().Database.GetDbConnection();
        System.Data.Common.DbConnection again = first.ServiceProvider.GetRequiredService<ControlServerDbContext>().Database.GetDbConnection();
        System.Data.Common.DbConnection b = second.ServiceProvider.GetRequiredService<ControlServerDbContext>().Database.GetDbConnection();

        Assert.IsType<SerializedSqliteConnection>(a);
        Assert.IsType<SerializedSqliteConnection>(b);
        Assert.Same(a, again);
        Assert.NotSame(a, b);
    }

    /// <summary>
    /// 8 路并发，每路反复新建上下文、开连接、查询、关连接，库是池化的文件库。同时开着的连接从不共用一个 sqlite3 句柄，
    /// 也没有一次失败。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ConcurrentContextsNeverShareANativeHandle()
    {
        const int lanes = 8;
        const int rounds = 300;
        CancellationToken token = TestContext.Current.CancellationToken;
        using TemporaryDatabase database = new();
        await using (ControlServerDbContext setup = Context(database.ConnectionString))
        {
            await setup.Database.EnsureCreatedAsync(token);
        }

        ConcurrentDictionary<nint, int> held = new();
        int shared = 0;
        ConcurrentQueue<Exception> failures = new();
        using Barrier start = new(lanes);
        Task[] workers = [.. Enumerable.Range(0, lanes).Select(lane => Task.Factory.StartNew(async () =>
        {
            start.SignalAndWait(token);
            for (int round = 0; round < rounds; round++)
            {
                try
                {
                    await using ControlServerDbContext context = Context(database.ConnectionString);
                    await context.Database.OpenConnectionAsync(token);
                    nint handle = ((SqliteConnection)context.Database.GetDbConnection()).Handle!.DangerousGetHandle();
                    if (held.AddOrUpdate(handle, 1, (_, count) => count + 1) > 1)
                    {
                        Interlocked.Increment(ref shared);
                    }
                    try
                    {
                        _ = await context.ProtocolInbox.AsNoTracking().AnyAsync(token);
                    }
                    finally
                    {
                        // Released before the connection goes back to the pool, so a later legitimate reuse never counts.
                        held.AddOrUpdate(handle, 0, (_, count) => count - 1);
                    }
                    await context.Database.CloseConnectionAsync();
                }
                catch (Exception error)
                {
                    failures.Enqueue(error);
                }
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap())];
        await Task.WhenAll(workers);

        Assert.Empty(failures);
        Assert.Equal(0, shared);
    }

    /// <summary>
    /// 审查 S1：锁真的在每一次开与关的路上。测试线程先拿住那把锁，另起的线程开一条、关一条，都要等；放开之后都走完。把锁换成
    /// 不锁（例如 <c>if (Gate is not null)</c>），两个线程立即走完，这条变红。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void OpenAndCloseWaitForTheGate()
    {
        using TemporaryDatabase database = new();
        object gate = typeof(SerializedSqliteConnection)
            .GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null) ?? throw new InvalidOperationException("SerializedSqliteConnection.Gate is gone.");
        using SerializedSqliteConnection opening = new(database.ConnectionString);
        using SerializedSqliteConnection closing = new(database.ConnectionString);
        closing.Open();
        Thread opener = new(opening.Open) { IsBackground = true };
        Thread closer = new(closing.Close) { IsBackground = true };

        Monitor.Enter(gate);
        try
        {
            opener.Start();
            closer.Start();
            Assert.False(opener.Join(TimeSpan.FromMilliseconds(500)), "锁被拿住时 Open 照样走完了。");
            Assert.False(closer.Join(TimeSpan.FromMilliseconds(500)), "锁被拿住时 Close 照样走完了。");
            Assert.Equal(ConnectionState.Closed, opening.State);
            Assert.Equal(ConnectionState.Open, closing.State);
        }
        finally
        {
            Monitor.Exit(gate);
        }

        Assert.True(opener.Join(TimeSpan.FromSeconds(10)));
        Assert.True(closer.Join(TimeSpan.FromSeconds(10)));
        Assert.Equal(ConnectionState.Open, opening.State);
        Assert.Equal(ConnectionState.Closed, closing.State);
    }

    /// <summary>
    /// 审查 M1 的方案 2：开库被池递来别人还在用的连接、<c>Open</c> 抛出之后仍处于打开状态时，这条连接标成
    /// <see cref="ConnectionState.Broken"/>，之后谁再开都抛 <see cref="SqliteConnectionPoisonedException"/>，关它不碰池。EF 见到
    /// Broken 会先关再开，于是同一个上下文里后来的查询也跑不到别人的句柄上。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AnOpenLeftHoldingAnotherOwnersConnectionLeavesItBrokenAndUnusable()
    {
        using TemporaryDatabase database = new();
        await using ControlServerDbContext context = Context(database.ConnectionString);
        SerializedSqliteConnection connection = (SerializedSqliteConnection)context.Database.GetDbConnection();
        Exception? first;
        await using (PooledConnectionSharingShape shape = await PooledConnectionSharingShape.ArrangeAsync(database.ConnectionString))
        {
            first = Record.Exception(connection.Open);

            Assert.IsType<SqliteConnectionPoisonedException>(first);
            Assert.True(first.InnerException is SqliteException { SqliteErrorCode: 5 }, $"内层不是开库时的 BUSY：{first}");
            Assert.Equal(ConnectionState.Broken, connection.State);
            Assert.IsType<SqliteConnectionPoisonedException>(Record.Exception(connection.Open));
            Assert.IsType<SqliteConnectionPoisonedException>(
                await Record.ExceptionAsync(() => context.ProtocolInbox.AsNoTracking().AnyAsync(TestContext.Current.CancellationToken)));
            connection.Close();
            Assert.Equal(ConnectionState.Broken, connection.State);
        }
    }

    /// <summary>
    /// 审查 S5：主机里不许有第二条建 SQLite 连接的路。src/ 里除了 <see cref="SerializedSqliteConnection"/> 自己和设计时工厂，不许出现
    /// <c>UseSqlite(</c> 或 <c>new SqliteConnection(</c>——那样建出来的连接开关不过那把锁。tools/ 不在内：FieldOps 是另一个进程、单线程。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public void NothingElseInTheHostBuildsASqliteConnection()
    {
        string root = RepositoryRoot();
        string[] elsewhere =
        [
            .. Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(relative => relative is not "src/ControlServer.Infrastructure/Persistence/SerializedSqliteConnection.cs"
                    and not "src/ControlServer.Infrastructure/Persistence/ControlServerDesignTimeDbContextFactory.cs")
                .Where(relative =>
                {
                    string text = File.ReadAllText(Path.Combine(root, relative));
                    return text.Contains("UseSqlite(", StringComparison.Ordinal)
                        || text.Contains("new SqliteConnection(", StringComparison.Ordinal);
                })
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            elsewhere.Length == 0,
            "These files build a SQLite connection whose open and close do not take SerializedSqliteConnection's lock "
            + "(control-server#779): " + string.Join(", ", elsewhere));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ControlServer repository root.");
    }

    private static ControlServerDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSerializedSqlite(connectionString).Options);

    private sealed class CountingConnection(string connectionString) : SerializedSqliteConnection(connectionString)
    {
        public int Opens { get; private set; }

        public int Closes { get; private set; }

        public override void Open()
        {
            Opens++;
            base.Open();
        }

        public override void Close()
        {
            // SqliteConnection.Close returns at once on a closed connection; count only the ones that close something.
            if (State == System.Data.ConnectionState.Open)
            {
                Closes++;
            }
            base.Close();
        }
    }

    /// <summary>A pooled file database in the temp directory, its own pool cleared and its files removed afterwards.</summary>
    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"cs779-{Guid.NewGuid():N}.db");

        public string ConnectionString => ControlServerSqlite.ForDatabaseFile(_path);

        public void Dispose()
        {
            // This database's pool only: clearing every pool would reach the tests running beside this one.
            using (SqliteConnection pooled = new(ConnectionString))
            {
                SqliteConnection.ClearPool(pooled);
            }
            foreach (string file in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Best effort: a temp file left behind is not a test failure.
                }
            }
        }
    }
}
