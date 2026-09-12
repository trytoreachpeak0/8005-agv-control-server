using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// REQ-0271：业务审计与管理员审计在期限内必须可查询、可导出（规格：CSV 与 JSON 两种）。
/// </summary>
public sealed class AuditExportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    /// <summary>两条流各导各的，按记录时刻排序——写入顺序与记录时刻故意颠倒。</summary>
    [Fact]
    public async Task EachStreamExportsOnlyItsOwnRecordsInTheOrderTheyWereRecorded()
    {
        await using ExportFixture fixture = await ExportFixture.CreateAsync();
        await fixture.WriteBusinessAsync("BUSINESS_LATER", T0.AddMinutes(2));
        await fixture.WriteBusinessAsync("BUSINESS_EARLIER", T0);
        await fixture.WriteAdministratorAsync("ADMINISTRATOR_ONLY", T0.AddMinutes(1), "SYSTEM_ADMINISTRATOR");

        IReadOnlyList<AuditExportRecord> business = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Business, null, null, TestContext.Current.CancellationToken);
        IReadOnlyList<AuditExportRecord> administrator = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Administrator, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["BUSINESS_EARLIER", "BUSINESS_LATER"], business.Select(record => record.Action));
        Assert.All(business, record => Assert.Null(record.ClaimedAdministratorRole));
        AuditExportRecord only = Assert.Single(administrator);
        Assert.Equal("ADMINISTRATOR_ONLY", only.Action);
        Assert.Equal("SYSTEM_ADMINISTRATOR", only.ClaimedAdministratorRole);
    }

    /// <summary>
    /// 一格里有逗号、双引号、换行，仍是一格；文件以 UTF-8 BOM 开头，否则 Excel 按 ANSI 打开，中文成乱码。
    /// </summary>
    [Fact]
    public async Task CsvKeepsAFieldWithCommasQuotesAndLineBreaksInOneCellAndOpensWithAByteOrderMark()
    {
        await using ExportFixture fixture = await ExportFixture.CreateAsync();
        const string detail = "{\n  \"note\": \"第一行, 带逗号\",\n  \"quoted\": \"say \\\"hi\\\"\"\n}";
        await fixture.WriteAdministratorAsync("ACTION_WITH_AWKWARD_DETAIL", T0, "MAINTENANCE_ADMINISTRATOR", detail);

        IReadOnlyList<AuditExportRecord> records = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Administrator, null, null, TestContext.Current.CancellationToken);
        byte[] bytes = AuditExport.Render(
            AuditExportFormat.Csv, AuditTrail.Administrator, records, null, null, T0.AddHours(1));

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        List<List<string>> rows = ParseCsv(Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal(2, rows.Count);
        Assert.Equal(rows[0].Count, rows[1].Count);
        Dictionary<string, string> cells = rows[0].Zip(rows[1]).ToDictionary(pair => pair.First, pair => pair.Second);
        Assert.Equal(detail, cells["detailJson"]);
        Assert.Equal("MAINTENANCE_ADMINISTRATOR", cells["claimedAdministratorRole"]);
        Assert.Equal("ActiveSlotConfiguration", cells["objectKind"]);
        Assert.Equal(T0, DateTimeOffset.Parse(cells["recordedAt"], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>业务流的 CSV 没有「声称角色」那一列：两条流在需求里是分开的两份记录。</summary>
    [Fact]
    public async Task TheBusinessCsvHasNoClaimedAdministratorRoleColumn()
    {
        await using ExportFixture fixture = await ExportFixture.CreateAsync();
        await fixture.WriteBusinessAsync("BUSINESS", T0);

        IReadOnlyList<AuditExportRecord> records = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Business, null, null, TestContext.Current.CancellationToken);
        byte[] bytes = AuditExport.Render(
            AuditExportFormat.Csv, AuditTrail.Business, records, null, null, T0);

        List<string> header = ParseCsv(Encoding.UTF8.GetString(bytes[3..]))[0];
        Assert.DoesNotContain("claimedAdministratorRole", header);
        Assert.Contains("detailJson", header);
    }

    /// <summary>JSON 给下一次审计工具机械读回，所以逐字段读回来要一样，没有 BOM。</summary>
    [Fact]
    public async Task JsonReadsBackFieldForFieldWithoutAByteOrderMark()
    {
        await using ExportFixture fixture = await ExportFixture.CreateAsync();
        const string detail = "{\"reasonCode\":\"SLOT_CONFIGURATION_FINGERPRINT_MISMATCH\",\"note\":\"指纹不符\"}";
        await fixture.WriteAdministratorAsync("ADMINISTRATOR", T0, "SYSTEM_ADMINISTRATOR", detail);

        IReadOnlyList<AuditExportRecord> records = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Administrator, null, null, TestContext.Current.CancellationToken);
        byte[] bytes = AuditExport.Render(
            AuditExportFormat.Json, AuditTrail.Administrator, records, T0.AddDays(-1), null, T0.AddHours(1));

        Assert.NotEqual(Encoding.UTF8.GetPreamble(), bytes[..3]);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        Assert.Equal("AdministratorActionAuditRecord", root.GetProperty("stream").GetString());
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("until").ValueKind);
        JsonElement record = Assert.Single(root.GetProperty("records").EnumerateArray().ToArray());
        Assert.Equal(records[0].AuditRecordId, record.GetProperty("auditRecordId").GetString());
        Assert.Equal(detail, record.GetProperty("detailJson").GetString());
        Assert.Equal("Failed", record.GetProperty("outcome").GetString());
        Assert.Equal(T0, record.GetProperty("recordedAt").GetDateTimeOffset());
    }

    /// <summary>窗口左闭右开：相邻两个窗口拼起来不重不漏。</summary>
    [Fact]
    public async Task TheWindowIncludesItsStartAndExcludesItsEnd()
    {
        await using ExportFixture fixture = await ExportFixture.CreateAsync();
        await fixture.WriteBusinessAsync("BEFORE", T0.AddTicks(-1));
        await fixture.WriteBusinessAsync("AT_START", T0);
        await fixture.WriteBusinessAsync("AT_END", T0.AddHours(1));

        IReadOnlyList<AuditExportRecord> window = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Business, T0, T0.AddHours(1), TestContext.Current.CancellationToken);
        IReadOnlyList<AuditExportRecord> next = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Business, T0.AddHours(1), null, TestContext.Current.CancellationToken);

        Assert.Equal(["AT_START"], window.Select(record => record.Action));
        Assert.Equal(["AT_END"], next.Select(record => record.Action));
        await Assert.ThrowsAsync<ArgumentException>(() => AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Business, T0.AddHours(1), T0, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// 导出读全集、不截断，也不删任何东西——一次悄悄只导出前 N 条的导出，与容量限制导致提前删除是同一种后果。
    /// </summary>
    [Fact]
    public async Task ExportingReadsEveryRetainedRecordAndDeletesNothing()
    {
        await using ExportFixture fixture = await ExportFixture.CreateAsync();
        const int written = 1200;
        for (int index = 0; index < written; index++)
        {
            fixture.AddBusiness($"BULK_{index:D4}", T0.AddSeconds(index));
        }
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<AuditExportRecord> records = await AuditExport.ReadAsync(
            fixture.Context, AuditTrail.Business, null, null, TestContext.Current.CancellationToken);
        byte[] csv = AuditExport.Render(AuditExportFormat.Csv, AuditTrail.Business, records, null, null, T0);

        Assert.Equal(written, records.Count);
        Assert.Equal("BULK_0000", records[0].Action);
        Assert.Equal($"BULK_{written - 1:D4}", records[^1].Action);
        Assert.Equal(written + 1, ParseCsv(Encoding.UTF8.GetString(csv[3..])).Count);
        Assert.Equal(written, await fixture.Context.Set<BusinessAuditRecordRow>()
            .CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>测试自己带一个最小的 RFC 4180 读取器：用被测代码去读被测代码写的东西，什么都证不了。</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        List<List<string>> rows = [];
        List<string> row = [];
        StringBuilder field = new();
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (quoted)
            {
                if (current == '"' && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (current == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(current);
                }
                continue;
            }
            switch (current)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r' when index + 1 < text.Length && text[index + 1] == '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    index++;
                    break;
                default:
                    field.Append(current);
                    break;
            }
        }
        Assert.False(quoted, "The CSV ended inside a quoted field.");
        Assert.Equal(0, field.Length);
        Assert.Empty(row);
        return rows;
    }

    private sealed class ExportFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private ExportFixture(SqliteConnection connection, ControlServerDbContext context, GovernanceStore store)
        {
            _connection = connection;
            Context = context;
            Store = store;
        }

        public ControlServerDbContext Context { get; }

        private GovernanceStore Store { get; }

        public static async Task<ExportFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            GovernanceStore store = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            return new ExportFixture(connection, context, store);
        }

        public Task<string> WriteBusinessAsync(string action, DateTimeOffset recordedAt) =>
            Store.WriteBusinessAsync(
                new GovernanceAuditEntry(
                    action, GovernedObjectKind.SlotModelVersion, "MODEL-1", 1,
                    GovernanceActionOutcome.Succeeded, "{}"),
                recordedAt,
                TestContext.Current.CancellationToken);

        public Task<string> WriteAdministratorAsync(
            string action,
            DateTimeOffset recordedAt,
            string claimedRole,
            string detailJson = "{}") =>
            Store.WriteAdministratorAsync(
                new GovernanceAuditEntry(
                    action, GovernedObjectKind.ActiveSlotConfiguration, "AGV-001", 2,
                    GovernanceActionOutcome.Failed, detailJson, SnapshotId: null,
                    ClaimedAdministratorRole: claimedRole),
                recordedAt,
                TestContext.Current.CancellationToken);

        /// <summary>批量场景直接加行、一次保存：逐条经 store 保存一千多次只会拖慢测试，证不出别的。</summary>
        public void AddBusiness(string action, DateTimeOffset recordedAt) =>
            Context.Set<BusinessAuditRecordRow>().Add(new BusinessAuditRecordRow
            {
                AuditRecordId = Guid.NewGuid().ToString("N"),
                RecordedAt = recordedAt,
                RecordedAtUtcTicks = recordedAt.UtcTicks,
                ActorIdentity = "deployment:8005-controlserver@test",
                ActorAttribution = AuditActorAttribution.NotAttributableToNaturalPerson,
                Action = action,
                ObjectKind = GovernedObjectKind.SlotModelVersion,
                ObjectId = "MODEL-1",
                Version = 1,
                Outcome = GovernanceActionOutcome.Succeeded,
                DetailJson = "{}"
            });

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
