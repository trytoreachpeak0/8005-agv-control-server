using System.Reflection;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace ControlServer.Tests;

/// <summary>
/// Leaves Microsoft.Data.Sqlite 8.0.30's pool for one database so that the next open is handed a native connection another
/// owner is still using, the way it happened at 2026-10-19 06:48:25Z (control-server#779, dotnet/efcore#39008).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic, not a race: the window the race needs -- an internal connection active with no owner -- is made by hand.
/// Owner <c>x</c> is an EF-initialised connection (so its handle carries EF's functions) with a statement that has stepped to a
/// row and is still active. A second open connection <c>z</c> makes the pool's count even. Then <c>x</c>'s internal connection
/// forgets its owner. The next open finds no idle connection and an even count, calls <c>ReclaimLeakedConnections</c>, takes
/// <c>x</c>'s internal connection as leaked and is handed it; registering EF's functions over the ones <c>x</c> registered,
/// with <c>x</c>'s statement active, fails with SQLITE_BUSY, and so does unregistering them in <c>Open</c>'s own catch. That
/// second failure leaves the opener at <c>State == Open</c>, still holding <c>x</c>'s handle (SqliteConnection 8.0.30 :321-327).
/// </para>
/// <para>Reaches into two private fields of the driver; a rename fails loudly here rather than making a test pass.</para>
/// </remarks>
internal sealed class PooledConnectionSharingShape : IAsyncDisposable
{
    private readonly ControlServerDbContext _owner;
    private readonly SqliteConnection _other;
    private sqlite3_stmt? _statement;

    private PooledConnectionSharingShape(ControlServerDbContext owner, SqliteConnection other, sqlite3_stmt statement)
    {
        _owner = owner;
        _other = other;
        _statement = statement;
    }

    public static async Task<PooledConnectionSharingShape> ArrangeAsync(string connectionString)
    {
        using (SqliteConnection pooled = new(connectionString))
        {
            SqliteConnection.ClearPool(pooled);
        }

        ControlServerDbContext owner = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSerializedSqlite(connectionString).Options);
        SqliteConnection x = (SqliteConnection)owner.Database.GetDbConnection();
        await x.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_prepare_v2(x.Handle, "SELECT 1 UNION ALL SELECT 2", out sqlite3_stmt statement));
        Assert.Equal(raw.SQLITE_ROW, raw.sqlite3_step(statement));

        SqliteConnection z = new(connectionString);
        await z.OpenAsync(TestContext.Current.CancellationToken);

        object inner = typeof(SqliteConnection)
            .GetField("_innerConnection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(x) ?? throw new InvalidOperationException("SqliteConnection._innerConnection is gone.");
        WeakReference<SqliteConnection?> ownerReference = (WeakReference<SqliteConnection?>)inner.GetType()
            .GetField("_outerConnection", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(inner)!;
        ownerReference.SetTarget(null);

        return new PooledConnectionSharingShape(owner, z, statement);
    }

    public async ValueTask DisposeAsync()
    {
        _statement?.Dispose();
        _statement = null;
        await Quietly(_other.DisposeAsync);
        await Quietly(_owner.DisposeAsync);
    }

    /// <summary>The shape leaves the pool in a state no clean teardown exists for; what is left is the test's database's alone.</summary>
    public static async Task Quietly(Func<ValueTask> dispose)
    {
        try
        {
            await dispose();
        }
        catch (Exception error) when (error is SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            // Unregistering functions on a handle another owner still uses can fail; nothing to keep.
        }
    }
}
