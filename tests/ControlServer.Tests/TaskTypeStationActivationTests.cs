using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.TaskTypeStations;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 绑定集整图版本化（control-server#161，批次6-05）：候选预览、激活、回滚、结果未知对账、解除暂停与审计。
/// 对真 SQLite 文件跑，入口是 <see cref="TaskTypeStationActivationService"/>。
/// </summary>
public sealed class TaskTypeStationActivationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DryRunPreviewsTheDifferenceAndTheInFlightImpactAndWritesNothingButOneAuditRecord()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await harness.AddInFlightDemandAsync("D-GATE", TransportTaskTypes.WireToGate, 1, 210);
        IReadOnlyDictionary<string, long> before = await harness.CountRowsAsync();
        TaskTypeStationBinding movedGate = new(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED");

        TaskTypeStationActivationResult result = await harness.Default().ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(movedGate, TaskTypeStationActivationHarness.Staging), dryRun: true);

        Assert.Equal(TaskTypeStationActivationOutcome.Previewed, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(1, result.PreviousVersion);
        Assert.Null(result.TargetVersion);
        Assert.Equal(
            [
                (TransportTaskTypes.StagingToWire, (int?)null, (int?)305),
                (TransportTaskTypes.WireToGate, 210, 330)
            ],
            result.Changes.Select(change => (change.TaskType, change.Before?.StationRiotId, change.After?.StationRiotId)));
        Assert.Equal(1, result.Impact.InFlightDemandCount);
        TaskTypeStationInFlightImpact affected = Assert.Single(result.Impact.Affected);
        Assert.Equal(("D-GATE", 1L, (int?)210, (int?)330),
            (affected.DemandId, affected.FrozenBindingSetVersion, affected.FrozenStationRiotId, affected.CandidateStationRiotId));

        IReadOnlyDictionary<string, long> after = await harness.CountRowsAsync();
        Assert.Equal(before["BusinessAuditRecords"] + 1, after["BusinessAuditRecords"]);
        Assert.Equal(
            before.Where(pair => pair.Key != "BusinessAuditRecords"),
            after.Where(pair => pair.Key != "BusinessAuditRecords"));
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        BusinessAuditRecordRow audit = (await harness.AuditAsync())[^1];
        Assert.Equal(TaskTypeStationActivationAuditActions.Previewed, audit.Action);
        Assert.Equal(GovernanceActionOutcome.Succeeded, audit.Outcome);
    }

    [Fact]
    public async Task ActivationSwitchesTheWholeMapAtOnceAfterTheFirstStepHeldEveryRequiredTaskType()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.Activated, result.Outcome);
        Assert.Equal((1L, 2L), (result.PreviousVersion, result.TargetVersion));
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());
        TaskTypeStationBindingSetVersion active = (await harness.Default().Bindings.ReadActiveAsync(25, Token))!;
        Assert.Equal([305, 210], active.Bindings.Select(binding => binding.StationRiotId));
        Assert.Equal(TaskTypeStationCatalogEvidence.RevisionOf(TaskTypeStationActivationHarness.Catalog.ContentSha256), active.CatalogRevision);

        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            holds.Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.All(holds, hold =>
        {
            Assert.Equal(TaskTypeStationHoldSource.ActivationResultUnknown, hold.Source);
            Assert.NotNull(hold.ReleasedAt);
        });
        Assert.Equal(
            [
                (TaskTypeStationActivationAuditActions.Started, GovernanceActionOutcome.ResultUnknown),
                (TaskTypeStationActivationAuditActions.Activated, GovernanceActionOutcome.Succeeded)
            ],
            (await harness.AuditAsync())
                .Where(row => row.Action.StartsWith("TASK_TYPE_STATION_BINDING_SET_ACTIVAT", StringComparison.Ordinal))
                .Select(row => (row.Action, row.Outcome)));
    }

    [Fact]
    public async Task SecondStepFailingBeforeCommitLeavesTheOldVersionWholeAndEveryRequiredTaskTypeHeld()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        CommitFault fault = new(new TimeoutException("SQLite commit did not return within the lock timeout."));
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(fault), inner => new FaultOnComplete(inner, fault));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(TaskTypeStationActivationHarness.Candidate(
            new TaskTypeStationBinding(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED"),
            TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(1, fault.Thrown);
        // Whole old version: the pointer never moved, and version 1 still reads back exactly as it was.
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        TaskTypeStationBindingSetVersion active = (await harness.Default().Bindings.ReadActiveAsync(25, Token))!;
        Assert.Equal((1L, 210), (active.Version, Assert.Single(active.Bindings).StationRiotId));

        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            holds.Where(hold => hold.ReleasedAt is null).Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.All(holds, hold => Assert.Equal(TaskTypeStationHoldSource.ActivationResultUnknown, hold.Source));

        IReadOnlyList<BusinessAuditRecordRow> audit = await harness.AuditAsync();
        Assert.DoesNotContain(audit, row => row.Action == TaskTypeStationActivationAuditActions.Activated);
        BusinessAuditRecordRow last = audit[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.ResultUnknown, GovernanceActionOutcome.TimedOut),
            (last.Action, last.Outcome));
    }

    [Fact]
    public async Task ReadBackThatContradictsTheTargetPutsTheMapBackOnHoldAndNeverSaysActivated()
    {
        // The pointer names the target, but the target's rows no longer hash to what the version recorded (review round 2,
        // N2: a pointer moved to some other version is being overtaken, which is a different test).
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(),
            inner => new AfterComplete(inner, () => harness.ExecuteAsync(
                "UPDATE TaskTypeStationBindings SET StationName = '关卡-改' WHERE MapId = 25 AND Version = 2")));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Contains("but its header says", result.Detail, StringComparison.Ordinal);
        Assert.Equal("25|2|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            (await harness.HoldsAsync()).Where(hold => hold.ReleasedAt is null)
                .Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        IReadOnlyList<BusinessAuditRecordRow> audit = await harness.AuditAsync();
        Assert.DoesNotContain(audit, row => row.Action == TaskTypeStationActivationAuditActions.Activated);
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.ResultUnknown, GovernanceActionOutcome.ResultUnknown),
            (audit[^1].Action, audit[^1].Outcome));
    }

    [Fact]
    public async Task ProcessDyingBetweenTheStepsLeavesTheMapHeldAcrossARestartUntilReconciliationFindsThePreviousVersion()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServer.Infrastructure.Persistence.ControlServerDbContext dying = harness.NewContext();
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            dying, inner => new DieBeforeComplete(inner, dying));

        await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        // After the restart: nothing after the first step reached the database.
        TaskTypeStationActivationHarness.Stack restarted = harness.Default();
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            (await harness.HoldsAsync()).Where(hold => hold.ReleasedAt is null)
                .Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        Assert.True(await restarted.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Equal(
            [TaskTypeStationActivationAuditActions.Started],
            (await harness.AuditAsync()).Where(row => row.Action.StartsWith("TASK_TYPE_STATION_BINDING_SET_ACTIVAT", StringComparison.Ordinal))
                .Select(row => row.Action));
        TaskTypeStationActivationAttempt open = (await restarted.Activations.ReadOpenAttemptAsync(25, Token))!;
        Assert.Equal((1L, 2L), (open.PreviousVersion, open.TargetVersion));

        TaskTypeStationReconciliationResult first = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, first.Conclusion);
        Assert.Equal((1L, 2L, 1L), (first.PreviousVersion, first.TargetVersion, first.ActiveVersion));
        Assert.Equal(2, first.ReleasedHoldIds.Count);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.All(await harness.HoldsAsync(), hold => Assert.NotNull(hold.ReleasedAt));

        // Reconciling again finds nothing open, and writes its own record next to the first one, which stays as it was.
        BusinessAuditRecordRow firstRecord = (await harness.AuditAsync()).Single(row => row.AuditRecordId == first.AuditRecordId);
        TaskTypeStationReconciliationResult second = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(2), Token);
        Assert.Equal(TaskTypeStationReconciliationConclusion.NothingToReconcile, second.Conclusion);
        IReadOnlyList<BusinessAuditRecordRow> reconciled = [.. (await harness.AuditAsync())
            .Where(row => row.Action == TaskTypeStationActivationAuditActions.Reconciled)];
        Assert.Equal([first.AuditRecordId, second.AuditRecordId], reconciled.Select(row => row.AuditRecordId));
        Assert.Equal(firstRecord.DetailJson, reconciled[0].DetailJson);
        Assert.Contains("\"conclusion\":\"PREVIOUS_ACTIVE\"", reconciled[0].DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"conclusion\":\"NOTHING_TO_RECONCILE\"", reconciled[1].DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondStepThatCommitsButThrowsIsStillUnknownUntilReconciliationReadsTheTargetBack()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        CommitFault fault = new(new InvalidOperationException("The connection dropped after COMMIT was sent.")) { AfterCommit = true };
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(fault), inner => new FaultOnComplete(inner, fault));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        // The switch did land, but nothing here saw it land: the map is held and no record says activated.
        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal("25|2|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.HoldsAsync()).Count(hold => hold.ReleasedAt is null));
        Assert.DoesNotContain(await harness.AuditAsync(), row => row.Action == TaskTypeStationActivationAuditActions.Activated);

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.TargetActive, reconciled.Conclusion);
        Assert.Equal(2, reconciled.ActiveVersion);
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.All(await harness.HoldsAsync(), hold => Assert.NotNull(hold.ReleasedAt));
    }

    [Fact]
    public async Task ReconciliationThatReadsNeitherVersionWholeKeepsTheHoldsAndEachRunKeepsItsOwnRecord()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        // Version 1's rows no longer hash to what version 1 recorded.
        await harness.ExecuteAsync(
            "UPDATE TaskTypeStationBindings SET StationName = '关卡-改' WHERE MapId = 25 AND Version = 1");
        TaskTypeStationActivationHarness.Stack restarted = harness.Default();

        TaskTypeStationReconciliationResult first = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);
        TaskTypeStationReconciliationResult second = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(2), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.Contradictory, first.Conclusion);
        Assert.Equal(TaskTypeStationReconciliationConclusion.Contradictory, second.Conclusion);
        Assert.Empty(first.ReleasedHoldIds);
        Assert.Contains("but its header says", first.Detail, StringComparison.Ordinal);
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.HoldsAsync()).Count(hold => hold.ReleasedAt is null));
        BusinessAuditRecordRow[] reconciled = [.. (await harness.AuditAsync())
            .Where(row => row.Action == TaskTypeStationActivationAuditActions.Reconciled)];
        Assert.Equal([first.AuditRecordId, second.AuditRecordId], reconciled.Select(row => row.AuditRecordId));
        Assert.All(reconciled, row =>
        {
            Assert.Equal(GovernanceActionOutcome.ResultUnknown, row.Outcome);
            Assert.Contains("\"conclusion\":\"CONTRADICTORY\"", row.DetailJson, StringComparison.Ordinal);
        });
    }

    public static TheoryData<string, string, string> Rejections => new()
    {
        { "catalog-stale", TaskTypeStationReasonCodes.BindingCatalogNotFresh, "more than the approved 300 s" },
        { "station-missing", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "has no station 999" },
        { "other-map", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "The catalog is for Map 26, not Map 25" },
        { "task-type-twice", TaskTypeStationReasonCodes.BindingDuplicate, "WIRE_TO_GATE is bound 2 times" },
        { "station-reused", TaskTypeStationReasonCodes.StationReused, "is bound to 2 task types" },
        { "required-unbound", TaskTypeStationReasonCodes.BindingRequiredMissing, "STAGING_TO_WIRE is in this map's requirement set" },
        { "rule-not-current", TaskTypeStationActivationReasonCodes.RuleVersionNotCurrent, "the current rule version is 2" },
        { "no-site-verification", TaskTypeStationReasonCodes.BindingSiteVerificationMissing, "without a site verification reference" },
    };

    /// <summary>
    /// 激活前重验的八种拒绝（REQ-0337、REQ-0338、REQ-0343）。被拒时什么都不写：没有新版本、没有暂停、指针不动，只有一条被拒审计——
    /// 这也是「未经校验的非法值写不进去」：绑定集只经这一条校验过的路写入。
    /// </summary>
    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task ActivationRevalidatesAndRefusesWithEveryReasonListed(string scenario, string reasonCode, string detail)
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationBinding gate = TaskTypeStationActivationHarness.Gate;
        TaskTypeStationBinding staging = TaskTypeStationActivationHarness.Staging;
        RiotMapStationCatalogSnapshot catalog = TaskTypeStationActivationHarness.Catalog;
        TaskTypeStationCandidate candidate = TaskTypeStationActivationHarness.Candidate(gate, staging);
        switch (scenario)
        {
            case "catalog-stale":
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddMinutes(-10));
                break;
            case "station-missing":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, staging with { StationRiotId = 999 });
                break;
            case "other-map":
                catalog = TaskTypeStationCatalogEvidence.Supplied(26, [new(210, "关卡"), new(305, "派工待送取货")], TaskTypeStationActivationHarness.Now);
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddSeconds(-30));
                break;
            case "task-type-twice":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, gate with { StationRiotId = 330, StationName = "氮气柜" });
                break;
            case "station-reused":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, staging with { StationRiotId = 210, StationName = "关卡" });
                break;
            case "required-unbound":
                candidate = new TaskTypeStationCandidate(
                    25, 1, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire], [gate]);
                break;
            case "rule-not-current":
                await harness.Default().Rules.WriteVersionAsync(
                    [.. TaskTypeStationTestData.SixRules.Where(rule => rule.TaskType != TransportTaskTypes.WireToNitrogen)],
                    "preset:newer", TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
                break;
            case "no-site-verification":
                candidate = TaskTypeStationActivationHarness.Candidate(gate, staging with { SiteVerificationRef = " " });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
        IReadOnlyDictionary<string, long> before = await harness.CountRowsAsync();

        TaskTypeStationActivationResult result = await harness.Default().ActivateAsync(candidate, catalog: catalog);

        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.ReasonCode == reasonCode
            && violation.Detail.Contains(detail, StringComparison.Ordinal));
        IReadOnlyDictionary<string, long> after = await harness.CountRowsAsync();
        Assert.Equal(before["BusinessAuditRecords"] + 1, after["BusinessAuditRecords"]);
        Assert.Equal(
            before.Where(pair => pair.Key != "BusinessAuditRecords"),
            after.Where(pair => pair.Key != "BusinessAuditRecords"));
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        BusinessAuditRecordRow rejected = (await harness.AuditAsync())[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.Rejected, GovernanceActionOutcome.Failed),
            (rejected.Action, rejected.Outcome));
        Assert.Contains(reasonCode, rejected.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// 回滚是把历史版本的内容当作一次新的激活再走一遍全部校验，不是把指针拨回去：它产生新版本号。激活、回滚交错，同一内容激活两次，
    /// 每一次都有自己的审计，后一次不覆盖前一次。
    /// </summary>
    [Fact]
    public async Task RollbackIsAFreshlyValidatedActivationOfOldContentAndEveryActivationKeepsItsOwnRecord()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationCandidate both = TaskTypeStationActivationHarness.Candidate(
            TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging);

        TaskTypeStationActivationResult activated = await stack.ActivateAsync(both);
        TaskTypeStationActivationResult again = await stack.ActivateAsync(both);
        TaskTypeStationActivationResult rolledBack = await stack.Service.RollbackAsync(
            25, 1, TaskTypeStationActivationHarness.Catalog, TaskTypeStationActivationHarness.Request, dryRun: false,
            TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationActivationResult forward = await stack.ActivateAsync(both);

        Assert.Equal(
            [
                (TaskTypeStationActivationOutcome.Activated, 1L, 2L),
                (TaskTypeStationActivationOutcome.Activated, 2L, 2L),
                (TaskTypeStationActivationOutcome.Activated, 2L, 3L),
                (TaskTypeStationActivationOutcome.Activated, 3L, 4L)
            ],
            new[] { activated, again, rolledBack, forward }.Select(result =>
                (result.Outcome, result.PreviousVersion ?? 0, result.TargetVersion ?? 0)));
        Assert.Equal(TaskTypeStationRequestCategory.Rollback, rolledBack.RequestCategory);
        TaskTypeStationBindingSetVersion three = (await stack.Bindings.ReadVersionAsync(25, 3, Token))!;
        Assert.Equal(210, Assert.Single(three.Bindings).StationRiotId);
        Assert.StartsWith("fieldops:rollback:v1:", three.Source, StringComparison.Ordinal);
        Assert.Equal("25|4|ACTIVE|<null>", await harness.PointerRowAsync());

        BusinessAuditRecordRow[] records = [.. (await harness.AuditAsync())
            .Where(row => row.Action == TaskTypeStationActivationAuditActions.Activated)];
        Assert.Equal(
            [.. new[] { activated, again, rolledBack, forward }.Select(result => result.AuditRecordIds.Single())],
            records.Select(row => row.AuditRecordId));
        Assert.Equal([2L, 2L, 3L, 4L], records.Select(row => row.Version ?? 0));
        Assert.Contains("\"requestCategory\":\"ROLLBACK\"", records[2].DetailJson, StringComparison.Ordinal);
        Assert.Contains("\"rollbackOfVersion\":1", records[2].DetailJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackRevalidatesTheOldContentAndRefusesWhatNoLongerHolds()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        await stack.ActivateAsync(TaskTypeStationActivationHarness.Candidate(
            TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        // Station 210 has since left the catalog the server confirms.
        RiotMapStationCatalogSnapshot without210 = TaskTypeStationCatalogEvidence.Supplied(
            25, [.. TaskTypeStationActivationHarness.CatalogStations.Where(station => station.StationId != 210)],
            TaskTypeStationActivationHarness.Now);
        await harness.ConfirmCatalogAsync(without210, TaskTypeStationActivationHarness.Now.AddSeconds(-10));

        TaskTypeStationActivationResult refused = await stack.Service.RollbackAsync(
            25, 1, without210, TaskTypeStationActivationHarness.Request, dryRun: false, TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationActivationResult missing = await stack.Service.RollbackAsync(
            25, 9, without210, TaskTypeStationActivationHarness.Request, dryRun: false, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, refused.Outcome);
        Assert.Contains(refused.Violations, violation =>
            violation.ReasonCode == TaskTypeStationReasonCodes.BindingStationNotInCatalog && violation.StationRiotId == 210);
        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, missing.Outcome);
        Assert.Equal(TaskTypeStationActivationReasonCodes.VersionNotFound, Assert.Single(missing.Violations).ReasonCode);
        Assert.Equal("25|2|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.Null(await stack.Bindings.ReadVersionAsync(25, 3, Token));
    }

    /// <summary>
    /// REQ-0345 配置半边：激活、回滚、结果未知与对账前后，需求冻结行、冻结站点行、旅程行、订单行与需求行逐行相同。
    /// </summary>
    [Fact]
    public async Task ActivationRollbackAndReconciliationLeaveEveryInFlightRowExactlyAsItWas()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await harness.AddInFlightDemandAsync("D-GATE-1", TransportTaskTypes.WireToGate, 1, 210);
        await harness.AddInFlightDemandAsync("D-GATE-2", TransportTaskTypes.WireToGate, 1, 210);
        IReadOnlyDictionary<string, IReadOnlyList<string>> before = await harness.SnapshotInFlightRowsAsync();
        Assert.All(TaskTypeStationActivationHarness.InFlightTables, table => Assert.NotEmpty(before[table]));
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationBinding movedGate = new(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED");

        TaskTypeStationActivationResult moved = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(movedGate, TaskTypeStationActivationHarness.Staging));
        TaskTypeStationActivationResult back = await stack.Service.RollbackAsync(
            25, 1, TaskTypeStationActivationHarness.Catalog, TaskTypeStationActivationHarness.Request, dryRun: false,
            TaskTypeStationActivationHarness.Now, Token);
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying))
            .ActivateAsync(TaskTypeStationActivationHarness.Candidate(movedGate));
        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationActivationOutcome.Activated, moved.Outcome);
        Assert.Equal(2, moved.Impact.Affected.Count);
        Assert.Equal(TaskTypeStationActivationOutcome.Activated, back.Outcome);
        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        IReadOnlyDictionary<string, IReadOnlyList<string>> after = await harness.SnapshotInFlightRowsAsync();
        Assert.All(TaskTypeStationActivationHarness.InFlightTables, table => Assert.Equal(before[table], after[table]));
    }

    /// <summary>
    /// 激活事务直接写的暂停行：重试、重启、重复对账都不会让同一任务类型出现第二条未解除的「激活结果未知」暂停。第二步按本次尝试的 HoldId
    /// 撤；对账能下结论时撤该图全部「激活结果未知」暂停，连同别的尝试留下的孤儿（该图任何时候最多一次未结尝试，审查 S3）。人工暂停一条不碰。
    /// </summary>
    [Fact]
    public async Task ActivationHoldsAreNeverDuplicatedAndOnlyThisAttemptsHoldsAreReleased()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationCandidate both = TaskTypeStationActivationHarness.Candidate(
            TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging);
        // A hold of the same source left by some other attempt, and a manual hold: neither belongs to what follows.
        await harness.ExecuteAsync(
            "INSERT INTO TaskTypeStationHolds (HoldId, MapId, TaskType, Source, ReasonCode, DetailJson, RaisedAt, RaisedBy) VALUES "
            + "('foreign-hold', 25, 'WIRE_TO_OPTICAL', 'ACTIVATION_RESULT_UNKNOWN', 'TASK_TYPE_ACTIVATION_RESULT_UNKNOWN', "
            + "'{\"attemptId\":\"another-attempt\",\"targetVersion\":9,\"previousVersion\":8}', '2026-09-19 07:00:00+00:00', 'fieldops:activation:another-attempt')");
        TaskTypeStationHold manual = await harness.Default().Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        async Task AssertOneOpenActivationHoldPerTaskTypeAsync()
        {
            IReadOnlyList<TaskTypeStationHoldRow> open = [.. (await harness.HoldsAsync())
                .Where(hold => hold.ReleasedAt is null && hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown
                    && hold.HoldId != "foreign-hold")];
            Assert.Equal(
                [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
                open.Select(hold => hold.TaskType).Order(StringComparer.Ordinal));
        }

        // The second step commits and then throws: step 2 released the holds, the unknown path raises them again -- once.
        CommitFault fault = new(new InvalidOperationException("dropped after COMMIT")) { AfterCommit = true };
        TaskTypeStationActivationResult unknown = await TaskTypeStationActivationHarness.StackOver(
                harness.NewContext(fault), inner => new FaultOnComplete(inner, fault))
            .ActivateAsync(both);
        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, unknown.Outcome);
        await AssertOneOpenActivationHoldPerTaskTypeAsync();

        // Retrying while the result is unknown is refused and raises nothing.
        TaskTypeStationActivationResult retried = await harness.Default().ActivateAsync(both);
        Assert.Equal(TaskTypeStationActivationOutcome.Rejected, retried.Outcome);
        Assert.Contains(retried.Violations, violation =>
            violation.ReasonCode == TaskTypeStationActivationReasonCodes.ActivationPendingReconciliation);
        await AssertOneOpenActivationHoldPerTaskTypeAsync();

        // Re-marking the same attempt again, as a restarted process retrying the unknown path would, adds nothing.
        TaskTypeStationActivationHarness.Stack restarted = harness.Default();
        TaskTypeStationActivationAttempt open = (await restarted.Activations.ReadOpenAttemptAsync(25, Token))!;
        await restarted.Activations.MarkUnknownAsync(open, TaskTypeStationActivationHarness.Now, Token);
        await AssertOneOpenActivationHoldPerTaskTypeAsync();

        // Reconciling twice: the first run releases this attempt's two holds and the orphan, the second nothing more.
        TaskTypeStationReconciliationResult first = await restarted.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationReconciliationResult second = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
        Assert.Equal(TaskTypeStationReconciliationConclusion.TargetActive, first.Conclusion);
        Assert.Equal(3, first.ReleasedHoldIds.Count);
        Assert.Contains("foreign-hold", first.ReleasedHoldIds);
        Assert.Equal(TaskTypeStationReconciliationConclusion.NothingToReconcile, second.Conclusion);
        Assert.Empty(second.ReleasedHoldIds);

        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.NotNull(holds.Single(hold => hold.HoldId == "foreign-hold").ReleasedAt);
        Assert.Null(holds.Single(hold => hold.HoldId == manual.HoldId).ReleasedAt);
        Assert.DoesNotContain(holds, hold => hold.ReleasedAt is null
            && hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown);
        // Every activation hold this attempt ever raised names this attempt, and each release names it too.
        Assert.All(
            holds.Where(hold => hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown && hold.HoldId != "foreign-hold"),
            hold =>
            {
                Assert.Contains(unknown.AttemptId, hold.DetailJson, StringComparison.Ordinal);
                Assert.Equal("fieldops:activation:" + unknown.AttemptId, hold.ReleasedBy);
            });
    }

    [Fact]
    public async Task ActivationNeverReleasesAManualOrCatalogChangeHold()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationHold manual = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
        TaskTypeStationHold catalog = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "CATALOG_RENAMED", "{}", "server",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.Activated, result.Outcome);
        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Null(holds.Single(hold => hold.HoldId == manual.HoldId).ReleasedAt);
        Assert.Null(holds.Single(hold => hold.HoldId == catalog.HoldId).ReleasedAt);
        Assert.All(
            holds.Where(hold => hold.Source == TaskTypeStationHoldSource.ActivationResultUnknown),
            hold => Assert.NotNull(hold.ReleasedAt));
    }

    [Fact]
    public async Task ReleasingAHoldRecordsTheReleaseAndKeepsTheHoldRowAndLeavesActivationHoldsAlone()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationHold manual = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
        TaskTypeStationHold catalog = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.CatalogChange, "CATALOG_RENAMED", "{}", "server",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        TaskTypeStationHoldReleaseResult result = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK-0919", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Released, result.Outcome);
        Assert.Empty(result.Violations);
        Assert.Equal(
            new[] { catalog.HoldId, manual.HoldId }.Order(StringComparer.Ordinal),
            result.Released.Select(hold => hold.HoldId).Order(StringComparer.Ordinal));
        IReadOnlyList<TaskTypeStationHoldRow> holds = await harness.HoldsAsync();
        Assert.Equal(2, holds.Count);
        Assert.All(holds, hold => Assert.NotNull(hold.ReleasedAt));
        Assert.False(await stack.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        BusinessAuditRecordRow audit = (await harness.AuditAsync())[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.HoldReleased, GovernanceActionOutcome.Succeeded),
            (audit.Action, audit.Outcome));
        Assert.Contains("SITE-RECHECK-0919", audit.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// REQ-0348：预览、被拒、激活、回滚、结果未知、对账、解除暂停每个动作都写不可改写审计，字段齐全；操作者是部署标识并标不可归属；
    /// 改写与删除被拒；<c>export-audit</c> 用的那条导出路径能全部取回。
    /// </summary>
    [Fact]
    public async Task EveryActionLeavesAnImmutableExportableAuditRecordWithTheRequiredFields()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await harness.AddInFlightDemandAsync("D-GATE", TransportTaskTypes.WireToGate, 1, 210);
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationBinding movedGate = new(TransportTaskTypes.WireToGate, 330, "氮气柜", "SITE-CHECK-GATE-MOVED");
        TaskTypeStationCandidate candidate = TaskTypeStationActivationHarness.Candidate(movedGate, TaskTypeStationActivationHarness.Staging);

        List<string> ids = [];
        ids.AddRange((await stack.ActivateAsync(candidate, dryRun: true)).AuditRecordIds);
        ids.AddRange((await stack.ActivateAsync(TaskTypeStationActivationHarness.Candidate(movedGate, movedGate))).AuditRecordIds);
        TaskTypeStationActivationResult activated = await stack.ActivateAsync(candidate);
        ids.AddRange(activated.AuditRecordIds);
        ids.AddRange((await stack.Service.RollbackAsync(
            25, 1, TaskTypeStationActivationHarness.Catalog, TaskTypeStationActivationHarness.Request, false,
            TaskTypeStationActivationHarness.Now, Token)).AuditRecordIds);
        CommitFault fault = new(new TimeoutException("lock timeout"));
        ids.AddRange((await TaskTypeStationActivationHarness.StackOver(
                harness.NewContext(fault), inner => new FaultOnComplete(inner, fault))
            .ActivateAsync(candidate)).AuditRecordIds);
        ids.Add((await stack.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token)).AuditRecordId);
        await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now, Token);
        TaskTypeStationHoldReleaseResult releasedHold = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
        Assert.True(releasedHold.Outcome == TaskTypeStationHoldReleaseOutcome.Released, string.Join("; ", releasedHold.Violations.Select(v => v.ReasonCode + " " + v.Detail)));
        ids.Add(releasedHold.AuditRecordId);

        IReadOnlyList<BusinessAuditRecordRow> audit = await harness.AuditAsync();
        Assert.Equal(
            [
                TaskTypeStationActivationAuditActions.Previewed,
                TaskTypeStationActivationAuditActions.Rejected,
                TaskTypeStationActivationAuditActions.Activated,
                TaskTypeStationActivationAuditActions.Activated,
                TaskTypeStationActivationAuditActions.ResultUnknown,
                TaskTypeStationActivationAuditActions.Reconciled,
                TaskTypeStationActivationAuditActions.HoldReleased
            ],
            ids.Select(id => audit.Single(row => row.AuditRecordId == id).Action));
        Assert.All(audit, row =>
        {
            Assert.StartsWith(AuditActorAttribution.DeploymentIdentityPrefix, row.ActorIdentity, StringComparison.Ordinal);
            Assert.Equal(AuditActorAttribution.NotAttributableToNaturalPerson, row.ActorAttribution);
        });

        // REQ-0348's fields, on the activation that moved the gate.
        using (System.Text.Json.JsonDocument detail = System.Text.Json.JsonDocument.Parse(
                   audit.Single(row => row.AuditRecordId == activated.AuditRecordIds.Single()).DetailJson))
        {
            System.Text.Json.JsonElement root = detail.RootElement;
            Assert.Equal(25, root.GetProperty("mapId").GetInt32());
            System.Text.Json.JsonElement gate = root.GetProperty("taskTypes").EnumerateArray()
                .Single(item => item.GetProperty("taskType").GetString() == TransportTaskTypes.WireToGate);
            Assert.Equal((210, "关卡"), (gate.GetProperty("before").GetProperty("stationRiotId").GetInt32(), gate.GetProperty("before").GetProperty("stationName").GetString()));
            Assert.Equal((330, "氮气柜"), (gate.GetProperty("after").GetProperty("stationRiotId").GetInt32(), gate.GetProperty("after").GetProperty("stationName").GetString()));
            Assert.Equal(
                TaskTypeStationCatalogEvidence.RevisionOf(TaskTypeStationActivationHarness.Catalog.ContentSha256),
                root.GetProperty("catalogRevision").GetInt64());
            Assert.Equal((1, 1), (root.GetProperty("ruleVersion").GetProperty("previous").GetInt64(), root.GetProperty("ruleVersion").GetProperty("next").GetInt64()));
            Assert.Equal((1, 2), (root.GetProperty("bindingSetVersion").GetProperty("previous").GetInt64(), root.GetProperty("bindingSetVersion").GetProperty("target").GetInt64()));
            Assert.Equal(1, root.GetProperty("impactPreview").GetProperty("inFlightDemandCount").GetInt32());
            Assert.Equal(1, root.GetProperty("activeTaskImpactCount").GetInt32());
            Assert.Equal(TaskTypeStationActivationHarness.Request.Reason, root.GetProperty("reason").GetString());
            Assert.Equal("FieldEngineer", root.GetProperty("selfReportedRole").GetString());
            Assert.Contains(
                "SITE-CHECK-GATE-MOVED",
                root.GetProperty("siteVerification").EnumerateArray().Select(item => item.GetProperty("reference").GetString()));
            Assert.True(root.GetProperty("validation").GetProperty("passed").GetBoolean());
            Assert.Equal("ACTIVATE", root.GetProperty("requestCategory").GetString());
            Assert.Equal("TARGET_ACTIVE", root.GetProperty("conclusion").GetString());
        }

        // Write-once: neither a rewrite nor a delete inside retention gets through.
        await using (ControlServerDbContext context = harness.NewContext())
        {
            BusinessAuditRecordRow row = await context.Set<BusinessAuditRecordRow>()
                .SingleAsync(candidateRow => candidateRow.AuditRecordId == activated.AuditRecordIds[0], Token);
            row.DetailJson = "{}";
            await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() => context.SaveChangesAsync(Token));
            context.ChangeTracker.Clear();
            context.Remove(await context.Set<BusinessAuditRecordRow>()
                .SingleAsync(candidateRow => candidateRow.AuditRecordId == activated.AuditRecordIds[0], Token));
            await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() => context.SaveChangesAsync(Token));
        }
        Assert.Equal(
            audit.Single(row => row.AuditRecordId == activated.AuditRecordIds[0]).DetailJson,
            (await harness.AuditAsync()).Single(row => row.AuditRecordId == activated.AuditRecordIds[0]).DetailJson);

        await using ControlServerDbContext exporting = harness.NewContext();
        IReadOnlyList<AuditExportRecord> exported = await AuditExport.ReadAsync(
            exporting, AuditTrail.Business, null, null, Token);
        Assert.Superset(ids.ToHashSet(StringComparer.Ordinal), exported.Select(record => record.AuditRecordId).ToHashSet(StringComparer.Ordinal));
    }

    public static TheoryData<string, string, string> ReleaseRejections => new()
    {
        { "catalog-stale", TaskTypeStationReasonCodes.BindingCatalogNotFresh, "more than the approved 300 s" },
        { "station-gone", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "has no station 210" },
        { "name-changed", TaskTypeStationReasonCodes.BindingStationNotInCatalog, "is now named 关卡-北, not 关卡" },
        { "no-site-verification", TaskTypeStationReasonCodes.BindingSiteVerificationMissing, "without a site verification" },
    };

    /// <summary>解除暂停对当前新鲜目录做完整重验（REQ-0340 恢复半边）；不通过就拒绝，暂停原样保留。</summary>
    [Theory]
    [MemberData(nameof(ReleaseRejections))]
    public async Task ReleasingAHoldRevalidatesTheBindingAndRefusesWithTheReason(string scenario, string reasonCode, string detail)
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationHold manual = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);
        RiotMapStationCatalogSnapshot catalog = TaskTypeStationActivationHarness.Catalog;
        string? siteVerification = "SITE-RECHECK-0919";
        switch (scenario)
        {
            case "catalog-stale":
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddMinutes(-10));
                break;
            case "station-gone":
                catalog = TaskTypeStationCatalogEvidence.Supplied(
                    25, [.. TaskTypeStationActivationHarness.CatalogStations.Where(station => station.StationId != 210)],
                    TaskTypeStationActivationHarness.Now);
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddSeconds(-10));
                break;
            case "name-changed":
                catalog = TaskTypeStationCatalogEvidence.Supplied(
                    25,
                    [.. TaskTypeStationActivationHarness.CatalogStations.Select(station =>
                        station.StationId == 210 ? station with { StationName = "关卡-北" } : station)],
                    TaskTypeStationActivationHarness.Now);
                await harness.ConfirmCatalogAsync(catalog, TaskTypeStationActivationHarness.Now.AddSeconds(-10));
                break;
            case "no-site-verification":
                siteVerification = "  ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        TaskTypeStationHoldReleaseResult result = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, siteVerification, catalog, TaskTypeStationActivationHarness.Request,
            TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.ReasonCode == reasonCode
            && violation.Detail.Contains(detail, StringComparison.Ordinal));
        Assert.Empty(result.Released);
        Assert.Null((await harness.HoldsAsync()).Single(hold => hold.HoldId == manual.HoldId).ReleasedAt);
        BusinessAuditRecordRow audit = (await harness.AuditAsync())[^1];
        Assert.Equal(
            (TaskTypeStationActivationAuditActions.HoldReleaseRejected, GovernanceActionOutcome.Failed),
            (audit.Action, audit.Outcome));
    }

    // ======== Review of PR #183 (issuecomment-5739636280): S1-S6 and O2 ========

    /// <summary>
    /// S1：激活 A 的第二步还在路上时，运维对账判「原版本在用」、撤了 A 的暂停；A 的第二步随后到达，不得再把新版本切成生效——
    /// 对账写下的「没发生」必须仍然是真的。
    /// </summary>
    [Fact]
    public async Task ASecondStepThatArrivesAfterReconciliationDoesNotSwitchThePointer()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationReconciliationResult? reconciled = null;
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(),
            inner => new BeforeComplete(inner, async () => reconciled = await harness.Default().Service.ReconcileAsync(
                25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token)));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled!.Conclusion);
        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);
        Assert.DoesNotContain(await harness.AuditAsync(), row => row.Action == TaskTypeStationActivationAuditActions.Activated);
    }

    /// <summary>S1：对账之后又开始了回滚 R；激活 A 迟到的第二步不得把 R 的「结果未知」改回 <c>ACTIVE</c>。</summary>
    [Fact]
    public async Task ASecondStepThatArrivesAfterANewerAttemptBeganLeavesThatAttemptAlone()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(),
            inner => new BeforeComplete(inner, async () =>
            {
                await harness.Default().Service.ReconcileAsync(
                    25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
                // Rollback R: its first step commits, then its process dies.
                ControlServerDbContext dying = harness.NewContext();
                await TaskTypeStationActivationHarness.StackOver(dying, rInner => new DieBeforeComplete(rInner, dying)).Service
                    .RollbackAsync(25, 1, TaskTypeStationActivationHarness.Catalog, TaskTypeStationActivationHarness.Request,
                        dryRun: false, TaskTypeStationActivationHarness.Now, Token);
            }));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        // R's target is version 3 (version 1's content written anew); A's late second step must not have touched it.
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|3", await harness.PointerRowAsync());
    }

    /// <summary>
    /// S2：未结尝试从仍成立的暂停里取，不按审计时间戳。同一目标版本多出一条时间更晚的「开始」审计（时钟回拨、或版本复用）也不会选错。
    /// </summary>
    [Fact]
    public async Task TheOpenAttemptIsTakenFromItsHoldsNotFromTheLatestAuditTimestamp()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        string attemptId = AttemptOf((await harness.HoldsAsync())[0]);
        // A later-stamped started record for the same target version, naming some other attempt.
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        await stack.Governance.WriteBusinessAsync(
            new GovernanceAuditEntry(
                TaskTypeStationActivationAuditActions.Started, GovernedObjectKind.PublicStationBinding, "map-25", 2,
                GovernanceActionOutcome.ResultUnknown,
                """{"attemptId":"ghost","bindingSetVersion":{"previous":1},"heldTaskTypes":[]}"""),
            TaskTypeStationActivationHarness.Now.AddHours(1),
            Token);

        TaskTypeStationReconciliationResult reconciled = await stack.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal(attemptId, reconciled.AttemptId);
        Assert.Equal(2, reconciled.ReleasedHoldIds.Count);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);
    }

    /// <summary>S3：一条找不到尝试的孤儿「激活结果未知」暂停，在对账能下结论时被撤掉，不会把那个任务类型永远暂停。</summary>
    [Fact]
    public async Task ReconciliationReleasesAnOrphanActivationHoldWhenItCanConclude()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await harness.ExecuteAsync(
            "INSERT INTO TaskTypeStationHolds (HoldId, MapId, TaskType, Source, ReasonCode, DetailJson, RaisedAt, RaisedBy) VALUES "
            + "('orphan', 25, 'WIRE_TO_GATE', 'ACTIVATION_RESULT_UNKNOWN', 'TASK_TYPE_ACTIVATION_RESULT_UNKNOWN', "
            + "'{\"attemptId\":\"lost\",\"targetVersion\":7,\"previousVersion\":1}', '2026-09-19 07:00:00+00:00', 'fieldops:activation:lost')");
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        Assert.True(await stack.Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));

        TaskTypeStationReconciliationResult reconciled = await stack.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.NothingToReconcile, reconciled.Conclusion);
        Assert.Equal(["orphan"], reconciled.ReleasedHoldIds);
        Assert.False(await harness.Default().Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
    }

    /// <summary>
    /// S3：「矛盾」时系统给不出结论，出口是一个带审计的人工收尾：该图回到「无生效版本」、撤全部「激活结果未知」暂停、记下理由与自报角色。
    /// 不是矛盾时拒绝收尾，什么都不改。
    /// </summary>
    [Fact]
    public async Task AContradictionIsClosedOnlyByAnAuditedManualClose()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        TaskTypeStationChangeRequest close = new("两个版本都读不回完整内容，放弃这次激活", "现场工程师");

        // Not contradictory yet: version 1 still reads back whole, so this is the reconciliation's call, not a person's.
        TaskTypeStationManualCloseResult refused = await stack.Service.CloseManuallyAsync(
            25, close, TaskTypeStationActivationHarness.Now, Token);
        Assert.Equal(TaskTypeStationManualCloseOutcome.Rejected, refused.Outcome);
        Assert.Equal(
            TaskTypeStationActivationReasonCodes.ActivationNotContradictory, Assert.Single(refused.Violations).ReasonCode);
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());

        await harness.ExecuteAsync("UPDATE TaskTypeStationBindings SET StationName = '关卡-改' WHERE MapId = 25 AND Version = 1");
        Assert.Equal(
            TaskTypeStationReconciliationConclusion.Contradictory,
            (await stack.Service.ReconcileAsync(25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token)).Conclusion);

        TaskTypeStationManualCloseResult closed = await harness.Default().Service.CloseManuallyAsync(
            25, close, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationManualCloseOutcome.Closed, closed.Outcome);
        Assert.Equal(1, closed.ActiveVersionBefore);
        Assert.Equal(2, closed.ReleasedHoldIds.Count);
        // Review round 2, N1: a tombstone, not a missing row -- the map has no active version, and a restart must not read
        // that as "never activated" and load the preset.
        Assert.Equal("25|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);
        BusinessAuditRecordRow[] records = [.. (await harness.AuditAsync())
            .Where(row => row.Action is TaskTypeStationActivationAuditActions.ClosedManually or TaskTypeStationActivationAuditActions.CloseRejected)];
        Assert.Equal(
            [
                (TaskTypeStationActivationAuditActions.CloseRejected, GovernanceActionOutcome.Failed),
                (TaskTypeStationActivationAuditActions.ClosedManually, GovernanceActionOutcome.Succeeded)
            ],
            records.Select(row => (row.Action, row.Outcome)));
        Assert.Contains("放弃这次激活", records[1].DetailJson, StringComparison.Ordinal);
        Assert.Contains("现场工程师", records[1].DetailJson, StringComparison.Ordinal);
        Assert.Equal(closed.AuditRecordId, records[1].AuditRecordId);
    }

    /// <summary>
    /// S4：一张图的第一次激活没有完成，对账判「原版本在用」时，原版本是「没有」——指针回到「无生效版本」形态（没有指针行），
    /// 而不是一个「生效却没有版本」的 <c>ACTIVE</c>。这样重启时预置文件仍能装为第一版（规格 21.2 第 4 条）。
    /// </summary>
    [Fact]
    public async Task AFirstActivationThatNeverCompletedLeavesTheMapWithoutAnActiveVersion()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        RiotMapStationCatalogSnapshot map26 = TaskTypeStationCatalogEvidence.Supplied(
            26, TaskTypeStationActivationHarness.CatalogStations, TaskTypeStationActivationHarness.Now);
        await harness.ConfirmCatalogAsync(map26, TaskTypeStationActivationHarness.Now.AddSeconds(-30));
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            new TaskTypeStationCandidate(26, 1, [TransportTaskTypes.WireToGate], [TaskTypeStationActivationHarness.Gate]),
            catalog: map26);
        Assert.Equal("25|1|ACTIVE|<null>\n26|<null>|ACTIVATION_UNKNOWN|1", await harness.PointerRowAsync());

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            26, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Null(reconciled.ActiveVersion);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
    }

    /// <summary>S5：第一步提交没有返回（等锁超时），不崩溃：写一条超时审计、结论是结果未知，库里什么都没变。</summary>
    [Fact]
    public async Task AFirstStepThatFailsToCommitIsAuditedAsTimedOutAndChangesNothing()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        CommitFault fault = new(new TimeoutException("SQLite BEGIN IMMEDIATE waited past the busy timeout.")) { Remaining = 1 };
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(fault), inner => new FaultOnBegin(inner, fault));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(1, fault.Thrown);
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.Empty(await harness.HoldsAsync());
        BusinessAuditRecordRow last = (await harness.AuditAsync())[^1];
        Assert.Equal((TaskTypeStationActivationAuditActions.ResultUnknown, GovernanceActionOutcome.TimedOut), (last.Action, last.Outcome));
        Assert.Contains(result.AttemptId, last.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>S5：第一步提交之后才抛出：该图已处在结果未知、暂停着，审计照写，对账接得上。</summary>
    [Fact]
    public async Task AFirstStepThatCommitsButThrowsIsAuditedAsUnknownAndReconcilable()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        CommitFault fault = new(new InvalidOperationException("The connection dropped after COMMIT was sent.")) { AfterCommit = true, Remaining = 1 };
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(fault), inner => new FaultOnBegin(inner, fault));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.HoldsAsync()).Count(hold => hold.ReleasedAt is null));
        BusinessAuditRecordRow last = (await harness.AuditAsync())[^1];
        Assert.Equal((TaskTypeStationActivationAuditActions.ResultUnknown, GovernanceActionOutcome.ResultUnknown), (last.Action, last.Outcome));
        Assert.Equal(
            TaskTypeStationReconciliationConclusion.PreviousActive,
            (await harness.Default().Service.ReconcileAsync(25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token)).Conclusion);
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);
    }

    /// <summary>S5：对账自己的提交失败：不崩溃，结论是「没能对账」，暂停与指针原样，审计照写。</summary>
    [Fact]
    public async Task AReconciliationThatFailsToCommitIsAuditedAndChangesNothing()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying)).ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));
        CommitFault fault = new(new TimeoutException("lock timeout")) { Armed = true, Remaining = 1 };
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(harness.NewContext(fault));

        TaskTypeStationReconciliationResult result = await stack.Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.NotConcluded, result.Conclusion);
        Assert.Equal(1, fault.Thrown);
        Assert.Empty(result.ReleasedHoldIds);
        Assert.Equal("25|1|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.HoldsAsync()).Count(hold => hold.ReleasedAt is null));
        BusinessAuditRecordRow last = (await harness.AuditAsync())[^1];
        Assert.Equal((TaskTypeStationActivationAuditActions.Reconciled, GovernanceActionOutcome.TimedOut), (last.Action, last.Outcome));
        Assert.Contains("\"conclusion\":\"NOT_CONCLUDED\"", last.DetailJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// S6：存储层自己挡非法值，不靠调用方记得先校验——直接调规则、绑定集、激活与暂停存储，传非法的 <c>FixedEnd</c>、站点、核对记录与
    /// <c>Source</c>，一律被拒、什么都没写。指针 <c>State</c> 没有任何端口接受外来取值，只有存储内部的两个常量。
    /// </summary>
    [Fact]
    public async Task StoresRefuseIllegalValuesEvenWhenCalledDirectly()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = harness.Default();
        IReadOnlyDictionary<string, long> before = await harness.CountRowsAsync();

        await Assert.ThrowsAsync<TaskTypeStationConfigurationException>(() => stack.Rules.WriteVersionAsync(
            [.. TaskTypeStationTestData.SixRules.Where(rule => rule.TaskType != TransportTaskTypes.WireToGate),
                new TaskTypeStationRule(TransportTaskTypes.WireToGate, "SIDEWAYS")],
            "direct", TaskTypeStationActivationHarness.Now, Token));
        await Assert.ThrowsAsync<TaskTypeStationConfigurationException>(() => stack.Rules.WriteVersionAsync(
            [.. TaskTypeStationTestData.SixRules, new TaskTypeStationRule("WIRE_TO_MOON", TaskTypeFixedEnd.Destination)],
            "direct", TaskTypeStationActivationHarness.Now, Token));
        await Assert.ThrowsAsync<TaskTypeStationConfigurationException>(() => stack.Bindings.WriteVersionAsync(
            25, 1, [TransportTaskTypes.WireToGate], [TaskTypeStationActivationHarness.Gate with { SiteVerificationRef = " " }],
            null, "direct", TaskTypeStationActivationHarness.Now, Token));
        await Assert.ThrowsAsync<TaskTypeStationConfigurationException>(() => stack.Bindings.WriteVersionAsync(
            25, 1, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            [TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging with { StationRiotId = 210, StationName = "关卡" }],
            null, "direct", TaskTypeStationActivationHarness.Now, Token));
        await Assert.ThrowsAsync<TaskTypeStationConfigurationException>(() => stack.Activations.BeginAsync(
            new TaskTypeStationActivationStart(
                "direct", new TaskTypeStationCandidate(25, 1, [TransportTaskTypes.WireToGate], [TaskTypeStationActivationHarness.Gate with { StationName = "" }]),
                1, null, "direct", [TransportTaskTypes.WireToGate], TaskTypeStationActivationHarness.Now),
            attempt => new GovernanceAuditEntry("DIRECT", GovernedObjectKind.PublicStationBinding, "map-25", attempt.TargetVersion,
                GovernanceActionOutcome.ResultUnknown, "{}"),
            Token));
        await Assert.ThrowsAsync<ArgumentException>(() => stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, "BOGUS", "X", "{}", "direct", TaskTypeStationActivationHarness.Now, Token));

        Assert.Equal(before, await harness.CountRowsAsync());
        Assert.Equal("25|1|ACTIVE|<null>", await harness.PointerRowAsync());
        Assert.DoesNotContain(
            new[] { typeof(ITaskTypeStationBindingStore), typeof(ITaskTypeStationActivationStore), typeof(ITaskTypeStationHoldStore) }
                .SelectMany(port => port.GetMethods())
                .SelectMany(method => method.GetParameters()),
            parameter => string.Equals(parameter.Name, "state", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>O2：解除暂停与它的审计在同一个事务里——审计写不进去，暂停就不算解除。</summary>
    [Fact]
    public async Task AHoldReleaseWhoseAuditCannotBeWrittenReleasesNothing()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(), wrapAudit: inner => new FailingAudit(inner, TaskTypeStationActivationAuditActions.HoldReleased));
        TaskTypeStationHold manual = await stack.Holds.RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            TaskTypeStationActivationHarness.Now.AddMinutes(-5), Token);

        TaskTypeStationHoldReleaseResult result = await stack.Service.ReleaseHoldAsync(
            25, TransportTaskTypes.WireToGate, "SITE-RECHECK-0919", TaskTypeStationActivationHarness.Catalog,
            TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationHoldReleaseOutcome.Rejected, result.Outcome);
        Assert.Contains(result.Violations, violation => violation.ReasonCode == TaskTypeStationActivationReasonCodes.NotCommitted);
        Assert.Null((await harness.HoldsAsync()).Single(hold => hold.HoldId == manual.HoldId).ReleasedAt);
        Assert.True(await harness.Default().Holds.IsHeldAsync(25, TransportTaskTypes.WireToGate, Token));
    }

    // ======== Review round 2 of PR #183 (issuecomment-5739928863): N1-N4 ========

    /// <summary>
    /// N1：人工收尾之后该图没有生效版本，直到下一次 FieldOps 激活或回滚——从墓碑出发的激活照常；它若又中断，对账判「原版本在用」时
    /// 回到墓碑，不是回到「从未激活」（那样重启就会把预置装上）。
    /// </summary>
    [Fact]
    public async Task AfterAManualCloseTheMapStaysWithoutAnActiveVersionUntilAnActivationEvenAcrossAnotherInterruptedOne()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await CloseAfterAContradictionAsync(harness);
        Assert.Equal("25|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());
        Assert.Null(await harness.Default().Bindings.ReadActiveAsync(25, Token));

        // An activation from the tombstone is interrupted, and reconciled: back to the tombstone.
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying))
            .ActivateAsync(TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Optical));
        Assert.StartsWith("25|<null>|ACTIVATION_UNKNOWN|", await harness.PointerRowAsync(), StringComparison.Ordinal);
        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal("25|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());

        // And an activation that completes ends the tombstone.
        TaskTypeStationActivationResult activated = await harness.Default().ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate));
        Assert.Equal(TaskTypeStationActivationOutcome.Activated, activated.Outcome);
        Assert.Null(activated.PreviousVersion);
        Assert.Equal(FormattableString.Invariant($"25|{activated.TargetVersion}|ACTIVE|<null>"), await harness.PointerRowAsync());
    }

    /// <summary>
    /// N2：A 的第二步失败，在 A 重标之前，对账已给出结论、B 已激活完成；A 的重标不得造出「未知、待定 A、生效 B」——
    /// 那会让对账判矛盾、人工收尾删掉合法生效的 B。A 按被超越处理，只写审计。
    /// </summary>
    [Fact]
    public async Task ARemarkThatArrivesAfterANewerActivationCompletedLeavesThatActivationAlone()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        CommitFault fault = new(new TimeoutException("lock timeout")) { Remaining = 1 };
        TaskTypeStationActivationResult? b = null;
        TaskTypeStationActivationHarness.Stack a = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(fault),
            inner => new BeforeRemark(new FaultOnComplete(inner, fault), async () =>
            {
                await harness.Default().Service.ReconcileAsync(
                    25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);
                b = await harness.Default().ActivateAsync(
                    TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Optical));
            }));

        TaskTypeStationActivationResult result = await a.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.Activated, b!.Outcome);
        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal(FormattableString.Invariant($"25|{b.TargetVersion}|ACTIVE|<null>"), await harness.PointerRowAsync());
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);
        Assert.Contains("overtaken", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>N3：第二步已提交，读回时等锁超时：不崩溃，按结果未知写审计、该图暂停着，对账读到目标版本。</summary>
    [Fact]
    public async Task AReadBackThatTimesOutAfterTheSecondStepCommittedIsAuditedAsUnknownAndReconcilable()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(
            harness.NewContext(), inner => new ReadBackFailsOnce(inner, new TimeoutException("SQLite busy past the timeout")));

        TaskTypeStationActivationResult result = await stack.ActivateAsync(
            TaskTypeStationActivationHarness.Candidate(TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging));

        Assert.Equal(TaskTypeStationActivationOutcome.ResultUnknown, result.Outcome);
        Assert.Equal("25|2|ACTIVATION_UNKNOWN|2", await harness.PointerRowAsync());
        Assert.Equal(2, (await harness.HoldsAsync()).Count(hold => hold.ReleasedAt is null));
        BusinessAuditRecordRow last = (await harness.AuditAsync())[^1];
        Assert.Equal((TaskTypeStationActivationAuditActions.ResultUnknown, GovernanceActionOutcome.TimedOut), (last.Action, last.Outcome));
        Assert.DoesNotContain(await harness.AuditAsync(), row => row.Action == TaskTypeStationActivationAuditActions.Activated);
        Assert.Equal(
            TaskTypeStationReconciliationConclusion.TargetActive,
            (await harness.Default().Service.ReconcileAsync(25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token)).Conclusion);
    }

    /// <summary>
    /// N4：#159 原测试的另一半——写入中途被数据库拒绝时整体回滚：版本头、快照、审计、绑定行一样都不留。
    /// </summary>
    [Fact]
    public async Task ABindingSetWriteRefusedByTheDatabaseHalfwayLeavesNothingBehind()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        IReadOnlyDictionary<string, long> before = await harness.CountRowsAsync();
        SaveFault fault = new(new Microsoft.EntityFrameworkCore.DbUpdateException("disk I/O error"), failOnSave: 2);
        TaskTypeStationActivationHarness.Stack stack = TaskTypeStationActivationHarness.StackOver(harness.NewContext(fault));

        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => stack.Bindings.WriteVersionAsync(
            25, 1, [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            [TaskTypeStationActivationHarness.Gate, TaskTypeStationActivationHarness.Staging],
            null, "direct", TaskTypeStationActivationHarness.Now, Token));

        Assert.True(fault.Failed);
        Assert.Equal(before, await harness.CountRowsAsync());
        Assert.Null(await harness.Default().Bindings.ReadVersionAsync(25, 2, Token));
    }

    // ======== control-server#191: follow-ups of PR #183 review round 3 ========

    /// <summary>
    /// cs#191 第 1 条：从墓碑出发、需求集为空的激活在两步之间中断，没有暂停可还原它的来历。对账读回「原版本在用」时回到墓碑，
    /// 不删指针行；重启后预置不生效，该图仍无生效版本，等 FieldOps 激活或回滚。
    /// </summary>
    [Fact]
    public async Task AnInterruptedActivationOfAnEmptyRequirementSetFromATombstoneReconcilesBackToTheTombstoneAndARestartKeepsThePresetOut()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        await CloseAfterAContradictionAsync(harness);
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying))
            .ActivateAsync(TaskTypeStationActivationHarness.Candidate());
        Assert.StartsWith("25|<null>|ACTIVATION_UNKNOWN|", await harness.PointerRowAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(await harness.HoldsAsync(), hold => hold.ReleasedAt is null);

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            25, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now.AddMinutes(1), Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal("25|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());

        List<string> logs = await RestartWithPresetAsync(harness, 25);

        Assert.Equal("25|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());
        Assert.Null(await harness.Default().Bindings.ReadActiveAsync(25, Token));
        Assert.Contains(logs, line => line.Contains("not applied", StringComparison.Ordinal));
    }

    /// <summary>
    /// cs#191 对照：一张从未激活过的图（没有指针行）、需求集为空，第一次激活在两步之间中断。没有暂停记着它的来历，分不清先前是墓碑
    /// 还是「从未激活」，按墓碑处理（fail-safe）：对账后留墓碑，重启不装预置。这是有意的取舍——代价是这张图要等一次 FieldOps 激活。
    /// </summary>
    [Fact]
    public async Task AnInterruptedFirstActivationOfAnEmptyRequirementSetIsTakenAsFromATombstoneAndARestartKeepsThePresetOut()
    {
        await using TaskTypeStationActivationHarness harness = await TaskTypeStationActivationHarness.CreateAsync();
        RiotMapStationCatalogSnapshot map26 = TaskTypeStationCatalogEvidence.Supplied(
            26, TaskTypeStationActivationHarness.CatalogStations, TaskTypeStationActivationHarness.Now);
        await harness.ConfirmCatalogAsync(map26, TaskTypeStationActivationHarness.Now.AddSeconds(-30));
        ControlServerDbContext dying = harness.NewContext();
        await TaskTypeStationActivationHarness.StackOver(dying, inner => new DieBeforeComplete(inner, dying))
            .ActivateAsync(new TaskTypeStationCandidate(26, 1, [], []), catalog: map26);
        Assert.StartsWith("25|1|ACTIVE|<null>\n26|<null>|ACTIVATION_UNKNOWN|", await harness.PointerRowAsync(), StringComparison.Ordinal);

        TaskTypeStationReconciliationResult reconciled = await harness.Default().Service.ReconcileAsync(
            26, TaskTypeStationActivationHarness.Request, TaskTypeStationActivationHarness.Now, Token);

        Assert.Equal(TaskTypeStationReconciliationConclusion.PreviousActive, reconciled.Conclusion);
        Assert.Equal("25|1|ACTIVE|<null>\n26|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());

        await RestartWithPresetAsync(harness, 26);

        Assert.Equal("25|1|ACTIVE|<null>\n26|<null>|CLOSED_MANUALLY|<null>", await harness.PointerRowAsync());
        Assert.Null(await harness.Default().Bindings.ReadActiveAsync(26, Token));
    }

    /// <summary>
    /// 同一台重新起的服务端：在 <paramref name="harness"/> 的库文件上，带一份把 <c>WIRE_TO_GATE</c> 绑到 210 的预置，跑一遍启动装载。
    /// 返回启动写的每一行日志。
    /// </summary>
    private static async Task<List<string>> RestartWithPresetAsync(TaskTypeStationActivationHarness harness, int mapId)
    {
        string presetPath = Path.Combine(Path.GetDirectoryName(harness.DatabasePath)!, TaskTypeStationPreset.FileName);
        await File.WriteAllTextAsync(presetPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            TaskTypeStations = new
            {
                rules = TaskTypeStationTestData.SixRules.Select(rule => new { taskType = rule.TaskType, fixedEnd = rule.FixedEnd }),
                mapId,
                requiredTaskTypes = new[] { TransportTaskTypes.WireToGate },
                bindings = new[]
                {
                    new
                    {
                        taskType = TaskTypeStationActivationHarness.Gate.TaskType,
                        stationRiotId = TaskTypeStationActivationHarness.Gate.StationRiotId,
                        stationName = TaskTypeStationActivationHarness.Gate.StationName,
                        siteVerificationRef = TaskTypeStationActivationHarness.Gate.SiteVerificationRef
                    }
                }
            }
        }), Token);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TaskTypeStationPreset.SettingsFileKey] = presetPath })
            .Build();
        List<string> logs = [];
        ServiceCollection services = new();
        services.AddLogging(logging => logging.AddProvider(new LineCapturingLoggerProvider(logs)));
        services.AddSingleton(configuration);
        services.AddSingleton(Options.Create(new JourneyRuntimeOptions { Enabled = true, MapId = mapId }));
        services.AddDbContext<ControlServerDbContext>(options =>
            options.UseSqlite(ControlServerSqlite.ForDatabaseFile(harness.DatabasePath, readOnly: false)));
        services.AddGovernance(configuration);
        services.AddTaskTypeStations();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await TaskTypeStationStartup.EnsureAsync(provider, Token);
        return logs;
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

    private static string AttemptOf(TaskTypeStationHoldRow hold)
    {
        using System.Text.Json.JsonDocument detail = System.Text.Json.JsonDocument.Parse(hold.DetailJson);
        return detail.RootElement.GetProperty("attemptId").GetString()!;
    }
}

