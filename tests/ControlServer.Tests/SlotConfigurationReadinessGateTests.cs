using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 逐仓核验与每车 IO 完整性门禁（REQ-0259、REQ-0262、REQ-0263）。
/// </summary>
/// <remarks>
/// 门禁是**每车判定**：一台车之内不许有缺口，也不许抽样；三台现有车之间互不牵连，逐台推进。
/// </remarks>
public sealed class SlotConfigurationReadinessGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OneVehicleFailingTheGateSaysNothingAboutAnotherVehicle()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();

        // AGV-01 全八仓绑定齐、逐仓核验过；AGV-02 一个仓位都还没开始。
        await fixture.BindAllSlotsAsync("AGV-01", model);
        await fixture.VerifyAllSlotsAsync("AGV-01", model);

        SlotConfigurationReadinessVerdict first = await fixture.Gate.EvaluateAsync(
            "AGV-01", model, TestContext.Current.CancellationToken);
        SlotConfigurationReadinessVerdict second = await fixture.Gate.EvaluateAsync(
            "AGV-02", model, TestContext.Current.CancellationToken);

        Assert.True(first.Ready);
        Assert.Equal(SlotReadinessReasonCode.Ready, first.ReasonCode);
        Assert.False(second.Ready);
        Assert.Equal(SlotReadinessReasonCode.IoBindingIncomplete, second.ReasonCode);
        Assert.Equal(Enumerable.Range(1, 8), second.SlotsMissingBinding);

        // 把 AGV-02 也推到位，AGV-01 的判定一个字不变——两台车的判定之间没有任何耦合。
        await fixture.BindAllSlotsAsync("AGV-02", model);
        await fixture.VerifyAllSlotsAsync("AGV-02", model);
        Assert.True((await fixture.Gate.EvaluateAsync("AGV-02", model, TestContext.Current.CancellationToken)).Ready);
        Assert.Equal(
            first,
            await fixture.Gate.EvaluateAsync("AGV-01", model, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AVehicleWithOneSlotMissingItsBindingCannotBecomeBusinessAvailable()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();

        // 八个仓位只绑了七个——差一个就是差。
        await fixture.BindSlotsAsync("AGV-01", model, Enumerable.Range(1, 7));

        SlotConfigurationReadinessVerdict verdict = await fixture.Gate.EvaluateAsync(
            "AGV-01", model, TestContext.Current.CancellationToken);
        Assert.False(verdict.Ready);
        Assert.Equal([8], verdict.SlotsMissingBinding);

        // 这条判定直接是业务就绪的一项证据，所以这台车拿不到业务就绪。
        AgvLifecycleFacts facts = new("AGV-01", 1, Archived: false, Commissioned: true, HasCandidateRiotBinding: true);
        AgvGenerationEvidence evidence = new(
            RiotBindingConfirmed: true,
            SlotConfigurationReadinessConfirmed: await fixture.Gate.IsSlotConfigurationConfirmedAsync(
                "AGV-01", model, TestContext.Current.CancellationToken),
            SafetyChainConfirmed: true);
        Assert.False(BusinessAvailability.IsAvailable(facts, evidence));
        Assert.Contains(BusinessAvailabilityBlocker.GenerationEvidenceNegative,
            BusinessAvailability.Evaluate(facts, evidence));
    }

    [Fact]
    public async Task SamplingIsRejectedAndEveryOneOfTheVehiclesSlotsIsConfirmedOneByOne()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync("AGV-01", model);

        // 八仓测了三仓就报核验通过：整批拒绝，不是留一份待办清单。
        SampledVerificationRejectedException failure =
            await Assert.ThrowsAsync<SampledVerificationRejectedException>(() =>
                fixture.Gate.RecordVerificationAsync(
                    "AGV-01",
                    model,
                    [.. Enumerable.Range(1, 3).Select(Confirmation)],
                    Now,
                    TestContext.Current.CancellationToken));
        Assert.Contains("REQ-0263", failure.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Context.Set<SlotConfigurationVerificationRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        // 逐仓确认要三个信号都是「是」：只确认了开与关的那一仓不算过。
        await fixture.Gate.RecordVerificationAsync(
            "AGV-01",
            model,
            [
                .. Enumerable.Range(1, 7).Select(Confirmation),
                new SlotVerificationConfirmation(8, true, true, InPlaceSignalConfirmed: false)
            ],
            Now,
            TestContext.Current.CancellationToken);
        SlotConfigurationReadinessVerdict verdict = await fixture.Gate.EvaluateAsync(
            "AGV-01", model, TestContext.Current.CancellationToken);
        Assert.False(verdict.Ready);
        Assert.Equal(SlotReadinessReasonCode.VerificationNegative, verdict.ReasonCode);
        Assert.Equal([8], verdict.SlotsVerifiedNegative);
    }

    [Fact]
    public async Task AnyHardwareRelatedChangeSendsTheVehicleBackForReverificationAndTheEarlierReadinessLapses()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync("AGV-01", model);
        await fixture.VerifyAllSlotsAsync("AGV-01", model);
        Assert.True((await fixture.Gate.EvaluateAsync("AGV-01", model, TestContext.Current.CancellationToken)).Ready);

        await fixture.Gate.InvalidateForHardwareChangeAsync(
            "AGV-01", model, Now.AddHours(1), TestContext.Current.CancellationToken);

        SlotConfigurationReadinessVerdict after = await fixture.Gate.EvaluateAsync(
            "AGV-01", model, TestContext.Current.CancellationToken);
        Assert.False(after.Ready);
        Assert.Equal(SlotReadinessReasonCode.HardwareChanged, after.ReasonCode);

        // 此前的核验记录整批作废：重新核验就是重新逐仓核验，不是补一个签字。
        Assert.Empty(await fixture.Context.Set<SlotConfigurationVerificationRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));

        // 重新逐仓核验之后就绪才回来。
        await fixture.VerifyAllSlotsAsync("AGV-01", model, Now.AddHours(2));
        Assert.True((await fixture.Gate.EvaluateAsync("AGV-01", model, TestContext.Current.CancellationToken)).Ready);
    }

    [Fact]
    public async Task WholeVehicleMaintenanceCannotBeEnteredWhileTheServerLinkIsDown()
    {
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync("AGV-01", model);
        await fixture.VerifyAllSlotsAsync("AGV-01", model);

        // 断线：服务端手上没有这台车的就绪会话。REQ-0262 的核验因此拿不到「在线」这个前提。
        VehicleMaintenanceRequiresServerLinkException offline =
            await Assert.ThrowsAsync<VehicleMaintenanceRequiresServerLinkException>(() =>
                fixture.Gate.EnterWholeVehicleMaintenanceAsync(
                    "AGV-01", model, Now, TestContext.Current.CancellationToken));
        Assert.Contains("REQ-0262", offline.Message, StringComparison.Ordinal);

        // 被拒之后这台车的就绪一个字没变——失败的进入尝试不是一次半进入。
        Assert.True((await fixture.Gate.EvaluateAsync("AGV-01", model, TestContext.Current.CancellationToken)).Ready);

        // 会话在线之后同一次调用才成立，并且立刻让就绪失效：维护本来就是要动硬件。
        await fixture.MarkSessionReadyAsync("AGV-01");
        await fixture.Gate.EnterWholeVehicleMaintenanceAsync(
            "AGV-01", model, Now, TestContext.Current.CancellationToken);
        SlotConfigurationReadinessVerdict during = await fixture.Gate.EvaluateAsync(
            "AGV-01", model, TestContext.Current.CancellationToken);
        Assert.False(during.Ready);
        Assert.Equal(SlotReadinessReasonCode.WholeVehicleMaintenance, during.ReasonCode);
    }

    [Fact]
    public void TheOnboardSideHasNoEntryOfItsOwnIntoWholeVehicleMaintenance()
    {
        // 上一条测的是断线时被拒；这一条测的是**根本没有第二个入口**——车载端消息处理不认识这个门禁，
        // 所以「断线时自行进入」不是一条被守住的路，而是一条不存在的路。
        string root = FindRepositoryRoot();
        string processor = File.ReadAllText(
            Path.Combine(root, "src", "ControlServer.Host", "Transport", "OnboardMessageProcessor.cs"));
        Assert.DoesNotContain(nameof(SlotConfigurationReadinessGate), processor, StringComparison.Ordinal);
        Assert.DoesNotContain("WholeVehicleMaintenance", processor, StringComparison.Ordinal);

        foreach (string file in SourceFiles(Path.Combine(root, "src", "ControlServer.Host", "Transport")))
        {
            Assert.DoesNotContain(
                nameof(SlotConfigurationReadinessGate),
                File.ReadAllText(file),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BringingTheGateOnlineLeavesAnAlreadyVerifiedVehicleExactlyWhereItWas()
    {
        // 「先核对后启用」这条零停产排法的代码前提：判定是现算的，读模型只是它的一份留痕。所以一台
        // 已经逐仓核对过的车，在门禁第一次运行的那一刻本来就是就绪的——门禁没有让它掉出来过。
        await using GateFixture fixture = await GateFixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync("AGV-01", model);
        await fixture.VerifyAllSlotsAsync("AGV-01", model);

        // 模拟门禁尚未上线：把读模型整行删掉，只留下核对事实本身。
        fixture.Context.Set<SlotConfigurationReadinessRow>().RemoveRange(
            await fixture.Context.Set<SlotConfigurationReadinessRow>()
                .ToArrayAsync(TestContext.Current.CancellationToken));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // 门禁上线的第一次判定：就绪，不需要任何补录动作。
        SlotConfigurationReadinessVerdict first = await fixture.Gate.RefreshReadinessAsync(
            "AGV-01", model, Now.AddDays(1), TestContext.Current.CancellationToken);
        Assert.True(first.Ready);

        SlotConfigurationReadinessRow persisted = await fixture.Context.Set<SlotConfigurationReadinessRow>()
            .AsNoTracking()
            .SingleAsync(row => row.AgvId == "AGV-01", TestContext.Current.CancellationToken);
        Assert.True(persisted.Ready);
        Assert.Null(persisted.InvalidatedAt);
    }

    private static SlotVerificationConfirmation Confirmation(int slot) =>
        new(slot, true, true, true, FieldRecordReference: $"W1-{slot}");

    private static string[] SourceFiles(string root) =>
        [.. Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test binary.");
    }

    private sealed class GateFixture : IAsyncDisposable
    {
        private GateFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            SlotConfigurationAuthorityStore authority,
            SlotConfigurationReadinessGate gate)
        {
            Connection = connection;
            Context = context;
            Authority = authority;
            Gate = gate;
        }

        private SqliteConnection Connection { get; }

        public ControlServerDbContext Context { get; }

        public SlotConfigurationAuthorityStore Authority { get; }

        public SlotConfigurationReadinessGate Gate { get; }

        public static async Task<GateFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            GovernanceStore governance = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            SlotConfigurationAuthorityStore authority = new(
                context, new GovernedConfigurationPublisher(governance, governance));
            return new GateFixture(connection, context, authority, new SlotConfigurationReadinessGate(context, governance));
        }

        public async Task<string> PublishApprovedModelAsync()
        {
            SlotModelVersionRow model = await Authority.EnsureApprovedHardwareFactsAsync(
                Now, TestContext.Current.CancellationToken);
            return model.SlotModelVersionId;
        }

        public Task BindAllSlotsAsync(string agvId, string slotModelVersionId) =>
            BindSlotsAsync(agvId, slotModelVersionId, Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount));

        public async Task BindSlotsAsync(string agvId, string slotModelVersionId, IEnumerable<int> slots) =>
            await Authority.PublishIoBindingsAsync(
                agvId,
                slotModelVersionId,
                [.. ApprovedSlotHardwareFacts.IoBindings.Where(binding => slots.Contains(binding.PhysicalSlotNumber))],
                Now,
                TestContext.Current.CancellationToken);

        public Task VerifyAllSlotsAsync(string agvId, string slotModelVersionId, DateTimeOffset? at = null) =>
            Gate.RecordVerificationAsync(
                agvId,
                slotModelVersionId,
                [.. Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount).Select(Confirmation)],
                at ?? Now,
                TestContext.Current.CancellationToken);

        public async Task MarkSessionReadyAsync(string agvId)
        {
            Context.SessionRecoveries.Add(new SessionRecoveryRow
            {
                AgvId = agvId,
                SessionGeneration = 1,
                ProtocolCommit = "c",
                ManifestSha256 = "m",
                ProfileId = "WIRE_TO_GATE_MVP",
                ProtocolVersion = 3,
                Readiness = SessionReadiness.Ready,
                ReasonCode = "READY",
                UpdatedAt = Now
            });
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
