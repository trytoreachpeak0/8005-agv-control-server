using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// 一条 <see cref="SqliteConnection"/>，它的开与关在本进程里互斥（control-server#779）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么。</b>Microsoft.Data.Sqlite 8 的连接池能把同一条底层连接（一个 <c>sqlite3*</c>）同时借给两个
/// <see cref="SqliteConnection"/>（dotnet/efcore#39008，修复只进了 10.0.13，8.0.30 没有）。池里的内部连接在
/// <c>Leaked == _active &amp;&amp; owner 已不在</c> 时会被当作泄漏回收，而有两处它短暂地「active 却没有 owner」：借出时
/// <c>SqliteConnectionInternal.Activate</c> 先置 <c>_active</c> 再设 owner，归还时 <c>Deactivate</c> 先清 owner 再清
/// <c>_active</c>。另一个线程此时开库、池为空且连接数为偶数，就会在 <c>ReclaimLeakedConnections</c> 里把它当泄漏
/// 归还、再借走，于是两个使用者共用一条连接，也就共用它的事务。v5 上线当天的两次断线就是它的样子：06:48:25Z
/// 注销 EF 函数时报 SQLITE_BUSY（「这条连接上有别人的活动语句」），06:55:22Z 借到一个已被池释放的句柄。
/// </para>
/// <para>
/// <b>怎么挡。</b>那两个窗口都只在 <see cref="Open"/> 与 <see cref="Close"/> 里面，看到它们的也只有另一次
/// <see cref="Open"/>；让本进程所有这种连接的开与关互斥，就没有人能看到半截状态。异步与释放的路都落在这两个
/// 虚方法上：<c>DbConnection.OpenAsync</c> 同步调用 <c>Open()</c>，<c>CloseAsync</c> 调 <c>Close()</c>，
/// <c>DisposeAsync</c> 调 <c>Dispose()</c>，而 <c>SqliteConnection.Dispose(bool)</c> 调 <c>Close()</c>；
/// SqliteConnection 8.0.30 一个异步方法都没有重写（测试 <c>SerializedSqliteConnectionTests</c> 用计数证这一点）。
/// </para>
/// <para>
/// <b>不会死锁的理由。</b>锁里只有 base 调用，没有 await，不罩任何查询。<c>base.Open()</c> 做的是从池里取连接、
/// 注册 EF 的函数；连接串不设 <c>Foreign Keys</c>/<c>Recursive Triggers</c>，所以它不执行语句、不等写锁。
/// <c>base.Close()</c> 回滚还开着的事务（ROLLBACK 不等锁）、释放命令、把连接还给池。两者都不回调到这里，也不会
/// 再开一条连接；<see cref="System.Threading.Monitor"/> 可重入，即使将来某条路径在锁里再次 Open 同一线程也不会
/// 自锁。池内部那把锁只在池代码里拿、不回调出来，所以没有第二把锁与这把交叉。
/// </para>
/// <para>
/// <b>只保护走它的连接。</b>主机里所有 DbContext 由 <c>Program.cs</c> 的 <c>AddDbContext</c> 经它建出来。
/// EF 在库文件还不存在时另开的只读探测连接、以及 <c>ControlServer.FieldOps</c>（另一个进程，一次命令一个上下文、
/// 单线程，没有并发开库）不经过这里。
/// </para>
/// </remarks>
public class SerializedSqliteConnection(string connectionString) : SqliteConnection(connectionString)
{
    private static readonly object Gate = new();

    public override void Open()
    {
        lock (Gate)
        {
            base.Open();
        }
    }

    public override void Close()
    {
        lock (Gate)
        {
            base.Close();
        }
    }
}

/// <summary>How the host gives each DbContext its own <see cref="SerializedSqliteConnection"/>.</summary>
public static class SerializedSqliteDbContextOptions
{
    /// <summary>
    /// A new connection for every context the options are built for, owned and disposed by that context. With
    /// <c>AddDbContext</c> the options are built once per scope, so each scope -- each onboard connection, each runtime
    /// round -- gets its own.
    /// </summary>
    public static DbContextOptionsBuilder UseSerializedSqlite(this DbContextOptionsBuilder options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return options.UseSqlite(new SerializedSqliteConnection(connectionString), contextOwnsConnection: true);
    }

    /// <inheritdoc cref="UseSerializedSqlite(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<TContext> UseSerializedSqlite<TContext>(
        this DbContextOptionsBuilder<TContext> options, string connectionString)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseSerializedSqlite((DbContextOptionsBuilder)options, connectionString);
}
