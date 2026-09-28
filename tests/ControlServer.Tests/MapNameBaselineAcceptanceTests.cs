using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// Map 级改名的恢复（REQ-0340 恢复半边，control-server#186）：现场核对后把新名称接受为基线，写审计；改名没被接受之前，
/// 批次6-05 的解除暂停动作拒绝解除，接受之后照常解除。对真 SQLite 文件跑，入口是
/// <see cref="MapNameBaselineAcceptanceService"/>、<see cref="TaskTypeStationActivationService.ReleaseHoldAsync"/> 与 FieldOps 进程。
/// </summary>
public sealed class MapNameBaselineAcceptanceTests
{
    private static readonly DateTimeOffset Now = TaskTypeStationActivationHarness.Now;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcceptingThePendingNameMakesItTheBaselineAndWritesOneAuditNamingBothNames()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        MapNameBaselineStore baselines = new(stack.Context, stack.Governance);
        await PendingRenameAsync(baselines, "老厂前线new_wk", "老厂前线new_wk2");

        MapNameBaselineAcceptResult result = await Service(stack).AcceptAsync(
            25, "老厂前线new_wk2", new TaskTypeStationChangeRequest("现场核对 26 号图改名", "现场工程师"), Now, Token);

        Assert.True(result.Accepted, string.Join("; ", result.Violations.Select(violation => violation.ReasonCode)));
        Assert.Equal(("老厂前线new_wk", "老厂前线new_wk2"), (result.PreviousName, result.AcceptedName));
        stack.Context.ChangeTracker.Clear();
        MapNameBaseline? baseline = await baselines.ReadAsync(25, Token);
        Assert.Equal(
            ("老厂前线new_wk2", (string?)null, (DateTimeOffset?)null, (DateTimeOffset?)Now,
                "fieldops:accept-map-name:" + result.AuditRecordId),
            (baseline?.Name, baseline?.PendingName, baseline?.PendingSince, baseline?.AcceptedAt, baseline?.AcceptedBy));
        BusinessAuditRecordRow audit = Assert.Single(
            await harness.AuditAsync(), row => row.Action == MapNameBaselineAuditActions.Accepted);
        Assert.Equal(result.AuditRecordId, audit.AuditRecordId);
        Assert.Equal(
            (GovernedObjectKind.PublicStationBinding, "map-25", GovernanceActionOutcome.Succeeded),
            (audit.ObjectKind, audit.ObjectId, audit.Outcome));
        using JsonDocument detail = JsonDocument.Parse(audit.DetailJson);
        JsonElement root = detail.RootElement;
        Assert.Equal("老厂前线new_wk", root.GetProperty("previousName").GetString());
        Assert.Equal("老厂前线new_wk2", root.GetProperty("acceptedName").GetString());
        Assert.Equal("现场核对 26 号图改名", root.GetProperty("reason").GetString());
        Assert.Equal("现场工程师", root.GetProperty("selfReportedRole").GetString());
        Assert.Equal("ACCEPTED", root.GetProperty("conclusion").GetString());
    }

    [Theory]
    [InlineData("no-baseline", MapNameBaselineReasonCodes.BaselineMissing)]
    [InlineData("no-pending", MapNameBaselineReasonCodes.NoPendingRename)]
    [InlineData("typo", MapNameBaselineReasonCodes.NameMismatch)]
    [InlineData("baseline-name", MapNameBaselineReasonCodes.NameMismatch)]
    public async Task AnAcceptanceThatDoesNotMatchThePendingNameIsRefusedAuditedAndChangesNothing(string scenario, string reasonCode)
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        MapNameBaselineStore baselines = new(stack.Context, stack.Governance);
        switch (scenario)
        {
            case "no-baseline":
                break;
            case "no-pending":
                await baselines.EstablishAsync(25, "老厂前线new_wk", Now.AddHours(-1), Token);
                break;
            default:
                await PendingRenameAsync(baselines, "老厂前线new_wk", "老厂前线new_wk2");
                break;
        }
        string before = JsonSerializer.Serialize(await baselines.ReadAsync(25, Token));

        MapNameBaselineAcceptResult result = await Service(stack).AcceptAsync(
            25, scenario == "baseline-name" ? "老厂前线new_wk" : scenario == "typo" ? "老厂前线new_wk3" : "老厂前线new_wk2",
            new TaskTypeStationChangeRequest("现场核对"), Now, Token);

        Assert.False(result.Accepted);
        Assert.Equal(reasonCode, Assert.Single(result.Violations).ReasonCode);
        stack.Context.ChangeTracker.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(await baselines.ReadAsync(25, Token)));
        BusinessAuditRecordRow audit = Assert.Single(
            await harness.AuditAsync(), row => row.Action.StartsWith("MAP_NAME_BASELINE_", StringComparison.Ordinal));
        Assert.Equal(
            (MapNameBaselineAuditActions.AcceptRejected, GovernanceActionOutcome.Failed, result.AuditRecordId),
            (audit.Action, audit.Outcome, audit.AuditRecordId));
        Assert.Contains(reasonCode, audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>存储自己也只在待接受名称仍然逐字节相同时才写：不同就什么都不写，连审计也不写。</summary>
    [Fact]
    public async Task TheStoreAcceptsOnlyTheNameThatIsStillPendingAndOtherwiseWritesNothing()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        MapNameBaselineStore baselines = new(stack.Context, stack.Governance);
        await PendingRenameAsync(baselines, "老厂前线new_wk", "老厂前线new_wk2");
        int auditsBefore = (await harness.AuditAsync()).Count;

        string? auditId = await baselines.AcceptAsync(
            25, "老厂前线new_wk3", "fieldops:accept-map-name:", _ => Entry(), Now, Token);

        Assert.Null(auditId);
        stack.Context.ChangeTracker.Clear();
        Assert.Equal("老厂前线new_wk2", (await baselines.ReadAsync(25, Token))?.PendingName);
        Assert.Equal(auditsBefore, (await harness.AuditAsync()).Count);

        Assert.NotNull(await baselines.AcceptAsync(25, "老厂前线new_wk2", "fieldops:accept-map-name:", _ => Entry(), Now, Token));
        stack.Context.ChangeTracker.Clear();
        Assert.Equal("老厂前线new_wk2", (await baselines.ReadAsync(25, Token))?.Name);
    }

    /// <summary>
    /// 先解除、后接受会在下一轮被重新暂停；所以改名没被接受之前，解除动作直接拒绝并说明原因。接受之后照常解除。
    /// </summary>
    [Fact]
    public async Task AHoldReleaseIsRefusedWhileTheMapCarriesAnUnacceptedRenameAndGoesThroughOnceItIsAccepted()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        MapNameBaselineStore baselines = new(stack.Context, stack.Governance);
        await PendingRenameAsync(baselines, "老厂前线new_wk", "老厂前线new_wk2");
        TaskTypeStationHold renamed = (await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, MapNameHoldReasons.MapRenamed, "{}",
            "server", Now.AddMinutes(-5), Token)).Hold;

        TaskTypeStationHoldReleaseResult refused = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Rejected, refused.Outcome);
        Assert.Equal(MapNameBaselineReasonCodes.RenameNotAccepted, Assert.Single(refused.Violations).ReasonCode);
        Assert.Contains("老厂前线new_wk2", Assert.Single(refused.Violations).Detail, StringComparison.Ordinal);
        Assert.Null((await harness.HoldsAsync()).Single(hold => hold.HoldId == renamed.HoldId).ReleasedAt);

        Assert.True((await Service(stack).AcceptAsync(
            25, "老厂前线new_wk2", new TaskTypeStationChangeRequest("现场核对"), Now, Token)).Accepted);
        TaskTypeStationHoldReleaseResult released = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Released, released.Outcome);
        Assert.Equal(renamed.HoldId, Assert.Single(released.Released).HoldId);
    }

    [Fact]
    public async Task AcceptMapNameRunsThroughTheFieldOpsProcessWithItsExitCodes()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        await PendingRenameAsync(new MapNameBaselineStore(stack.Context, stack.Governance), "老厂前线new_wk", "老厂前线new_wk2");

        (int usageExit, _) = await TaskTypeStationFieldOpsTests.RunAsync(
            "accept-map-name", "--database", harness.DatabasePath, "--map", "25", "--reason", "现场核对");
        Assert.Equal(2, usageExit);

        (int refusedExit, JsonElement refused) = await TaskTypeStationFieldOpsTests.RunAsync(
            "accept-map-name", "--database", harness.DatabasePath, "--map", "25", "--map-name", "老厂前线new_wk3",
            "--reason", "现场核对");
        Assert.Equal(1, refusedExit);
        Assert.Equal("REJECTED", refused.GetProperty("outcome").GetString());
        Assert.Equal(
            MapNameBaselineReasonCodes.NameMismatch,
            Assert.Single(refused.GetProperty("violations").EnumerateArray()).GetProperty("reasonCode").GetString());

        (int acceptedExit, JsonElement accepted) = await TaskTypeStationFieldOpsTests.RunAsync(
            "accept-map-name", "--database", harness.DatabasePath, "--map", "25", "--map-name", "老厂前线new_wk2",
            "--reason", "现场核对", "--role", "现场工程师");
        Assert.Equal(0, acceptedExit);
        Assert.Equal(
            ("OK", "老厂前线new_wk", "老厂前线new_wk2"),
            (accepted.GetProperty("outcome").GetString(), accepted.GetProperty("previousName").GetString(),
                accepted.GetProperty("acceptedName").GetString()));
    }

    private static MapNameBaselineAcceptanceService Service(TaskTypeStationActivationHarness.Stack stack) =>
        new(new MapNameBaselineStore(stack.Context, stack.Governance), stack.Governance);

    /// <summary>
    /// A baseline and a pending name, written through the store. Not asserted here: the store's writes are pinned by
    /// <see cref="MapRenameHoldConvergenceTests"/>, and each test below asserts what follows from them.
    /// </summary>
    private static async Task PendingRenameAsync(MapNameBaselineStore baselines, string baseline, string pending)
    {
        await baselines.EstablishAsync(25, baseline, Now.AddHours(-1), Token);
        await baselines.SetPendingAsync(25, pending, Now.AddMinutes(-10), Token);
    }

    private static GovernanceAuditEntry Entry() => new(
        MapNameBaselineAuditActions.Accepted,
        GovernedObjectKind.PublicStationBinding,
        TaskTypeStationGovernance.BindingSetObjectId(25),
        null,
        GovernanceActionOutcome.Succeeded,
        "{}");
}
