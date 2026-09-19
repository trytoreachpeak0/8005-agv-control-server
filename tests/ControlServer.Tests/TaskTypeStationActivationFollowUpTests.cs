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
