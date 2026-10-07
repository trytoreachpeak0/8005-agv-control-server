using Microsoft.Data.Sqlite;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 控制服务器那个 SQLite 库的连接串，唯一定义处。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个库有两个进程在写。</b>服务端主机是一个，<c>ControlServer.FieldOps</c> 是另一个——它的
/// <c>bind-io</c> 发布 IO 绑定、<c>release</c> 放行仓位配置，写的都是同一个文件。SQLite 的并发模型是
/// 「同一时刻只有一个写事务」，所以两个进程撞上的第一现场是 <c>SQLITE_BUSY</c>（错误码 5），报出来的
/// 原话是 <c>database is locked</c>。
/// </para>
/// <para>
/// <b>撞上之后的策略分两段，这里是第一段。</b><see cref="BusyTimeoutSeconds"/> 写进连接串的
/// <c>Default Timeout</c>：Microsoft.Data.Sqlite 把命令超时实现成一个「撞上写锁就退让重试」的循环，
/// 所以它就是这个库的 busy timeout——对方那次写事务在这个时间内结束的话，这一次根本不会失败。第二段在
/// <see cref="SlotConfigurationVersionLine"/>：等满了还拿不到锁时，整次取号重来。
/// </para>
/// <para>
/// <b>为什么要明写出来。</b>30 秒恰好也是 Microsoft.Data.Sqlite 的默认值，所以这一行不改变今天的行为。
/// 改变的是它<b>是不是一个决定</b>：在此之前全仓没有一处提到过 busy timeout，升级驱动改了默认值、或者
/// 有人往连接串里加别的键时，没有任何东西会说这个值是被想过的。它也是测试能把等待缩短的那个旋钮。
/// </para>
/// <para>
/// <b>刻意没做的事。</b>没有关 <c>Pooling</c>：池里那条空闲连接不持有任何锁，关掉它只是让每条命令
/// 多一次开库。
/// </para>
/// <para>
/// <b>这个库是 WAL 模式，而且一直是。</b>这里的连接串不设日志模式，打开它的是 EF Core：库文件不存在时
/// <c>Database.MigrateAsync()</c> 先调 <c>SqliteDatabaseCreator.Create()</c>，那里执行
/// <c>PRAGMA journal_mode = 'wal';</c>（Microsoft.EntityFrameworkCore.Sqlite 8.0.30），模式记在文件头里，
/// 之后每次打开都是 WAL。这段注释原先写的是「没有开 WAL」，那是错的（control-server#503）。WAL 下读不挡写、
/// 写不挡读，但同一时刻仍然只有一个写事务，所以上面那两段关于 <c>SQLITE_BUSY</c> 的话照样成立。
/// </para>
/// <para>
/// <b>WAL 让库变成三个文件：</b><c>controlserver.db</c>、<c>-wal</c> 与 <c>-shm</c>。已提交但还没写回主文件的
/// 行只在 <c>-wal</c> 里，所以服务端运行中、或进程被强杀之后只拷主文件，拷出来的库能打开、完整性检查也过，
/// 却静默少了最近的行。拷库只有两种做法：停服务（并确认进程已退出）后整个目录拷，或者运行中用
/// <c>VACUUM INTO</c> 导出一个单文件快照。
/// </para>
/// </remarks>
public static class ControlServerSqlite
{
    /// <summary>
    /// 撞上另一个进程的写锁时，驱动自己退让重试多久，单位秒。
    /// </summary>
    /// <remarks>
    /// 30 秒是按「另一个进程正在做的那件事有多长」定的：现场运维一次发布是几十行绑定加一次冻结，远在这个
    /// 数量级之内。等不到再久也没有意义——那意味着对方卡住了，那时候重新取号（或者报出来）比继续等要好。
    /// </remarks>
    public const int BusyTimeoutSeconds = 30;

    /// <summary>
    /// 把配置里那一串规范化成本仓统一的连接串：展开环境变量、统一路径分隔符、写上 busy timeout。
    /// </summary>
    /// <param name="configuredConnectionString">配置里读到的那一串，例如
    /// <c>Data Source=%ProgramData%/8005/ControlServer/data/controlserver.db</c>。</param>
    /// <param name="readOnly">只读打开。只读由驱动保证，不靠调用方自觉。</param>
    /// <param name="busyTimeoutSeconds">等锁上限，秒。默认 <see cref="BusyTimeoutSeconds"/>。</param>
    public static string FromConfigured(
        string configuredConnectionString,
        bool readOnly = false,
        int busyTimeoutSeconds = BusyTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredConnectionString);
        ArgumentOutOfRangeException.ThrowIfNegative(busyTimeoutSeconds);

        SqliteConnectionStringBuilder builder = new(configuredConnectionString)
        {
            DefaultTimeout = busyTimeoutSeconds
        };
        builder.DataSource = ExpandDataSource(builder.DataSource);
        if (readOnly)
        {
            builder.Mode = SqliteOpenMode.ReadOnly;
        }
        return builder.ConnectionString;
    }

    /// <summary>磁盘上一个库文件的连接串。</summary>
    public static string ForDatabaseFile(
        string databasePath,
        bool readOnly = false,
        int busyTimeoutSeconds = BusyTimeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return FromConfigured(
            new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString,
            readOnly,
            busyTimeoutSeconds);
    }

    /// <summary>连接串指着的那个文件路径。</summary>
    public static string DataSourceOf(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new SqliteConnectionStringBuilder(connectionString).DataSource;
    }

    /// <summary>
    /// 库文件所在的目录不存在就建出来。
    /// </summary>
    /// <remarks>
    /// 装完第一次启动时那个目录还不存在，而 SQLite 只建文件不建目录。
    /// </remarks>
    public static void EnsureDataSourceDirectory(string connectionString)
    {
        string? directory = Path.GetDirectoryName(DataSourceOf(connectionString));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string ExpandDataSource(string dataSource) =>
        string.IsNullOrWhiteSpace(dataSource)
            ? dataSource
            : Environment.ExpandEnvironmentVariables(dataSource).Replace('/', Path.DirectorySeparatorChar);
}
