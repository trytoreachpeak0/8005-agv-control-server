using ControlServer.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Takes the lock bound to the server's database file before anything touches the database, and refuses to start when another
/// process holds it (control-server#473; <see cref="ControlServerDatabaseLock"/>).
/// </summary>
/// <remarks>
/// <para>
/// The running server holds the lock for the life of its process, so FieldOps' direct database write, which takes the same lock,
/// cannot slip in between its probe of the server and its write. Two servers on one database are refused for the same reason:
/// each would overwrite the other's state.
/// </para>
/// <para>
/// <b>It waits <see cref="Wait"/> before refusing.</b> A service restart can start the new process while the old one is still
/// finishing after the service manager reported it stopped, and a FieldOps write holds the lock for a few seconds. A second
/// instance genuinely using the database holds it for as long as it runs, and is still refused. The wait stays well under the
/// service manager's 30 second start timeout.
/// </para>
/// <para>
/// <c>--import-package-capacity</c> does not take it: that command is run beside the live server by design (control-server#87)
/// and exits before the host binds anything.
/// </para>
/// </remarks>
public static class DatabaseLockStartup
{
    public const string ReasonCode = "DATABASE_IN_USE";

    /// <summary>The lock file cannot be opened at all (permissions, read-only, ...), as distinct from someone holding it.</summary>
    public const string UnusableReasonCode = "DATABASE_LOCK_FILE_UNUSABLE";

    /// <summary>How long a start waits for the lock before refusing.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    private static readonly Action<ILogger, string, int, Exception?> Waiting =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(9404, "DatabaseLockWaiting"),
            "Another process holds the lock on database {Database}; waiting up to {Seconds} s for it to be released.");

    private static readonly Action<ILogger, string, string, Exception?> Refused =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(9405, "DatabaseLockStartupRefused"),
            "Startup refused: {ReasonCode} {Detail}");

    private static readonly Action<ILogger, string, string, Exception?> MigrationRefused =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(9406, "DatabaseLockMigrationRefused"),
            "Migration refused: {ReasonCode} {Detail}");

    /// <summary>The lock on <paramref name="databasePath"/>; the caller holds it until the process ends.</summary>
    /// <param name="migrateOnly"><c>--migrate-only</c>: a refusal says the migration was refused, not the start.</param>
    public static async Task<ControlServerDatabaseLock> AcquireAsync(
        IServiceProvider services, string databasePath, bool migrateOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ILogger logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(DatabaseLockStartup).FullName!)
            ?? NullLogger.Instance;
        string database = Path.GetFullPath(databasePath);
        ControlServerDatabaseLock? acquired;
        try
        {
            acquired = await ControlServerDatabaseLock.AcquireAsync(
                    database,
                    Wait,
                    RetryInterval,
                    () => Waiting(logger, database, (int)Wait.TotalSeconds, null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ControlServerDatabaseLockException unusable)
        {
            throw Refuse(logger, migrateOnly, UnusableReasonCode, unusable.Message, unusable);
        }
        return acquired ?? throw Refuse(
            logger, migrateOnly, ReasonCode, ControlServerDatabaseLock.ServerRefusal(database, migrateOnly), null);
    }

    private static InvalidOperationException Refuse(
        ILogger logger, bool migrateOnly, string reasonCode, string detail, Exception? cause)
    {
        (migrateOnly ? MigrationRefused : Refused)(logger, reasonCode, detail, null);
        return new InvalidOperationException($"{reasonCode}: {detail}", cause);
    }
}