/// <summary>
/// The process dies right after the first step commits: the connection goes away, and nothing the service tries afterwards
/// (re-marking, the result-unknown audit) reaches the database.
/// </summary>
internal sealed class DieBeforeComplete(ITaskTypeStationActivationStore inner, ControlServerDbContext context)
    : DelegatingActivationStore(inner)
{
    public override async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await context.DisposeAsync();
        throw new OperationCanceledException("The process was terminated between the two steps.");
    }
}

/// <summary>Runs <paramref name="afterComplete"/> right after the real second step commits, as a racing writer would.</summary>
internal sealed class AfterComplete(ITaskTypeStationActivationStore inner, Func<Task> afterComplete)
    : DelegatingActivationStore(inner)
{
    public override async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await base.CompleteAsync(attempt, at, cancellationToken);
        await afterComplete();
    }
}

/// <summary>Arms <see cref="CommitFault"/> for exactly the second step, so the first step commits for real.</summary>
internal sealed class FaultOnComplete(ITaskTypeStationActivationStore inner, CommitFault fault) : DelegatingActivationStore(inner)
{
    public override async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        fault.Armed = true;
        try
        {
            await base.CompleteAsync(attempt, at, cancellationToken);
        }
        finally
        {
            fault.Armed = false;
        }
    }
}

