using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ControlServer.Tests;

/// <summary>
/// A fixture's fresh database in the state <c>Database.MigrateAsync()</c> leaves it, copied from a database migrated once per
/// test process instead of migrating it again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists (control-server#553).</b> One <c>MigrateAsync</c> builds the full target model of every migration's
/// Designer file -- 40 of them at the time, about 190 MB allocated and 0.8 to 1 s on the control laptop -- and most of that
/// work does not run in parallel: under xunit's 18 to 20 concurrent tests on vm01 every such test waited about 1.2 s for
/// each test running beside it, so the suite's wall clock was the sum of every fixture's migration. Copying a migrated
/// database with SQLite's backup API takes about 3 ms and allocates nothing.
/// </para>
/// <para>
/// <b>What the copy is.</b> The same pages a real migration wrote, <c>__EFMigrationsHistory</c> included, so pending
/// migrations are none and a test that later migrates down and up again still finds the history it expects. A file
/// database is also switched to WAL afterwards, as <c>SqliteDatabaseCreator.Create()</c> does when a migration creates the
/// file (<see cref="ControlServerSqlite"/> explains why the mode matters). <c>MigratedDatabaseTemplateTests</c> pins all of
/// that against a real migration, so a difference shows up there rather than as a changed result somewhere else.
/// </para>
/// <para>
/// <b>Where it is not what a migration would have left.</b> A fixture that opens its file database before migrating -- the
/// multi-vehicle <c>FleetFixture</c> with a <c>databaseFile</c> -- has the file created empty by that open, so a real
/// migration finds it existing, skips <c>Create()</c> and leaves it in <c>delete</c> journal mode. The copy is WAL either way
/// (control-server#553). WAL is production's mode and still admits one writer at a time, which is what the only such tests,
/// <c>FieldConfirmationWriteLockTests</c> (control-server#452), rely on; their guard is shown to fail under WAL by mutation.
/// </para>
/// <para>
/// <b>What it does not replace.</b> Tests about the migrations themselves -- the <c>*MigrationDisciplineTests</c>, the
/// <c>*MigrationTests</c>, anything that migrates to a named migration through <c>IMigrator</c>, and
/// <c>RuntimeFixture.CreateAsync(migrate: true)</c> -- keep calling <c>MigrateAsync</c>. A migration that is wrong is wrong
/// in the template as well; only those tests catch it.
/// </para>
/// </remarks>
internal static class MigratedDatabaseTemplate
{
    // Migrated once, never cancelled by whichever test happened to ask first: a cancelled build cached here would fail
    // every later fixture in the process.
    private static readonly Lazy<Task<SqliteConnection>> s_template =
        new(BuildAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    // A SqliteConnection is not safe to use from two threads, and the template is one connection read by every copy.
    private static readonly object s_copyGate = new();

    /// <summary>
    /// Gives the empty database behind <paramref name="database"/> the schema and history of every migration, as
    /// <c>database.MigrateAsync()</c> would when it creates that database itself. A file that already exists empty -- opened
    /// before migrating -- ends up in WAL here where a migration would have left it in <c>delete</c> journal mode (see the
    /// remarks on the class).
    /// </summary>
    /// <exception cref="InvalidOperationException">The database already has tables: a copy would replace them, which a
    /// migration never does.</exception>
    public static async Task ApplyAsync(DatabaseFacade database, CancellationToken cancellationToken)
    {
        SqliteConnection template = await s_template.Value.WaitAsync(cancellationToken);
        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            SqliteConnection target = (SqliteConnection)database.GetDbConnection();
            if (Scalar<long>(target, "SELECT count(*) FROM sqlite_master;") != 0)
            {
                throw new InvalidOperationException(
                    "MigratedDatabaseTemplate copies into an empty database only; this one already has a schema.");
            }
            lock (s_copyGate)
            {
                template.BackupDatabase(target);
            }
            if (IsFile(target))
            {
                string mode = Scalar<string>(target, "PRAGMA journal_mode = 'wal';");
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"The copied database stayed in journal mode '{mode}', not WAL.");
                }
            }
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

    /// <summary>Whether the template refuses writes (<c>PRAGMA query_only</c>); for its own guard test.</summary>
    internal static async Task<bool> TemplateIsQueryOnlyAsync()
    {
        SqliteConnection template = await s_template.Value;
        lock (s_copyGate)
        {
            return Scalar<long>(template, "PRAGMA query_only;") == 1;
        }
    }

    private static async Task<SqliteConnection> BuildAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using (ControlServerDbContext context =
                     new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options))
        {
            await context.Database.MigrateAsync(CancellationToken.None);
        }
        Scalar<long>(connection, "PRAGMA query_only = ON; SELECT 1;");
        return connection;
    }

    private static bool IsFile(SqliteConnection connection)
    {
        SqliteConnectionStringBuilder builder = new(connection.ConnectionString);
        return builder.Mode != SqliteOpenMode.Memory &&
               !string.IsNullOrEmpty(builder.DataSource) &&
               !string.Equals(builder.DataSource, ":memory:", StringComparison.Ordinal);
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
