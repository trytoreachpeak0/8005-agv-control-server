using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ControlServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>REQ-0271 分开点名的两条审计流。</summary>
public enum AuditTrail
{
    /// <summary><c>BusinessAuditRecord</c>。</summary>
    Business,

    /// <summary><c>AdministratorActionAuditRecord</c>。</summary>
    Administrator
}

/// <summary>
/// 导出格式。
/// </summary>
/// <remarks>
/// 规格定为 CSV 与 JSON 两种：CSV 因为审计导出的实际消费者是人和 Excel，JSON 因为它要能被下一次审计工具
/// 机械读回。不做 PDF——那需要一个排版层，而 REQ-0271 没有任何关于呈现的要求。
/// </remarks>
public enum AuditExportFormat
{
    Csv,
    Json
}

/// <summary>导出里的一条审计记录。两条流共用这一个形状，业务流的 <see cref="ClaimedAdministratorRole"/> 恒为空。</summary>
public sealed record AuditExportRecord(
    string AuditRecordId,
    DateTimeOffset RecordedAt,
    string ActorIdentity,
    string ActorAttribution,
    string? ClaimedAdministratorRole,
    string Action,
    GovernedObjectKind ObjectKind,
    string ObjectId,
    long? Version,
    GovernanceActionOutcome Outcome,
    string? SnapshotId,
    string DetailJson);

