using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;
using static ControlServer.Tests.ChargingGovernanceHarness;
using static ControlServer.Tests.WaitingPointImportHarness;

namespace ControlServer.Tests;

/// <summary>
/// <c>ChargingPolicyVersion</c> 的导入、批准、激活与按周期冻结（control-server#400，批次9-02；REQ-0281、REQ-0282）：整份校验、阈值硬关系、
/// 没有缺省值、未批准不能激活、测试批准不冒充现场批准、激活的 diff 列出失去策略的车、新版本不改已开始周期的快照、读不到按没有。
/// 真 SQLite 文件，真治理发布。
/// </summary>
public sealed class ChargingPolicyGovernanceTests
{
    [Fact]
    public async Task AValidPolicyBecomesAVersionThatIsNeitherApprovedNorActive()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        ChargingPolicyImportResult imported = await harness.ImportPolicyAsync(PolicyFile());

        Assert.Equal((ChargingPolicyOperationOutcome.Accepted, 1L), (imported.Outcome, imported.Version!.Version));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(TestChargingPolicies.Content), System.Text.Json.JsonSerializer.Serialize(imported.Version.Content));
        Assert.Equal((1L, 0L, 0L, 0L, 1L, 1L, 0L, 0L), await harness.PolicyFootprintAsync());
        Assert.Null(await ResolveAsync(harness, VehicleA));
    }

    /// <summary>REQ-0281 的三种关系违例：完成线等于入口线、入口线低于余量、三者倒过来。各整份拒绝、一行都不写。</summary>
    [Theory]
    [InlineData(30, 80, 80, 1)]
    [InlineData(30, 20, 80, 1)]
    [InlineData(80, 30, 20, 2)]
    public async Task EachThresholdRelationViolationRejectsTheWholeFile(int margin, int entry, int completion, int violations)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        ChargingPolicyImportResult result = await harness.ImportPolicyAsync(PolicyFile(margin: margin, entry: entry, completion: completion));

        Assert.Equal(ChargingPolicyOperationOutcome.Rejected, result.Outcome);
        Assert.Equal(violations, result.Errors.Count(error => error.ReasonCode == ChargingPolicyReasonCodes.ThresholdRelationViolated));
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L), await harness.PolicyFootprintAsync());
    }

    /// <summary>关系校验只有一份：<see cref="ChargingPolicyRules.ThresholdRelationViolations"/>，边界上 80 &gt; 30 &gt;= 30 成立。</summary>
    [Theory]
    [InlineData(80, 30, 30, 0)]
    [InlineData(80, 30, 20, 0)]
    [InlineData(31, 30, 30, 0)]
    [InlineData(30, 30, 30, 1)]
    [InlineData(80, 29, 30, 1)]
    [InlineData(20, 30, 80, 2)]
    public void TheOneThresholdRelationFunctionDecidesTheBoundaries(int completion, int entry, int margin, int violations)
    {
        Assert.Equal(violations, ChargingPolicyRules.ThresholdRelationViolations(completion, entry, margin).Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("string-number")]
    [InlineData("range-percent")]
    [InlineData("range-seconds")]
    [InlineData("increase-zero")]
    [InlineData("fleet")]
    public async Task AMissingFieldAnOutOfRangeValueOrAVehicleOutsideTheFleetRejectsTheWholeFile(string kind)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        string valid = PolicyFile();
        string file = kind switch
        {
            // No default value stands in for a missing field (REQ-0282).
            "missing" => valid.Replace("\"estimatedTaskConsumptionPercent\":0,", string.Empty, StringComparison.Ordinal),
            "extra" => valid.Replace("{", "{\"minimumBatteryPercent\":30,", StringComparison.Ordinal),
            "string-number" => valid.Replace("\"chargingCompletionThresholdPercent\":80", "\"chargingCompletionThresholdPercent\":\"80\"", StringComparison.Ordinal),
            "range-percent" => PolicyFile(completion: 101),
            "range-seconds" => PolicyFile(window: 0),
            "increase-zero" => PolicyFile(increase: 0),
            _ => PolicyFile(scope: ["VK-Z"]),
        };
        Assert.NotEqual(valid, file);

        ChargingPolicyImportResult result = await harness.ImportPolicyAsync(file);

        Assert.Equal(ChargingPolicyOperationOutcome.Rejected, result.Outcome);
        Assert.NotEmpty(result.Errors);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L), await harness.PolicyFootprintAsync());
    }

    [Fact]
    public async Task ADryRunWritesNothingAndTheSameFileAgainIsUnchanged()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();

        ChargingPolicyImportResult preview = await harness.ImportPolicyAsync(PolicyFile(), dryRun: true);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 0L, 0L, 0L), await harness.PolicyFootprintAsync());
        await harness.ImportPolicyAsync(PolicyFile());
        ChargingPolicyImportResult again = await harness.ImportPolicyAsync(PolicyFile());

        Assert.Equal((ChargingPolicyOperationOutcome.Accepted, (ChargingPolicyVersion?)null), (preview.Outcome, preview.Version));
        Assert.Equal((ChargingPolicyOperationOutcome.Unchanged, 1L), (again.Outcome, again.Version!.Version));
        Assert.Equal(1L, (await harness.PolicyFootprintAsync()).Versions);
    }

    [Fact]
    public async Task AnUnapprovedVersionCannotBeActivated()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportPolicyAsync(PolicyFile());

        ChargingPolicyActivationResult refused = await harness.ActivatePolicyAsync(1);

        Assert.Equal(ChargingPolicyOperationOutcome.Rejected, refused.Outcome);
        Assert.Equal(ChargingPolicyReasonCodes.VersionNotApproved, Assert.Single(refused.Errors).ReasonCode);
        Assert.Equal((1L, 0L, 0L, 0L, 1L, 1L, 0L, 0L), await harness.PolicyFootprintAsync());
        Assert.Null(await ResolveAsync(harness, VehicleA));
    }

    /// <summary>
    /// 批准与激活各留一条审计；只有测试夹具或 L2 预置批准的版本，激活要显式开关，不带就被拒——生产库不会不知不觉地用上测试批准的策略。
    /// </summary>
    [Fact]
    public async Task ApprovalAndActivationAreRecordedAndATestOnlyApprovalNeedsTheExplicitSwitch()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportPolicyAsync(PolicyFile());

        ChargingPolicyApprovalResult approved = await harness.ApprovePolicyAsync(1, ChargingPolicyApprovalSources.L2Preset);
        ChargingPolicyActivationResult withoutSwitch = await harness.ActivatePolicyAsync(1);
        ChargingPolicyActivationResult withSwitch = await harness.ActivatePolicyAsync(1, allowNonFieldApproval: true);

        Assert.Equal((ChargingPolicyOperationOutcome.Accepted, ChargingPolicyApprovalSources.L2Preset), (approved.Outcome, approved.Approval!.Source));
        Assert.Equal(ChargingPolicyReasonCodes.OnlyNonFieldApproval, Assert.Single(withoutSwitch.Errors).ReasonCode);
        Assert.Equal((ChargingPolicyOperationOutcome.Accepted, 1L), (withSwitch.Outcome, withSwitch.Activation!.Sequence));
        Assert.Equal((1L, 0L, 1L, 1L, 1L, 1L, 1L, 1L), await harness.PolicyFootprintAsync());
        Assert.Equal(1L, (await ResolveAsync(harness, VehicleA))!.Policy.Version);
    }

    [Fact]
    public async Task AFieldApprovalActivatesWithoutTheSwitchAndAnUnknownSourceIsRefused()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportPolicyAsync(PolicyFile());

        ChargingPolicyApprovalResult unknown = await harness.ApprovePolicyAsync(1, "DEVELOPER");
        await harness.ApprovePolicyAsync(1);
        ChargingPolicyActivationResult activated = await harness.ActivatePolicyAsync(1);
        ChargingPolicyActivationResult again = await harness.ActivatePolicyAsync(1);

        Assert.Equal(ChargingPolicyReasonCodes.ApprovalSourceUnknown, Assert.Single(unknown.Errors).ReasonCode);
        Assert.Equal(ChargingPolicyOperationOutcome.Accepted, activated.Outcome);
        Assert.Equal(ChargingPolicyOperationOutcome.Unchanged, again.Outcome);
        Assert.Equal((1L, 0L, 1L, 1L, 1L, 1L, 1L, 1L), await harness.PolicyFootprintAsync());
    }

    /// <summary>
    /// 策略按「全局一个生效版本」存：激活一版只覆盖 VK-A 的策略，导入与激活的输出都点名 VK-B 会失去策略；激活之后 VK-B 确实读不到。
    /// </summary>
    [Fact]
    public async Task ActivatingAPolicyThatCoversFewerVehiclesNamesTheVehiclesThatLoseTheirPolicy()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportPolicyAsync(PolicyFile());
        await harness.ApprovePolicyAsync(1);
        await harness.ActivatePolicyAsync(1);

        ChargingPolicyImportResult narrower = await harness.ImportPolicyAsync(PolicyFile(scope: [VehicleA]));
        await harness.ApprovePolicyAsync(2);
        ChargingPolicyActivationResult preview = await harness.ActivatePolicyAsync(2, dryRun: true);
        ChargingPolicyActivationResult activated = await harness.ActivatePolicyAsync(2);

        foreach (ChargingPolicyImpact impact in (ChargingPolicyImpact[])[narrower.Impact!, preview.Impact!, activated.Impact!])
        {
            Assert.Equal(1L, impact.ComparedWith);
            Assert.Equal([VehicleB], impact.VehiclesLosingPolicy);
            Assert.Equal([VehicleB], impact.VehiclesWithoutPolicyAfter);
            Assert.Equal("vehicleScope", Assert.Single(impact.Changes).Field);
        }
        Assert.Null(preview.Activation);
        Assert.Equal(2L, (await ResolveAsync(harness, VehicleA))!.Policy.Version);
        Assert.Null(await ResolveAsync(harness, VehicleB));
    }

    /// <summary>
    /// 按周期冻结：周期记下版本 1，之后激活版本 2——按旧版本号读回的快照逐字不变，周期上记的版本号不变，新决定用版本 2；
    /// 激活的输出列出这个周期（它不受影响）。
    /// </summary>
    [Fact]
    public async Task ActivatingANewVersionLeavesTheFrozenSnapshotAndTheStartedCycleExactlyAsTheyWere()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportPolicyAsync(PolicyFile());
        await harness.ApprovePolicyAsync(1);
        await harness.ActivatePolicyAsync(1);
        await harness.ImportRosterAsync(Roster211());
        await using (ControlServerDbContext context = harness.Open())
        {
            VehicleChargingPolicyDecision decision = await new ChargingPolicyResolver(PolicyStore(context)).ResolveForNewDecisionAsync(VehicleA, Token);
            await harness.StartCycleAsync("C-1", VehicleA, rosterVersion: 1, policyVersion: decision.PolicyVersion!.Value);
        }
        string frozenBefore = System.Text.Json.JsonSerializer.Serialize(await ReadFrozenAsync(harness, 1));
        string cycleBefore = await harness.DumpAsync("ChargingCycles");

        await harness.ImportPolicyAsync(PolicyFile(completion: 90, entry: 40, margin: 20));
        await harness.ApprovePolicyAsync(2);
        ChargingPolicyActivationResult activated = await harness.ActivatePolicyAsync(2);

        Assert.Equal("C-1", Assert.Single(activated.Impact!.OpenCycles).CycleId);
        Assert.Equal(frozenBefore, System.Text.Json.JsonSerializer.Serialize(await ReadFrozenAsync(harness, 1)));
        Assert.Equal(cycleBefore, await harness.DumpAsync("ChargingCycles"));
        Assert.Equal(2L, (await ResolveAsync(harness, VehicleA))!.Policy.Version);
        await using ControlServerDbContext read = harness.Open();
        Assert.Equal(1L, (await new ChargingCycleStore(read).ReadAsync("C-1", Token))!.ChargingPolicyVersion);
    }

    /// <summary>崩溃点：批准行与它的审计、激活行与它的审计各在同一个事务里，提交前崩溃就都不在。</summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("activate")]
    public async Task ACrashBeforeCommitLeavesNeitherTheApprovalOrActivationNorItsAudit(string step)
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await harness.ImportPolicyAsync(PolicyFile());
        if (step == "activate")
        {
            await harness.ApprovePolicyAsync(1);
        }
        (long, long, long, long, long, long, long, long) before = await harness.PolicyFootprintAsync();
        string table = step == "approve" ? "ChargingPolicyApprovals" : "ChargingPolicyActivations";
        string action = step == "approve" ? ChargingPolicyAudit.ApprovedAction : ChargingPolicyAudit.ActivatedAction;
        CrashOnCommit crash = new($"SELECT (SELECT COUNT(*) FROM {table}) + (SELECT COUNT(*) FROM BusinessAuditRecords WHERE Action = '{action}')");

        await using (ControlServerDbContext context = harness.Open(crash))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(Token);
            await Assert.ThrowsAsync<ProcessCrashed>(async () =>
            {
                ChargingPolicyGovernanceService service = PolicyService(context);
                _ = step == "approve"
                    ? (object)await service.ApproveAsync(1, "Zhengyu Shao", "PRODUCT_OWNER", "test", ChargingPolicyApprovalSources.Field, Now, Token)
                    : await service.ActivateAsync(1, "Zhengyu Shao", Fleet, false, false, Now, Token);
                await transaction.CommitAsync(Token);
            });
        }

        Assert.Equal(2L, crash.CountInsideTheTransaction);
        Assert.Equal(before, await harness.PolicyFootprintAsync());
    }

    /// <summary>FAILED／UNKNOWN 分支：读策略时库抛异常，判定按「没有」处理（fail-closed），不向外抛。</summary>
    [Fact]
    public async Task APolicyThatCannotBeReadIsNoPolicy()
    {
        VehicleChargingPolicyDecision decision =
            await new ChargingPolicyResolver(new ThrowingPolicyStore()).ResolveForNewDecisionAsync(VehicleA, Token);

        Assert.Equal((false, ChargingPolicyCommissioningReasons.Unreadable, (long?)null), (decision.Commissioned, decision.Reason, decision.PolicyVersion));
        Assert.Equal("database is locked", decision.Detail);
    }

    /// <summary>服务端出厂不带任何策略版本：一个新库里每辆车都不投运，而判定本身不出错。</summary>
    [Fact]
    public async Task AFreshDatabaseHasNoPolicyAndEveryVehicleIsNotCommissioned()
    {
        await using WaitingPointImportHarness harness = await CreateAsync();
        await using ControlServerDbContext context = harness.Open();
        ChargingPolicyResolver resolver = new(PolicyStore(context));

        foreach (string vehicleKey in Fleet)
        {
            VehicleChargingPolicyDecision decision = await resolver.ResolveForNewDecisionAsync(vehicleKey, Token);
            Assert.Equal((false, ChargingPolicyCommissioningReasons.NotApproved), (decision.Commissioned, decision.Reason));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ReadFrozenAsync(1, Token));
    }

    private static async Task<EffectiveChargingPolicy?> ResolveAsync(WaitingPointImportHarness harness, string vehicleKey)
    {
        await using ControlServerDbContext context = harness.Open();
        return (await new ChargingPolicyResolver(PolicyStore(context)).ResolveForNewDecisionAsync(vehicleKey, Token)).Effective;
    }

    private static async Task<ChargingPolicyVersion> ReadFrozenAsync(WaitingPointImportHarness harness, long version)
    {
        await using ControlServerDbContext context = harness.Open();
        return await new ChargingPolicyResolver(PolicyStore(context)).ReadFrozenAsync(version, Token);
    }

    private sealed class ThrowingPolicyStore : IChargingPolicyStore
    {
        public Task<EffectiveChargingPolicy?> ReadEffectiveForVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database is locked");

        public Task<ChargingPolicyVersion> WriteVersionAsync(ChargingPolicyContent content, string? changeNote, DateTimeOffset writtenAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChargingPolicyApproval> ApproveAsync(long version, string approvedBy, string approverRole, DateTimeOffset approvedAt, string basisReference, string source, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChargingPolicyActivation> ActivateAsync(long version, string activatedBy, DateTimeOffset activatedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ChargingPolicyVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChargingPolicyApproval>> ListApprovalsAsync(long version, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
