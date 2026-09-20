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
/// <b>批次7-06（control-server#211）之后，这批基线钉的不再是「什么都没变」，而是「只有这两列变了」。</b>那一票让
/// <c>JourneyStops.Status</c> 与 <c>JourneyDemands.Status</c> 真的动起来——当前停靠与装货进度从此是落库的状态，
/// 不再从阶段反推——所以基线在这两列上必然变，那是它要的变化。**它仍然不是「重录来变绿」**：重录之后逐行比对过，
/// 全部差异只有三种形状，<c>Status='PENDING'→'COMPLETED'</c>、<c>'PENDING_LOAD'→'UNLOADED'</c> 与
/// <c>'PENDING_LOAD'→'TERMINATED'</c>，七张表的其余每一列、以及出站消息的确认时刻，逐字未动
/// （<c>evidence/b7-06/green/03-zero-change-pin-diff.txt</c>）。判别力正在这里：差异越出这三种形状，就是改坏了别的东西。
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