/// <summary>Runs <paramref name="beforeComplete"/> just before the real second step, as whatever overtook it would.</summary>
internal sealed class BeforeComplete(ITaskTypeStationActivationStore inner, Func<Task> beforeComplete)
    : DelegatingActivationStore(inner)
{
    public override async Task CompleteAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await beforeComplete();
        await base.CompleteAsync(attempt, at, cancellationToken);
    }
}

/// <summary>Arms <see cref="CommitFault"/> for exactly the first step.</summary>
internal sealed class FaultOnBegin(ITaskTypeStationActivationStore inner, CommitFault fault) : DelegatingActivationStore(inner)
{
    public override async Task<TaskTypeStationActivationAttempt> BeginAsync(
        TaskTypeStationActivationStart start,
        Func<TaskTypeStationActivationAttempt, GovernanceAuditEntry> startedAudit,
        CancellationToken cancellationToken)
    {
        fault.Armed = true;
        try
        {
            return await base.BeginAsync(start, startedAudit, cancellationToken);
        }
        finally
        {
            fault.Armed = false;
        }
    }
}

/// <summary>An audit writer that cannot write one action, as a full disk or a lost connection would.</summary>
internal sealed class FailingAudit(IGovernanceAuditWriter inner, string failingAction) : IGovernanceAuditWriter
{
    public Task<string> WriteBusinessAsync(GovernanceAuditEntry entry, DateTimeOffset recordedAt, CancellationToken cancellationToken) =>
        entry.Action == failingAction
            ? throw new IOException($"The audit record {failingAction} could not be written.")
            : inner.WriteBusinessAsync(entry, recordedAt, cancellationToken);

