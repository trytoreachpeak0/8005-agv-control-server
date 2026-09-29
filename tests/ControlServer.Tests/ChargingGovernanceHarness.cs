using System.Data.Common;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 名册与策略导入（control-server#400）的测试辅助。测试库与 26 号图站点目录借 <see cref="WaitingPointImportHarness"/>：
/// 站 211「充电点1」、212「充电准备点1」、214～216 等待点、12 机台站、305 派工待送取货站。
/// </summary>
internal static class ChargingGovernanceHarness
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    internal static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static GovernanceStore Governance(ControlServerDbContext context) =>
        new(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    internal static ChargerRosterImportService RosterService(ControlServerDbContext context)
    {
        GovernanceStore governance = Governance(context);
        GovernedConfigurationPublisher publisher = new(governance, governance);
        return new ChargerRosterImportService(
            new ChargerRosterStore(context, publisher),
            new WaitingPointRegistry(context, publisher),
            new StationExclusivityStore(context),
            new ChargingGovernanceFacts(context, new TaskTypeStationBindingStore(context, publisher)),
            governance);
    }

    internal static ChargingPolicyGovernanceService PolicyService(ControlServerDbContext context)
    {
        GovernanceStore governance = Governance(context);
        GovernedConfigurationPublisher publisher = new(governance, governance);
        return new ChargingPolicyGovernanceService(
            new ChargingPolicyStore(context, publisher),
            new ChargingGovernanceFacts(context, new TaskTypeStationBindingStore(context, publisher)),
            governance);
    }

    internal static ChargingPolicyStore PolicyStore(ControlServerDbContext context)
    {
        GovernanceStore governance = Governance(context);
        return new ChargingPolicyStore(context, new GovernedConfigurationPublisher(governance, governance));
    }

    /// <summary>26 号图站 211 的名册文件，与 <c>docs/field/charger-roster/</c> 那份同形。</summary>
    internal static string Roster211(params string[] vehicleScope) => RosterFile(Charger(211, "充电点1", 212, 212, vehicleScope));

    internal static string EmptyRoster() => RosterFile();

    internal static object Charger(int stationId, string stationName, int? entry = null, int? exit = null, string[]? scope = null, int mapId = 26) =>
        new { mapId, stationId, stationName, entryStationId = entry, exitStationId = exit, vehicleScope = scope ?? [] };

    internal static string RosterFile(params object[] chargers) =>
        JsonSerializer.Serialize(new
        {
            approvedBy = "Zhengyu Shao",
            approvalBasis = "test: charger roster",
            changeNote = (string?)null,
            chargers
        });

    /// <summary>一次名册导入，在自己的上下文与写事务里，像 FieldOps 每跑一次是一个新进程那样。</summary>
    internal static async Task<ChargerRosterImportResult> ImportRosterAsync(
        this WaitingPointImportHarness harness,
        string file,
        bool dryRun = false,
        IReadOnlyList<string>? fleet = null,
        RiotMapStationCatalogSnapshot? catalog = null,
        int mapId = WaitingPointImportHarness.Map)
    {
        await using ControlServerDbContext context = harness.Open();
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        ChargerRosterImportResult result = await RosterService(context).ImportAsync(
            new ChargerRosterImportRequest(file, mapId, fleet ?? WaitingPointImportHarness.Fleet, catalog ?? WaitingPointImportHarness.Catalog()),
            dryRun,
            Now,
            Token);
        if (!dryRun && result.Outcome != ChargerRosterImportOutcome.Rejected)
        {
            await transaction.CommitAsync(Token);
        }
        return result;
    }

    internal static string PolicyFile(
        int margin = 30,
        int entry = 30,
        int completion = 80,
        int consumption = 0,
        int stabilization = 180,
        int window = 600,
        int increase = 3,
        string[]? scope = null,
        string? changeNote = "test policy") =>
        JsonSerializer.Serialize(new
        {
            minimumPostTaskBatteryMarginPercent = margin,
            mandatoryChargeEntryThresholdPercent = entry,
            chargingCompletionThresholdPercent = completion,
            estimatedTaskConsumptionPercent = consumption,
            progressStabilizationSeconds = stabilization,
            progressObservationWindowSeconds = window,
            progressMinimumIncreasePercent = increase,
            vehicleScope = scope ?? [],
            changeNote
        });

    internal static async Task<ChargingPolicyImportResult> ImportPolicyAsync(
        this WaitingPointImportHarness harness, string file, bool dryRun = false, IReadOnlyList<string>? fleet = null)
    {
        await using ControlServerDbContext context = harness.Open();
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        ChargingPolicyImportResult result = await PolicyService(context).ImportAsync(
            file, fleet ?? WaitingPointImportHarness.Fleet, dryRun, Now, Token);
        if (!dryRun && result.Outcome == ChargingPolicyOperationOutcome.Accepted)
        {
            await transaction.CommitAsync(Token);
        }
        return result;
    }

    internal static async Task<ChargingPolicyApprovalResult> ApprovePolicyAsync(
        this WaitingPointImportHarness harness, long version, string source = ChargingPolicyApprovalSources.Field)
    {
        await using ControlServerDbContext context = harness.Open();
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        ChargingPolicyApprovalResult result = await PolicyService(context).ApproveAsync(
            version, "Zhengyu Shao", "PRODUCT_OWNER", "test: battery specification", source, Now, Token);
        if (result.Outcome == ChargingPolicyOperationOutcome.Accepted)
        {
            await transaction.CommitAsync(Token);
        }
        return result;
    }

    internal static async Task<ChargingPolicyActivationResult> ActivatePolicyAsync(
        this WaitingPointImportHarness harness,
        long version,
        bool allowNonFieldApproval = false,
        bool dryRun = false,
        IReadOnlyList<string>? fleet = null)
    {
        await using ControlServerDbContext context = harness.Open();
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        ChargingPolicyActivationResult result = await PolicyService(context).ActivateAsync(
            version, "Zhengyu Shao", fleet ?? WaitingPointImportHarness.Fleet, allowNonFieldApproval, dryRun, Now, Token);
        if (!dryRun && result.Outcome == ChargingPolicyOperationOutcome.Accepted)
        {
            await transaction.CommitAsync(Token);
        }
        return result;
    }

    /// <summary>一个进行中的充电周期：周期行、用途占有与 <c>CHARGER</c> 预占，经 control-server#399 的存储造出来。</summary>
    internal static async Task StartCycleAsync(this WaitingPointImportHarness harness, string cycleId, string vehicleKey, long rosterVersion, long policyVersion)
    {
        await using ControlServerDbContext context = harness.Open();
        ChargingCycleStartOutcome outcome = await new ChargingCycleStore(context).TryStartAsync(
            new ChargingCycleStart(cycleId, vehicleKey, "charge:" + vehicleKey, WaitingPointImportHarness.Map, 211, rosterVersion, policyVersion, Now),
            null,
            Token);
        Assert.Equal(ChargingCycleStartOutcome.Started, outcome);
    }

    internal static async Task<string> DumpAsync(this WaitingPointImportHarness harness, string table)
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(harness.DatabasePath, readOnly: true));
        await connection.OpenAsync(Token);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        List<string> rows = [];
        while (await reader.ReadAsync(Token))
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(index => Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))));
        }
        return string.Join("\n", rows);
    }

    /// <summary>名册在库里留下的全部痕迹：版本行、桩行、候选车辆行、治理快照、写版本的审计、「已是此内容」的审计。</summary>
    internal static async Task<(long Versions, long Chargers, long Scopes, long Snapshots, long Imported, long Unchanged)> RosterFootprintAsync(
        this WaitingPointImportHarness harness)
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(harness.DatabasePath, readOnly: true));
        await connection.OpenAsync(Token);
        return (
            await WaitingPointImportHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM ChargerRosterVersions"),
            await WaitingPointImportHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM ChargerRosterEntries"),
            await WaitingPointImportHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM ChargerRosterVehicleScopes"),
            await WaitingPointImportHarness.ScalarAsync(
                connection, $"SELECT COUNT(*) FROM GovernedConfigurationSnapshots WHERE ObjectKind = '{GovernedObjectKind.ChargerRoster}'"),
            await WaitingPointImportHarness.ScalarAsync(
                connection, $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{ChargingGovernance.RosterVersionImportedAction}'"),
            await WaitingPointImportHarness.ScalarAsync(
                connection, $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{ChargerRosterImportAudit.UnchangedAction}'"));
    }

    /// <summary>策略在库里留下的全部痕迹：版本、适用车辆、批准、激活、快照、三种审计。</summary>
    internal static async Task<(long Versions, long Scopes, long Approvals, long Activations, long Snapshots, long Written, long Approved, long Activated)> PolicyFootprintAsync(
        this WaitingPointImportHarness harness)
    {
        await using SqliteConnection connection = new(ControlServerSqlite.ForDatabaseFile(harness.DatabasePath, readOnly: true));
        await connection.OpenAsync(Token);
        return (
            await WaitingPointImportHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM ChargingPolicyVersions"),
            await WaitingPointImportHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM ChargingPolicyVehicleScopes"),
            await WaitingPointImportHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM ChargingPolicyApprovals"),
            await WaitingPointImportHarness.ScalarAsync(connection, "SELECT COUNT(*) FROM ChargingPolicyActivations"),
            await WaitingPointImportHarness.ScalarAsync(
                connection, $"SELECT COUNT(*) FROM GovernedConfigurationSnapshots WHERE ObjectKind = '{GovernedObjectKind.ChargingPolicy}'"),
            await WaitingPointImportHarness.ScalarAsync(
                connection, $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{ChargingGovernance.PolicyVersionWrittenAction}'"),
            await WaitingPointImportHarness.ScalarAsync(
                connection, $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{ChargingPolicyAudit.ApprovedAction}'"),
            await WaitingPointImportHarness.ScalarAsync(
                connection, $"SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{ChargingPolicyAudit.ActivatedAction}'"));
    }

    internal sealed class ProcessCrashed : Exception;

    /// <summary>在提交前一刻崩溃：那一刻事务里已经暂存的行都在，提交不会发生。</summary>
    internal sealed class CrashOnCommit(string countSql) : DbTransactionInterceptor
    {
        public long CountInsideTheTransaction { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            await using DbCommand count = transaction.Connection!.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = countSql;
            CountInsideTheTransaction = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            throw new ProcessCrashed();
        }
    }
}
