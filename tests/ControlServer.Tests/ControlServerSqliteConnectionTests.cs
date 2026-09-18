using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace ControlServer.Tests;

/// <summary>
/// 这个库有两个进程在写，连接串因此只在一处拼：<see cref="ControlServerSqlite"/>。
/// </summary>
/// <remarks>
/// <para>
/// 两个进程是服务端主机与 <c>ControlServer.FieldOps</c>。SQLite 同一时刻只容一个写事务，所以两边撞上时
/// 先报出来的是 <c>SQLITE_BUSY</c>——等多久才算撞上，是连接串上的 <c>Default Timeout</c> 决定的。两个
/// 进程各自拼一串出来，这个值就会各是各的，而它是「一次正常的并发操作会不会失败」的分界。
/// </para>
/// <para>
/// 下面的断言都很短，因为这一处本来就只做三件事：展开路径、写上等锁上限、按需要打上只读。它们守的是
/// 「这三件事是同一处做的」，不是这三件事本身有多难。
/// </para>
/// </remarks>
public sealed class ControlServerSqliteConnectionTests
{
    /// <summary>
    /// 等写锁的上限写进连接串，而且是明写的。
    /// </summary>
    /// <remarks>
    /// 30 秒恰好也是 Microsoft.Data.Sqlite 的默认值，所以这条测试守的不是数值本身，是「它被写下来过」：
    /// 驱动改了默认值、或者有人往连接串里加别的键，这里会说话。
    /// </remarks>
    [Fact]
    public void TheSharedConnectionStringCarriesTheBusyTimeout()
    {
        SqliteConnectionStringBuilder built = new(
            ControlServerSqlite.ForDatabaseFile(Path.Combine("data", "controlserver.db")));

        Assert.Equal(ControlServerSqlite.BusyTimeoutSeconds, built.DefaultTimeout);
    }

    /// <summary>
    /// 配置里那一串照样规范化：环境变量展开、斜杠统一，busy timeout 补上。
    /// </summary>
    /// <remarks>
    /// <c>appsettings.json</c> 里写的就是 <c>%ProgramData%/8005/...</c> 这个形状，服务端装完第一次启动
    /// 靠的正是这一步。
    /// </remarks>
    [Fact]
    public void AConfiguredConnectionStringIsExpandedAndGivenTheSamePolicy()
    {
        string expanded = ControlServerSqlite.FromConfigured(
            "Data Source=%ProgramData%/8005/ControlServer/data/controlserver.db");

        SqliteConnectionStringBuilder built = new(expanded);
        Assert.Equal(ControlServerSqlite.BusyTimeoutSeconds, built.DefaultTimeout);
        Assert.DoesNotContain("%ProgramData%", built.DataSource, StringComparison.Ordinal);
        Assert.DoesNotContain('/', built.DataSource);
        Assert.Equal(built.DataSource, ControlServerSqlite.DataSourceOf(expanded));
    }

    /// <summary>
    /// 只读那一路除了只读，其余一模一样。
    /// </summary>
    /// <remarks>
    /// <c>check-binding-snapshots</c> 走的是这一路。只读连接一样会撞上写锁，所以它也要那个等待上限——
    /// 缺了它，体检会在服务端一次正常发布的中途报错退出，而那不是它查出来的东西。
    /// </remarks>
    [Fact]
    public void TheReadOnlyPathIsTheSamePolicyPlusReadOnly()
    {
        SqliteConnectionStringBuilder built = new(
            ControlServerSqlite.ForDatabaseFile("controlserver.db", readOnly: true));

        Assert.Equal(SqliteOpenMode.ReadOnly, built.Mode);
        Assert.Equal(ControlServerSqlite.BusyTimeoutSeconds, built.DefaultTimeout);
    }

    /// <summary>
    /// 等锁上限可以调短，因为套件要靠它把「一直撞不上」的等待压到一秒。
    /// </summary>
    [Fact]
    public void TheBusyTimeoutCanBeShortenedForATestThatWantsToFailFast()
    {
        SqliteConnectionStringBuilder built = new(
            ControlServerSqlite.ForDatabaseFile("controlserver.db", busyTimeoutSeconds: 1));

        Assert.Equal(1, built.DefaultTimeout);
    }

    /// <summary>
    /// 产品代码里没有第二处在拼这个库的连接串。
    /// </summary>
    /// <remarks>
    /// 这一条才是前面几条成立的前提：等锁上限只有在「所有写这个库的进程都用同一处」的时候才是一条策略，
    /// 否则它只是某一个进程碰巧带着的一个值。所以扫源码——<c>Data Source=</c> 这个字面量在 <c>src/</c> 与
    /// <c>tools/</c> 里只许出现在两个地方：<see cref="ControlServerSqlite"/> 自己，和
    /// <c>appsettings.json</c> 缺省时主机用的那个兜底串（它随后就交给 <see cref="ControlServerSqlite"/>
    /// 规范化）。
    /// </remarks>
    [Fact]
    public void NothingElseInProductCodeBuildsAConnectionStringForThisDatabase()
    {
        string root = RepositoryRoot();
        string[] elsewhere =
        [
            .. ProductSourceRoots
                .SelectMany(top => Directory.EnumerateFiles(
                    Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(relative => relative is not "src/ControlServer.Infrastructure/Persistence/ControlServerSqlite.cs"
                    and not "src/ControlServer.Host/Program.cs")
                .Where(relative => File.ReadAllText(Path.Combine(root, relative))
                    .Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            elsewhere.Length == 0,
            "These files build a SQLite connection string of their own, which means the busy timeout "
            + "two processes share is no longer one decision: " + string.Join(", ", elsewhere));
    }

    /// <summary>The two top-level directories that hold product source.</summary>
    private static readonly string[] ProductSourceRoots = ["src", "tools"];

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
}