    public Task<string> WriteAdministratorAsync(GovernanceAuditEntry entry, DateTimeOffset recordedAt, CancellationToken cancellationToken) =>
        inner.WriteAdministratorAsync(entry, recordedAt, cancellationToken);
}

/// <summary>Throws from the database's own commit, armed only while the second step runs.</summary>
internal sealed class CommitFault(Exception toThrow) : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
{
    public bool Armed { get; set; }

    public int Thrown { get; private set; }

    /// <summary>How many more commits may be failed while armed; the default is every one.</summary>
    public int Remaining { get; set; } = int.MaxValue;

    /// <summary>Throw once the commit has gone through, instead of before it.</summary>
    public bool AfterCommit { get; init; }

    public override Task TransactionCommittedAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (Armed && AfterCommit && Remaining > 0)
        {
            Remaining--;
            Thrown++;
            throw toThrow;
        }
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
        System.Data.Common.DbTransaction transaction,
        Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (Armed && !AfterCommit && Remaining > 0)
        {
            Remaining--;
            Thrown++;
            throw toThrow;
        }
        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }
}

/// <summary>Runs <paramref name="beforeRemark"/> just before the real re-marking, as whatever overtook it would.</summary>
internal sealed class BeforeRemark(ITaskTypeStationActivationStore inner, Func<Task> beforeRemark) : DelegatingActivationStore(inner)
{
    public override async Task<TaskTypeStationActivationAttempt> MarkUnknownAsync(
        TaskTypeStationActivationAttempt attempt, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await beforeRemark();
        return await base.MarkUnknownAsync(attempt, at, cancellationToken);
    }
}

/// <summary>The first read-back throws, as a lock wait past the busy timeout would; later ones go through.</summary>
internal sealed class ReadBackFailsOnce(ITaskTypeStationActivationStore inner, Exception failure) : DelegatingActivationStore(inner)
{
    private bool _failed;

    public override Task<TaskTypeStationActiveReadBack> ReadBackAsync(int mapId, CancellationToken cancellationToken)
    {
        if (!_failed)
        {
            _failed = true;
            throw failure;
        }
        return base.ReadBackAsync(mapId, cancellationToken);
    }
}

/// <summary>Fails the n-th SaveChanges on its context, from inside the database write.</summary>
internal sealed class SaveFault(Exception toThrow, int failOnSave) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
{
    private int _saves;

    public bool Failed { get; private set; }

    public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
        Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
        Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (++_saves == failOnSave)
        {
            Failed = true;
            throw toThrow;
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

/// <summary>Collects every formatted log line, in order, for a startup run against the harness's database.</summary>
internal sealed class LineCapturingLoggerProvider(List<string> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LineCapturingLogger(lines);

    public void Dispose()
    {
    }

    private sealed class LineCapturingLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}
