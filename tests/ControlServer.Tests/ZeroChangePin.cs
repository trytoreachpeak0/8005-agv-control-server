using System.Runtime.CompilerServices;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-02（control-server#207）：行为不变的重构，其前后对外可见的东西逐字钉死——单需求旅程在每一条终结路径上终结之后
/// 库里落下的状态，以及两个看板端点对同一库的输出。
/// </summary>
/// <remarks>
/// <para>
/// 本票把「结束本站即结束整趟」拆成「终结需求」与「本旅程没有剩余需求时关闭旅程」两步，租约改为最后一条需求终结时才放，
/// 「需求找旅程」改经从属需求表。这些在单需求下必须一字不变：终结状态钉的是需求、租约、用途占有、订单占用、旅程、从属需求、
/// 停靠七张表的每一列，加上出站消息的确认时刻（录入请求与装货命令在终结时被结算）。
/// </para>
/// <para>
/// 期望值是集成分支自身上跑出的结果，存在 <c>ZeroChangePins/</c>：初版取自 <c>fp/v2-impl@47ae7376</c>（批次7-01 合入后、
/// 本票动产品代码之前），随集成分支前移重录过一次，取自 <c>fp/v2-impl@15807555</c>——把本票的测试文件原样搬到集成分支的
/// 工作树上跑出来，再搬回来。**重录只有这一种做法**：在集成分支上录，不在本票分支上录；红了就是行为变了，不能重录来变绿。
/// 那次重录唯一的差别是 <c>JourneyRuntimes</c> 多了 cs#228 的新列 <c>AreaEndAdmissionRevokedSince=NULL</c>，其余逐字未动。
/// </para>
/// <para>
/// <b>批次7-06（control-server#211）之后，这批基线钉的不再是「什么都没变」，而是「只有 Status 变了」。</b>那一票让
/// <c>JourneyStops.Status</c> 与 <c>JourneyDemands.Status</c> 真的动起来——当前停靠与装货进度从此是落库的状态，
/// 不再从阶段反推——所以基线在这两列上必然变，那是它要的变化。<b>它仍然不是「重录来变绿」</b>，而这一点由
/// 与<b>集成分支顶端</b>（<c>fp/v2-impl@10175635</c>）的逐字段比对撑着，不是与本票自己的上一个提交比：
/// <c>evidence/b7-06/green/04-zero-change-pin-vs-integration-tip.txt</c>。
/// </para>
/// <para>
/// 那份比对的全部差异是两类。<b>同一行内被改写 12 处，三种形状，全部是 <c>Status</c></b>：
/// <c>JourneyDemands</c> 的 <c>'PENDING_LOAD'→'TERMINATED'</c> 9 处、<c>'PENDING_LOAD'→'UNLOADED'</c> 1 处，
/// <c>JourneyStops</c> 的 <c>'PENDING'→'COMPLETED'</c> 2 处；每一处都只变了一列，这不是读出来的，
/// 是统计脚本对「一行里变了不止一列」单独报出来、结果为零。<b>纯新增 6 行</b>，全部是 <c>JourneyStops</c>：
/// 三份 <c>commanded-ending-*</c> 各多两行，因为手写旅程行的夹具改用了 <c>JourneyMembershipSeed.Seed</c>。
/// <b>删除 0 行。</b>七张表的其余每一列、以及出站消息的确认时刻，逐字未动。判别力正在这里：差异越出这两类，
/// 就是改坏了别的东西。
/// </para>
/// <para>
/// <b>这几个数字数错过一次，值得记着怎么错的。</b>第一版统计把「行数不等」的文件整份跳过逐字段比对，于是三份
/// <c>commanded-ending-*</c> 里各一处 <c>Status</c> 改写没被算进去，报出来是 9 处改写而不是 12 处。
/// 对不上的是总行数：证据文件里 +18/−12 行，而 9 处改写加 6 行新增只能是 +15/−9。<b>先算出该是多少，再去对</b>——
/// 否则一个漏了三处的统计看上去和对的一样。
/// </para>
/// <para>
/// <b>批次7-12（control-server#217）给两份阻断旅程看板基线加了三个字段，其余逐字未动。</b>那一票让阻断旅程端点每一行多给
/// <c>blockReasonDescription</c>、<c>journeyId</c>、<c>demands</c>（只加字段、不改名不删字段），所以
/// <c>dashboard-blocked-journeys-*</c> 两份在这三个字段上必然变。判据是「把新输出里这三个字段删掉，与旧基线按键序逐字相同」：
/// 两份各 3 行、7 行全部成立，删掉的是 9 个与 21 个字段，值只有 <c>null</c>、<c>[]</c> 与各行自己的旅程 id
/// （<c>evidence/cs217/green/01-dashboard-pin-rerecord-additions-only.txt</c>，在工作区证据目录）。期待动作超时的两份基线不受影响。
/// </para>
/// <para>
/// <b>control-server#273 给十份终结状态基线的 <c>JourneyRuntimes</c> 行加了四列，其余逐字未动。</b>那一票加了等人起点
/// <c>WaitingSince</c> 与等人监看的三列（<c>WaitingBatteryPercent</c>、<c>WaitingBatteryObservedAt</c>、<c>WaitingWarnedAt</c>）。
/// 判据与 cs#228、cs#217 同一个形状：把新输出里这四个字段删掉，与<b>集成分支上的</b>旧基线（<c>fp/v2-impl@8ec088b1</c>）逐字相同。
/// 十份都成立，每份恰好删掉四个字段，而且都在 <c>JourneyRuntimes</c> 那一行上。十份的 <c>WaitingSince</c> 与
/// <c>WaitingWarnedAt</c> 全为 <c>NULL</c>——终结之后的旅程不在等人；有七份记下了电量 80（夹具的默认值），三份
/// <c>commanded-ending-*</c> 的电量两列为 <c>NULL</c>。看板四份基线不受影响。
/// </para>
/// <para>
/// <b>control-server#323 给十份终结状态基线的发件箱一节各加了三行，其余逐字未动。</b>那一票让旅程收尾时给车发三张收尾快照
/// （空清单、空计划、不带旅程的业务状态），它们与收尾同一次保存落库、此刻还没被确认。判据：与<b>集成分支上的</b>旧基线
/// （<c>fp/v2-impl@a98ae9be</c>）相比，删除 0 行，新增恰好 3 行，三行的类型恰好是那三种快照、全部 <c>AcknowledgedAt=NULL</c>。
/// 十份都成立；七张表一列未动。同票重录的六份 <c>WirePins/</c> 另有判据，见 <c>evidence/cs323/green/01-pin-rerecord-vs-integration-tip.txt</c>。
/// </para>
/// <para>
/// <b>control-server#339 给十份终结状态基线的 <c>JourneyStops</c> 行各加了一列 <c>WorklistRefills=0</c>，其余逐字未动。</b>那一票加了
/// 「本停靠的清单因离站期限重填多发了几版」，这十条路径都没有断线重连，所以全为 0。判据与 cs#273 同一个形状：把新基线里的
/// <c>|WorklistRefills=0</c> 删掉，与<b>集成分支上的</b>旧基线（<c>fp/v2-impl@012c31b2</c>）逐字相同——十四份全部成立（没有
/// <c>JourneyStops</c> 行的四份看板基线本来就没动）。先算出该是多少再去对：十份各两行停靠，应有 20 处，实数 20 处
/// （<c>evidence/l1/20260923-cs339-pins/SUMMARY.md</c>）。列插在 <c>Status</c> 之前，是 <c>EnsureCreated</c> 按属性声明顺序建表的结果。
/// </para>
/// </remarks>
internal static class ZeroChangePin
{
    private static readonly string[] Tables =
    [
        "AcceptedDemands",
        "VehicleDispatchLeases",
        "VehiclePurposeClaims",
        "OrderIntents",
        "JourneyRuntimes",
        "JourneyDemands",
        "JourneyStops",
    ];

