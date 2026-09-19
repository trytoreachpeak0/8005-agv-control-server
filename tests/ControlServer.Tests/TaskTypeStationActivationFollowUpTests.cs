using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#200: the batch-6 review follow-ups of control-server#191 -- what a reconciliation audit says about the
/// pointer it left, the unattributed attempt that goes back to <c>ACTIVE</c>, audit writes that fail without leaving their
/// row behind, and a hold release that can be traced to its audit.
/// </summary>
public sealed class TaskTypeStationActivationFollowUpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ======== a: the reconciliation audit records the pointer as written ========

    /// <summary>
    /// cs#200 a：对账把指针改回 <c>ACTIVE</c>（原版本仍在用）时，审计详情的 <c>pointerAfter</c> 写明这一笔与写后的指针，
    /// 与库里的那一行一致。
    /// </summary>
    [Fact]
    public async Task AReconciliationThatPutsThePointerBackToActiveRecordsThePointerAsWritten()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        JsonElement after = await PointerAfterAsync(harness, reconciled.AuditRecordId);
        Assert.Equal("RESTORED_ACTIVE", after.GetProperty("write").GetString());
        Assert.Equal(await harness.PointerRowAsync(), PointerRowOf(25, after));
    }

    /// <summary>
    /// cs#200 a：从墓碑出发、需求集为空的尝试对账回墓碑时，<c>pointerAfter</c> 写明留了墓碑，状态与版本与库里一致。
    /// </summary>
    [Fact]
    public async Task AReconciliationThatLeavesATombstoneRecordsThePointerAsWritten()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await CloseAfterAContradictionAsync(harness);
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying))
            .ActivateAsync(TaskTypeStationActivationHarness.Candidate());

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal("25|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());
        JsonElement after = await PointerAfterAsync(harness, reconciled.AuditRecordId);
        Assert.Equal("TOMBSTONE", after.GetProperty("write").GetString());
        Assert.Equal(await harness.PointerRowAsync(), PointerRowOf(25, after));
    }

    /// <summary>
    /// cs#200 a：一张从未激活过的图，第一次激活（有暂停记着来历）中断，对账删掉指针行时，<c>pointerAfter</c> 明确写「无指针行」。
    /// </summary>
    [Fact]
    public async Task AReconciliationThatDeletesThePointerRowSaysThereIsNoPointerRow()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        RiotMapStationCatalogSnapshot map26 = TaskTypeStationCatalogEvidence.Supplied(
            26, TaskTypeStationActivationHarness.CatalogStations, TaskTypeStationActivationHarness.Now);
        await harness.ConfirmCatalogAsync(map26, TaskTypeStationActivationHarness.Now.AddSeconds(-30));
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            new TaskTypeStationCandidate(
                26, 1, [TransportTaskTypes.WireToGate], [TaskTypeStationActivationHarness.Gate]),
            catalog: map26);
        Assert.StartsWith("25|1|ACTIVE|<null>\n26|<null>|ACTIVATION_UNKNOWN|", await harness.PointerRowAsync(), StringComparison.Ordinal);

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            26, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        JsonElement after = await PointerAfterAsync(harness, reconciled.AuditRecordId);
        Assert.Equal(
            ("ROW_DELETED", false),
            (after.GetProperty("write").GetString(), after.GetProperty("rowPresent").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("state").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("activeVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("pendingVersion").ValueKind);
    }

    /// <summary>
    /// cs#200 a：读回矛盾时对账什么都不写，<c>pointerAfter</c> 写明没动，仍是库里那一行（结果未知、待定为目标版本）。
    /// </summary>
    [Fact]
    public async Task AContradictoryReconciliationRecordsThatItLeftThePointerAsItWas()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        await harness.ExecuteAsync("UPDATE TaskTypeStationBindings SET StationName = '关卡-改' WHERE MapId = 25 AND Version = 1");

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.Contradictory, reconciled.Conclusion);
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        JsonElement after = await PointerAfterAsync(harness, reconciled.AuditRecordId);
        Assert.Equal("UNCHANGED", after.GetProperty("write").GetString());
        Assert.Equal(await harness.PointerRowAsync(), PointerRowOf(25, after));
    }

    // ======== c: an unattributed attempt over an active version goes back to ACTIVE, not to a tombstone ========

    /// <summary>
    /// cs#200 c：需求集为空、已有生效版本 v2 的图，激活 v3 在两步之间中断——没有暂停记着来历（<c>unattributed</c>）。对账读回
    /// v2 仍在用：指针回到 <c>ACTIVE</c>、生效 v2、无待定版本，不是墓碑。
    /// </summary>
    [Fact]
    public async Task AnUnattributedAttemptOverAnActiveVersionThatDidNotHappenGoesBackToActiveNotToATombstone()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await ActivateAnEmptyRequirementSetAsync(harness);
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());

        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            SecondEmptyRequirementSet, at: TaskTypeStationActivationHarness.Now.AddMinutes(1));
        Assert.Equal("25|2|ACTIVATION_UNKNOWN|3", await harness.PointerRowAsync());
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(2), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal("unattributed", reconciled.AttemptId);
        Assert.Equal((2L, 3L), (reconciled.PreviousVersion, reconciled.TargetVersion));
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.Default().Bindings.ReadActiveAsync(25, Token))?.Version);
    }

    /// <summary>
    /// cs#200 c：同上，但 v3 的第二步已提交、提交之后连接断了——指针被重新标成结果未知（生效 v3、待定 v3），仍没有暂停。对账读回
    /// v3 已生效：指针 <c>ACTIVE</c>、生效 v3。
    /// </summary>
    [Fact]
    public async Task AnUnattributedAttemptWhoseTargetCommittedGoesToActiveOnTheTarget()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await ActivateAnEmptyRequirementSetAsync(harness);

        CommitFault fault = new(new InvalidOperationException("The connection dropped after COMMIT was sent.")) { AfterCommit = true };
        TaskTypeStationActivationResult unknown = await TaskTypeStationActivationHarness.StackOver(
                harness.NewContext(fault), inner => new FaultOnComplete(inner, fault))
            .ActivateAsync(SecondEmptyRequirementSet, at: TaskTypeStationActivationHarness.Now.AddMinutes(1));
        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, unknown.Outcome);
        Assert.Equal("25|3|ACTIVATION_UNKNOWN|3", await harness.PointerRowAsync());
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(2), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.TargetActive, reconciled.Conclusion);
        Assert.Equal("unattributed", reconciled.AttemptId);
        Assert.Equal("25|3|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.Equal(3, (await harness.Default().Bindings.ReadActiveAsync(25, Token))?.Version);
    }

    // ======== d: an audit write the database refuses leaves nothing behind in the context ========

    /// <summary>
    /// cs#200 d：业务审计的插入被数据库拒绝（等锁超时），调用方在同一个上下文上接着保存别的东西——被拒的那行不能被顺带补交，
    /// 第二次保存照常成功。
    /// </summary>
    [Fact]
    public async Task ABusinessAuditTheDatabaseRefusesIsNotCommittedByTheNextSaveOnTheSameContext()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        RefuseOnce<BusinessAuditRecordRow> fault = new(row => row.Action == "CS200_REFUSED");
        GovernanceStore store = Governance(harness.NewContext(fault));

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.WriteBusinessAsync(
            Entry("CS200_REFUSED"), TaskTypeStationActivationHarness.Now, Token));
        string next = await store.WriteBusinessAsync(Entry("CS200_NEXT"), TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(1, fault.Thrown);
        await using ControlServerDbContext reader = harness.NewContext();
        string[] written = await reader.Set<BusinessAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action.StartsWith("CS200_"))
            .Select(row => row.AuditRecordId + "|" + row.Action)
            .ToArrayAsync(Token);
        Assert.Equal([next + "|CS200_NEXT"], written);
    }

    /// <summary>cs#200 d：同上，管理员审计。</summary>
    [Fact]
    public async Task AnAdministratorAuditTheDatabaseRefusesIsNotCommittedByTheNextSaveOnTheSameContext()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        RefuseOnce<AdministratorAuditRecordRow> fault = new(row => row.Action == "CS200_REFUSED");
        GovernanceStore store = Governance(harness.NewContext(fault));

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.WriteAdministratorAsync(
            Entry("CS200_REFUSED"), TaskTypeStationActivationHarness.Now, Token));
        string next = await store.WriteAdministratorAsync(Entry("CS200_NEXT"), TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(1, fault.Thrown);
        await using ControlServerDbContext reader = harness.NewContext();
        string[] written = await reader.Set<AdministratorAuditRecordRow>().AsNoTracking()
            .Where(row => row.Action.StartsWith("CS200_"))
            .Select(row => row.AuditRecordId + "|" + row.Action)
            .ToArrayAsync(Token);
        Assert.Equal([next + "|CS200_NEXT"], written);
    }

    /// <summary>
    /// cs#200 d：审计写失败只摘掉审计自己那一行，调用方在同一个上下文里先前跟踪、尚未保存的改动原样留着——那是调用方的工作单元，
    /// 不归审计写入器清。
    /// </summary>
    [Fact]
    public async Task ARefusedAuditLeavesTheCallersOwnPendingChangesTracked()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        RefuseOnce<BusinessAuditRecordRow> fault = new(row => row.Action == "CS200_REFUSED");
        ControlServerDbContext context = harness.NewContext(fault);
        GovernanceStore store = Governance(context);
        TaskTypeStationActiveBindingSetRow pointer = await context.Set<TaskTypeStationActiveBindingSetRow>()
            .SingleAsync(row => row.MapId == 25, Token);
        pointer.UpdatedAt = TaskTypeStationActivationHarness.Now.AddMinutes(5);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => store.WriteBusinessAsync(
            Entry("CS200_REFUSED"), TaskTypeStationActivationHarness.Now, Token));

        Assert.Equal(EntityState.Modified, context.Entry(pointer).State);
        Assert.DoesNotContain(context.ChangeTracker.Entries<BusinessAuditRecordRow>(), entry => entry.State == EntityState.Added);
    }

    // ======== e: a hold release names the audit record that recorded it ========

    /// <summary>
    /// cs#200 e：解除人工与目录变化暂停后，每条暂停的 <c>ReleasedBy</c> 是 <c>fieldops:release-hold:</c> 加这次解除那条审计的记录号，
    /// 按它能在业务审计里查回那条解除审计；<c>RaisedBy</c> 不变。再解除一次（已无可解除）被拒，先前记下的解除人不被改写。
    /// </summary>
    [Fact]
    public async Task AHoldReleaseRecordsItsOwnAuditRecordAsTheOneWhoReleasedTheHolds()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
        await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "CATALOG_RENAMED", "{}", "server",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        TaskTypeStationHoldReleaseResult released = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK-0920", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Released, released.Outcome);
        string releaser = "fieldops:release-hold:" + released.AuditRecordId;
        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Equal(
            [("operator", releaser), ("server", releaser)],
            holds.Select(hold => (hold.RaisedBy, hold.ReleasedBy)).Order());
        Assert.All(released.Released, hold => Assert.Equal(releaser, hold.ReleasedBy));
        BusinessAuditRecordRow audit = Assert.Single(
            await harness.AuditAsync(), row => row.AuditRecordId == released.AuditRecordId);
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.HoldReleased, GovernanceActionOutcome.Succeeded),
            (audit.Action, audit.Outcome));

        TaskTypeStationHoldReleaseResult again = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK-0920", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Rejected, again.Outcome);
        Assert.Contains(again.Violations, violation => violation.ReasonCode == TaskTypeStationActivationReasonCodes.NoHoldToRelease);
        Assert.All(await harness.HoldsAsync(), hold => Assert.Equal(releaser, hold.ReleasedBy));
    }

    private static GovernanceStore Governance(ControlServerDbContext context) =>
        new(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    private static GovernanceAuditEntry Entry(string action) =>
        new(action, GovernedObjectKind.PublicStationBinding, "cs200", null, GovernanceActionOutcome.Succeeded, "{}");

    /// <summary>
    /// A second version with an empty requirement set, different from the first: the gate stays bound but nothing on the
    /// map requires it, so an activation of it from the first holds no task type.
    /// </summary>
    private static TaskTypeStationCandidate SecondEmptyRequirementSet { get; } =
        new(TaskTypeStationActivationHarness.MapId, 1, [], [TaskTypeStationActivationHarness.Gate]);

    /// <summary>Map 25 moves from the fixture's version 1 to a version 2 whose requirement set is empty.</summary>
    private static async Task ActivateAnEmptyRequirementSetAsync(TaskTypeStationActivationHarness harness)
    {
        TaskTypeStationActivationResult activated = await harness.Default().ActivateAsync(
            new TaskTypeStationCandidate(TaskTypeStationActivationHarness.MapId, 1, [], []));
        Assert.Equal((TaskTypeStationActivationOutcome.Activated, 2L), (activated.Outcome, activated.TargetVersion));
    }

    private static async Task CloseAfterAContradictionAsync(TaskTypeStationActivationHarness harness)
    {
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        await harness.ExecuteAsync("UPDATE TaskTypeStationBindings SET StationName = '关卡-改' WHERE MapId = 25 AND Version = 1");
        TaskTypeStationManualCloseResult closed = await harness.Default().Service.CloseManuallyAsync(
            25, new TaskTypeStationChangeRequest("两个版本都读不回", "现场工程师"), TaskTypeStationActivationHarness.Now, Token);
        Assert.Equal(TaskTypeStationManualCloseOutcome.Closed, closed.Outcome);
    }

    /// <summary>The <c>pointerAfter</c> object of the business audit record <paramref name="auditRecordId"/>.</summary>
    private static async Task<JsonElement> PointerAfterAsync(TaskTypeStationActivationHarness harness, string auditRecordId)
    {
        await using ControlServerDbContext context = harness.NewContext();
        BusinessAuditRecordRow row = await context.Set<BusinessAuditRecordRow>().AsNoTracking()
            .SingleAsync(candidate => candidate.AuditRecordId == auditRecordId, Token);
        using JsonDocument detail = JsonDocument.Parse(row.DetailJson);
        return detail.RootElement.GetProperty("pointerAfter").Clone();
    }

    /// <summary>The pointer as <c>pointerAfter</c> states it, in <see cref="TaskTypeStationActivationHarness.PointerRowAsync"/>'s form.</summary>
    private static string PointerRowOf(int mapId, JsonElement after)
    {
        Assert.True(after.GetProperty("rowPresent").GetBoolean());
        return $"{mapId}|{Value(after.GetProperty("activeVersion"))}|{after.GetProperty("state").GetString()}|{Value(after.GetProperty("pendingVersion"))}";
    }

    private static string Value(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? "<null>" : value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The database refuses the insert of one <typeparamref name="TRow"/>, once, from inside SaveChanges -- after the row was
/// added to the context, as a lock wait past the busy timeout would.
/// </summary>
internal sealed class RefuseOnce<TRow>(Func<TRow, bool> refuses) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    where TRow : class
{
    public int Thrown { get; private set; }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (Thrown == 0 && eventData.Context!.ChangeTracker.Entries<TRow>().Any(entry =>
                entry.State == EntityState.Added && refuses(entry.Entity)))
        {
            Thrown++;
            throw new Microsoft.Data.Sqlite.SqliteException("database is locked", 5);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
