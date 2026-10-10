using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// <see cref="MigratedDatabaseTemplate"/> hands a fixture the database a real migration would have, its own and nobody
/// else's (control-server#553).
/// </summary>
/// <remarks>
/// Most of the suite's fixtures stopped migrating and copy the template instead, so what this class pins is what every one of
/// them now assumes: a copy is indistinguishable from a migration -- schema, seeded rows, history, file-level pragmas and,
/// for a file, WAL -- and no test's writes reach the template or another test's copy.
/// </remarks>
public sealed class MigratedDatabaseTemplateTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cs553-template-" + Guid.NewGuid().ToString("N"));

    public MigratedDatabaseTemplateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task AnInMemoryCopyIsTheDatabaseARealMigrationWrites()
    {
        await using SqliteConnection migrated = await OpenMemoryAsync();
        await using (ControlServerDbContext context = Context(migrated))
        {
            await context.Database.MigrateAsync(Token);
        }
        await using SqliteConnection copied = await OpenMemoryAsync();
        await using (ControlServerDbContext context = Context(copied))
        {
            await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
        }

        Assert.Equal(Describe(migrated), Describe(copied));
    }

    [Fact]
    public async Task AFileCopyIsTheDatabaseARealMigrationWritesInWalMode()
    {
        string migratedPath = Path.Combine(_directory, "migrated.db");
        string copiedPath = Path.Combine(_directory, "copied.db");
        await using (ControlServerDbContext context = FileContext(migratedPath))
        {
            await context.Database.MigrateAsync(Token);
        }
        await using (ControlServerDbContext context = FileContext(copiedPath))
        {
            await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
        }

        await using SqliteConnection migrated = await OpenFileAsync(migratedPath);
        await using SqliteConnection copied = await OpenFileAsync(copiedPath);
        Assert.Equal("wal", Scalar(migrated, "PRAGMA journal_mode;"));
        Assert.Equal("wal", Scalar(copied, "PRAGMA journal_mode;"));
        Assert.Equal(Describe(migrated), Describe(copied));
    }

    [Fact]
    public async Task ACopyHasEveryMigrationAppliedAndNonePending()
    {
        await using SqliteConnection connection = await OpenMemoryAsync();
        await using ControlServerDbContext context = Context(connection);
        await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);

        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync(Token));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Token));
    }

    [Fact]
    public async Task WritesToACopyNeverReachTheTemplate()
    {
        Assert.True(await MigratedDatabaseTemplate.TemplateIsQueryOnlyAsync());

        await using SqliteConnection written = await CopyAsync();
        string before = Describe(written);
        Mark(written, "cs553-written");
        Assert.NotEqual(before, Describe(written));

        await using SqliteConnection next = await CopyAsync();
        Assert.Equal(before, Describe(next));
    }

    [Fact]
    public async Task CopiesMadeAtOnceSeeOnlyTheirOwnRows()
    {
        // In memory and on disk, made concurrently the way parallel test classes make them.
        Task<SqliteConnection>[] making =
        [
            .. Enumerable.Range(0, 6).Select(_ => Task.Run(CopyAsync)),
            .. Enumerable.Range(0, 2).Select(n => Task.Run(() => CopyToFileAsync(Path.Combine(_directory, $"parallel-{n}.db")))),
        ];
        SqliteConnection[] copies = await Task.WhenAll(making);
        try
        {
            for (int n = 0; n < copies.Length; n++)
            {
                Mark(copies[n], $"cs553-copy-{n}");
            }

            for (int n = 0; n < copies.Length; n++)
            {
                Assert.Equal([$"cs553-copy-{n}"], Marks(copies[n]));
            }
        }
        finally
        {
            foreach (SqliteConnection copy in copies)
            {
                await copy.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ADatabaseThatAlreadyHasASchemaIsRefusedRatherThanOverwritten()
    {
        await using SqliteConnection connection = await OpenMemoryAsync();
        Scalar(connection, "CREATE TABLE Existing (Id INTEGER); SELECT 1;");
        await using ControlServerDbContext context = Context(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MigratedDatabaseTemplate.ApplyAsync(context.Database, Token));
        Assert.Equal("1", Scalar(connection, "SELECT count(*) FROM sqlite_master;"));
    }

    /// <summary>
    /// A test about the migrations runs them: the template is migrated by the same code, so a wrong migration would be wrong
    /// in every copy too, and only these tests would say so.
    /// </summary>
    [Fact]
    public void NoMigrationTestStartsFromATemplateCopy()
    {
        string tests = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "tests", "ControlServer.Tests");
        string[] migrationTests =
        [
            .. Directory.GetFiles(tests, "*Migration*.cs")
                .Where(path => !Path.GetFileName(path).StartsWith(nameof(MigratedDatabaseTemplate), StringComparison.Ordinal)),
            Path.Combine(tests, "AuditDatabaseImmutabilityTests.cs"),
        ];
        Assert.True(migrationTests.Length >= 15, $"Found only {migrationTests.Length} migration test files under {tests}.");

        string[] copying =
        [
            .. migrationTests.Where(path =>
            {
                string text = File.ReadAllText(path);
                return text.Contains(nameof(MigratedDatabaseTemplate), StringComparison.Ordinal) ||
                       text.Contains("Batch7JourneyFixture.CreateAsync()", StringComparison.Ordinal);
            }).Select(Path.GetFileName)!,
        ];
        Assert.True(
            copying.Length == 0,
            "These migration tests start from a template copy instead of running the migrations; use "
            + "Batch7JourneyFixture.CreateMigratedForRealAsync() or MigrateAsync: " + string.Join(", ", copying));
    }

    private static async Task<SqliteConnection> CopyAsync()
    {
        SqliteConnection connection = await OpenMemoryAsync();
        await using ControlServerDbContext context = Context(connection);
        await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
        return connection;
    }

    private static async Task<SqliteConnection> CopyToFileAsync(string path)
    {
        await using (ControlServerDbContext context = FileContext(path))
        {
            await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);
        }
        return await OpenFileAsync(path);
    }

    /// <summary>A row only this copy should have: a migration id no migration carries.</summary>
    private static void Mark(SqliteConnection connection, string marker) =>
        Scalar(connection, $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('{marker}', 'cs553'); SELECT 1;");

    private static string[] Marks(SqliteConnection connection) =>
        [.. Rows(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" WHERE \"ProductVersion\" = 'cs553';")];

    /// <summary>
    /// Everything a migration leaves in the file that a fixture could observe: the file-level pragmas, every schema object's
    /// SQL, and every row of every table.
    /// </summary>
    private static string Describe(SqliteConnection connection)
    {
        List<string> lines = [];
        foreach (string pragma in (string[])["user_version", "application_id", "page_size", "auto_vacuum", "encoding", "foreign_keys"])
        {
            lines.Add($"pragma {pragma} = {Scalar(connection, $"PRAGMA {pragma};")}");
        }
        lines.AddRange(Rows(connection, "SELECT type || ' ' || name || ' ON ' || tbl_name || ': ' || coalesce(sql, '') FROM sqlite_master ORDER BY type, name;"));
        foreach (string table in Rows(connection, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"))
        {
            string[] rows = [.. RowsOf(connection, table).Order(StringComparer.Ordinal)];
            lines.Add($"table {table}: {rows.Length} rows");
            lines.AddRange(rows.Select(row => $"  {row}"));
        }
        return string.Join('\n', lines);
    }

    private static List<string> RowsOf(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table}\";";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount)
                .Select(i => $"{reader.GetName(i)}={(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))}")));
        }
        return rows;
    }

    private static List<string> Rows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> rows = [];
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async Task<SqliteConnection> OpenMemoryAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(Token);
        return connection;
    }

    private static async Task<SqliteConnection> OpenFileAsync(string path)
    {
        SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(path));
        await connection.OpenAsync(Token);
        return connection;
    }

    private static ControlServerDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);

    private static ControlServerDbContext FileContext(string path) =>
        new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(ControlServerSqlite.ForDatabaseFile(path)).Options);
}
