namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// A <see cref="SerializedSqliteConnection"/> whose open failed while it was left holding a native connection another owner
/// may still be using (control-server#779 review M1). It stays <see cref="System.Data.ConnectionState.Broken"/>: nothing may
/// run on it, and closing it leaves that native connection alone. Not transient: the context it belongs to has to go.
/// </summary>
public sealed class SqliteConnectionPoisonedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
