using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Every <c>SafetyStateSnapshot</c> and <c>SafetyStateChanged</c> in the inbox, newest received first, streamed so that a reader
/// can stop at the row it wants (control-server#597).
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the one the readers picked by before, ties included.</b> They loaded both types whole -- the store walks
/// <c>IX_ProtocolInbox_MessageType</c>, so rows came back by type (<c>SafetyStateChanged</c> sorts first) and within a type in
/// the order they were written -- and then took the first match, after a stable sort by receive time where they sorted at all.
/// Receive time descending, then message type, then rowid is that same order, so on the same tick the row taken is the one taken
/// before. <c>SafetyStateLatestFirstReadTests</c> pins it on the code before this change.
/// </para>
/// <para>
/// <b>Why raw SQL.</b> The tie-break is the rowid, which the model does not map. And the two types are read as the two arms of a
/// <c>UNION ALL</c> because SQLite merges two arms that each come out in order (<c>MERGE (UNION ALL)</c>) and starts answering at
/// once, where <c>MessageType IN (...) ORDER BY ...</c> sorts every safety message before the first row comes back. Each arm
/// comes out in order straight from the <c>(MessageType, ReceivedAtUtcTicks)</c> index, sorting only rows that share a tick.
/// Plans for both index sets are in <c>evidence/cs597/</c>.
/// </para>
/// <para>
/// The inbox has no vehicle or generation column -- both are in the envelope -- so the reader still parses what it reads and
/// reads every vehicle's rows newer than the one it stops at.
/// </para>
/// </remarks>
internal static class SafetyMessagesNewestFirst
{
    private const string Sql =
        """
        SELECT "MessageId", "ContentHash", "FirstResponseJson", "MessageType", "ReceivedAt", "ReceivedAtUtcTicks", "RequestJson"
        FROM (
            SELECT rowid AS "InsertionOrder", * FROM "ProtocolInbox" WHERE "MessageType" = 'SafetyStateChanged'
            UNION ALL
            SELECT rowid AS "InsertionOrder", * FROM "ProtocolInbox" WHERE "MessageType" = 'SafetyStateSnapshot'
        )
        ORDER BY "ReceivedAtUtcTicks" DESC, "MessageType", "InsertionOrder"
        """;

    public static IAsyncEnumerable<ProtocolInboxRow> Read(ControlServerDbContext dbContext) =>
        dbContext.ProtocolInbox.FromSqlRaw(Sql).AsNoTracking().AsAsyncEnumerable();
}