    /// <summary>
    /// Columns whose value the test's own inputs or the wall clock decide (a random create attempt id, the moment the RIoT
    /// double answered, the message id a test generated for the envelope it sent). Only whether they are set is pinned.
    /// </summary>
    private static readonly HashSet<string> SetOrNotOnly = new(StringComparer.Ordinal)
    {
        "ConsumedSafetyResultMessageId",
        "ConsumedSublotMessageId",
        "CreateAttemptId",
        "CreateDispatchArmedAt",
        "LastCreateOutcomeAt",
        "LastCreateReceiptJson",
        "LastReconciliationOutcomeAt",
        "LastReconciliationReceiptJson",
    };

    internal static async Task AssertMatchesAsync(
        ControlServerDbContext context,
        string pinName,
        [CallerFilePath] string sourceFile = "") =>
        AssertTextMatches(await CaptureAsync(context), pinName, sourceFile);

    /// <summary>Compares <paramref name="actual"/> with the pin file <c>ZeroChangePins/&lt;pinName&gt;.txt</c>.</summary>
    internal static void AssertTextMatches(string actual, string pinName, [CallerFilePath] string sourceFile = "")
    {
        actual = actual.ReplaceLineEndings("\n");
        string path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "ZeroChangePins", pinName + ".txt");
        Assert.True(File.Exists(path), $"Pin '{pinName}' has no expected state at {path}. Actual:\n{actual}");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    internal static async Task<string> CaptureAsync(ControlServerDbContext context)
    {
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        List<string> lines = [];
        foreach (string table in Tables)
        {
            lines.Add($"## {table}");
            lines.AddRange(await DumpAsync(connection, table));
        }
        lines.Add("## ProtocolOutbox (MessageId, MessageType, AcknowledgedAt)");
        await using SqliteCommand outbox = connection.CreateCommand();
        outbox.CommandText =
            "SELECT 'MessageId=' || quote(MessageId) || '|MessageType=' || quote(MessageType) || " +
            "'|AcknowledgedAt=' || quote(AcknowledgedAt) FROM ProtocolOutbox";
        List<string> rows = [];
        await using (SqliteDataReader reader = await outbox.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                rows.Add(reader.GetString(0));
            }
        }
        rows.Sort(StringComparer.Ordinal);
        lines.AddRange(rows);
        return string.Join('\n', lines) + "\n";
    }

    /// <summary><see cref="Batch7JourneyFixture.DumpAsync"/>, with <see cref="SetOrNotOnly"/> reduced to set or NULL.</summary>
    private static async Task<string[]> DumpAsync(SqliteConnection connection, string table)
    {
        List<string> columns = [];
        await using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info('{table}')";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = $"SELECT {string.Join(" || '|' || ", columns.Select(Column))} FROM \"{table}\"";
        List<string> rows = [];
        await using SqliteDataReader rowsReader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rowsReader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(rowsReader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];

        static string Column(string name) => SetOrNotOnly.Contains(name)
            ? $"'{name}=' || CASE WHEN \"{name}\" IS NULL THEN 'NULL' ELSE '<set>' END"
            : $"'{name}=' || quote(\"{name}\")";
    }
}