/// <summary>
/// REQ-0271：两条审计流在期限内必须可查询、可导出。
/// </summary>
/// <remarks>
/// <para>
/// <b>只读，不删、不截断。</b>导出读的是库里此刻还在的全部记录，没有页长上限——一次悄悄只导出前 N 条的
/// 导出，与容量限制导致提前删除是同一种后果：审计的人拿到的不是全集，而且不知道。
/// </para>
/// <para>
/// <b>按 UTC ticks 过滤与排序。</b>EF 对 SQLite 翻译不了 <see cref="DateTimeOffset"/> 的比较与排序；两列由
/// 同一次写入从同一个时刻赋值，见 <see cref="BusinessAuditRecordRow.RecordedAtUtcTicks"/>。
/// </para>
/// <para>
/// 入口在 <c>ControlServer.FieldOps</c> 的 <c>export-audit</c>，不是 HTTP：本期没有人员认证，一份审计导出
/// 不该挂在一个谁都能读的端点上，而受控运维工具要人在机器前刻意敲一次。
/// </para>
/// </remarks>
public static class AuditExport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // 审计里的 detailJson 常带中文与引号，转义成 \uXXXX 读起来像乱码，而这份文件首先是给人看的。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>流在需求文本里的名字，也是导出文件里写的名字。</summary>
    public static string StreamName(AuditTrail stream) => stream switch
    {
        AuditTrail.Business => "BusinessAuditRecord",
        AuditTrail.Administrator => "AdministratorActionAuditRecord",
        _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, null)
    };

    /// <summary>
    /// 读一条流在 [<paramref name="from"/>, <paramref name="until"/>) 之内的全部记录，按记录时刻排序。
    /// </summary>
    /// <remarks>两端都可省；省掉就是那一侧不设界。左闭右开，相邻两个窗口拼起来不重不漏。</remarks>
    public static async Task<IReadOnlyList<AuditExportRecord>> ReadAsync(
        ControlServerDbContext context,
        AuditTrail stream,
        DateTimeOffset? from,
        DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        long fromTicks = from?.UtcTicks ?? long.MinValue;
        long untilTicks = until?.UtcTicks ?? long.MaxValue;
        if (fromTicks > untilTicks)
        {
            throw new ArgumentException("The export window ends before it starts.", nameof(until));
        }

        if (stream == AuditTrail.Business)
        {
            return await context.Set<BusinessAuditRecordRow>().AsNoTracking()
                .Where(row => row.RecordedAtUtcTicks >= fromTicks && row.RecordedAtUtcTicks < untilTicks)
                .OrderBy(row => row.RecordedAtUtcTicks)
                .ThenBy(row => row.AuditRecordId)
                .Select(row => new AuditExportRecord(
                    row.AuditRecordId,
                    row.RecordedAt,
                    row.ActorIdentity,
                    row.ActorAttribution,
                    null,
                    row.Action,
                    row.ObjectKind,
                    row.ObjectId,
                    row.Version,
                    row.Outcome,
                    row.SnapshotId,
                    row.DetailJson))
                .ToArrayAsync(cancellationToken);
        }

        return await context.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.RecordedAtUtcTicks >= fromTicks && row.RecordedAtUtcTicks < untilTicks)
            .OrderBy(row => row.RecordedAtUtcTicks)
            .ThenBy(row => row.AuditRecordId)
            .Select(row => new AuditExportRecord(
                row.AuditRecordId,
                row.RecordedAt,
                row.ActorIdentity,
                row.ActorAttribution,
                row.ClaimedAdministratorRole,
                row.Action,
                row.ObjectKind,
                row.ObjectId,
                row.Version,
                row.Outcome,
                row.SnapshotId,
                row.DetailJson))
            .ToArrayAsync(cancellationToken);
    }

    /// <summary>渲染成要写进文件的字节。</summary>
    /// <remarks>
    /// CSV 带 UTF-8 BOM：没有它，Excel 按本机 ANSI 代码页打开，<c>detailJson</c> 里的中文会是乱码，而 CSV 的
    /// 读者正是 Excel。JSON 不带——机械读回的一方不需要，而且有些 JSON 解析器会把 BOM 当成非法首字符。
    /// </remarks>
    public static byte[] Render(
        AuditExportFormat format,
        AuditTrail stream,
        IReadOnlyList<AuditExportRecord> records,
        DateTimeOffset? from,
        DateTimeOffset? until,
        DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(records);

        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        return format switch
        {
            AuditExportFormat.Csv => [.. Encoding.UTF8.GetPreamble(), .. utf8.GetBytes(ToCsv(stream, records))],
            AuditExportFormat.Json => utf8.GetBytes(JsonSerializer.Serialize(
                new
                {
                    stream = StreamName(stream),
                    exportedAt,
                    from,
                    until,
                    count = records.Count,
                    records
                },
                Json)),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
        };
    }

    /// <summary>
    /// RFC 4180：逗号分隔、CRLF 换行、含逗号／双引号／换行的字段整格加双引号并把其中的双引号写成两个。
    /// </summary>
    /// <remarks>
    /// 管理员流比业务流多一列 <c>claimedAdministratorRole</c>。两条流在需求里就是分开的两份记录，业务流里
    /// 放一整列永远为空的「声称角色」，会让读表的人以为那一列本该有值。
    /// </remarks>
    private static string ToCsv(AuditTrail stream, IReadOnlyList<AuditExportRecord> records)
    {
        bool administrator = stream == AuditTrail.Administrator;
        StringBuilder csv = new();
        List<string> header =
        [
            "auditRecordId", "recordedAt", "actorIdentity", "actorAttribution"
        ];
        if (administrator)
        {
            header.Add("claimedAdministratorRole");
        }
        header.AddRange(["action", "objectKind", "objectId", "version", "outcome", "snapshotId", "detailJson"]);
        AppendRow(csv, header);

        foreach (AuditExportRecord record in records)
        {
            List<string?> row =
            [
                record.AuditRecordId,
                record.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
                record.ActorIdentity,
                record.ActorAttribution
            ];
            if (administrator)
            {
                row.Add(record.ClaimedAdministratorRole);
            }
            row.AddRange(
            [
                record.Action,
                record.ObjectKind.ToString(),
                record.ObjectId,
                record.Version?.ToString(CultureInfo.InvariantCulture),
                record.Outcome.ToString(),
                record.SnapshotId,
                record.DetailJson
            ]);
            AppendRow(csv, row);
        }
        return csv.ToString();
    }

    private static void AppendRow(StringBuilder csv, IEnumerable<string?> fields)
    {
        csv.AppendJoin(',', fields.Select(Field)).Append("\r\n");
    }

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
