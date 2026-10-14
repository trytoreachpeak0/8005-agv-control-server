using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// 仓位就绪门禁接入派车（control-server#686；REQ-0259、REQ-0263，program#170 缺口 G2）：档位是 <c>Enforcing</c> 时，仓位配置在
/// 当前库里没有确认的车不接搬运；档位关时与今天逐字相同。
/// </summary>
/// <remarks>
/// 判定用的是 <see cref="SlotConfigurationReadinessGate.EvaluateAsync"/> 现算的结论，不是读模型里存的那一行：读模型只是最近一次结论，
/// 换新库之后它根本不存在，而「没有记录」必须是「未确认」。
/// </remarks>
public sealed class SlotConfigurationReadinessDispatchTests
{
    private const string AgvA = "AGV-01";
    private const string AgvB = "AGV-02";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithTheGateOffAVehicleNeverVerifiedStillTakesWork()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync(AgvA, model);

        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.EvaluateAsync(SlotConfigurationGateMode.Off, AgvA));
    }

    [Fact]
    public async Task EnforcingAVehicleBoundButNeverVerifiedTakesNoWork()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync(AgvA, model);

        Assert.Equal(
            DispatchReasonCodes.SlotConfigurationNotConfirmed,
            await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));
    }

    [Fact]
    public async Task EnforcingAVehicleVerifiedSlotBySlotTakesWork()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync(AgvA, model);
        await fixture.VerifyAllSlotsAsync(AgvA, model);

        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));
    }

    [Fact]
    public async Task EnforcingAVehicleWhoseModelCannotBeResolvedTakesNoWork()
    {
        // 新库 B 上一辆车什么都还没导：没有生效配置也没有已发布绑定，车型解析不出——不猜车型，算未确认。
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.PublishApprovedModelAsync();

        Assert.Equal(
            DispatchReasonCodes.SlotConfigurationNotConfirmed,
            await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));
        Assert.Null(await fixture.Gate.EvaluateVehicleAsync(AgvA, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnforcingOneSlotThatFailedVerificationKeepsTheVehicleOut()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync(AgvA, model);
        await fixture.Gate.RecordVerificationAsync(
            AgvA,
            model,
            [.. Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount)
                .Select(slot => new SlotVerificationConfirmation(slot, true, true, InPlaceSignalConfirmed: slot != 5))],
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            DispatchReasonCodes.SlotConfigurationNotConfirmed,
            await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));
    }

    [Fact]
    public async Task EnforcingAHardwareChangeAfterVerificationTakesTheVehicleOutUntilItIsVerifiedAgain()
    {
        // 第二次发生的动作不能被第一次的结论盖住：核验过、又换了硬件的车，在重新核验之前不接活。
        await using Fixture fixture = await Fixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync(AgvA, model);
        await fixture.VerifyAllSlotsAsync(AgvA, model);
        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));

        await fixture.Gate.InvalidateForHardwareChangeAsync(AgvA, model, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        Assert.Equal(
            DispatchReasonCodes.SlotConfigurationNotConfirmed,
            await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));

        await fixture.VerifyAllSlotsAsync(AgvA, model, Now.AddMinutes(2));
        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));
    }

    [Fact]
    public async Task EnforcingIsJudgedVehicleByVehicle()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync(AgvA, model);
        await fixture.VerifyAllSlotsAsync(AgvA, model);
        await fixture.BindAllSlotsAsync(AgvB, model);

        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA));
        Assert.Equal(
            DispatchReasonCodes.SlotConfigurationNotConfirmed,
            await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvB));
    }

    [Fact]
    public async Task TheRefusalIsLoggedOncePerVehicleAcrossRoundsWithTheReadinessReason()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        string model = await fixture.PublishApprovedModelAsync();
        await fixture.BindAllSlotsAsync(AgvA, model);
        SlotConfigurationReadinessLog shared = new();
        EventRecordingLogger<SlotConfigurationReadinessCriterion> log = new();

        for (int round = 0; round < 3; round++)
        {
            await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA, shared, log);
        }
        EventRecordingLogger<SlotConfigurationReadinessCriterion>.Entry single = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, single.Level);
        Assert.Contains(SlotReadinessReasonCode.NeverVerified, single.Message, StringComparison.Ordinal);

        await fixture.VerifyAllSlotsAsync(AgvA, model);
        await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA, shared, log);
        Assert.Single(log.Entries);

        await fixture.Gate.InvalidateForHardwareChangeAsync(AgvA, model, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        await fixture.EvaluateAsync(SlotConfigurationGateMode.Enforcing, AgvA, shared, log);
        Assert.Equal(2, log.Entries.Count);
        Assert.Contains(SlotReadinessReasonCode.HardwareChanged, log.Entries[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcingWithoutAReadinessGateCannotBeConstructed()
    {
        // 档位开着却没有判定来源，等于门禁开着却永远放行——构造时就炸，不留这种组合。
        Assert.Throws<ArgumentNullException>(() => new SlotConfigurationReadinessCriterion(
            new SlotConfigurationGateSwitch(SlotConfigurationGateMode.Enforcing), null, new SlotConfigurationReadinessLog()));
    }

    [Fact]
    public void TheHostRegistersTheCriterionAndItsLogAsASingleton()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IDispatchAdmissionCriterion)
            && descriptor.ImplementationType == typeof(SlotConfigurationReadinessCriterion));
        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(SlotConfigurationReadinessLog)).Lifetime);
    }

    [Fact]
    public void TheInTransitChainCarriesTheCriterionSoEnRouteAppendIsRefusedToo()
    {
        SlotConfigurationReadinessCriterion criterion = new(
            new SlotConfigurationGateSwitch(SlotConfigurationGateMode.Off), null, new SlotConfigurationReadinessLog());

        IReadOnlyList<IDispatchAdmissionCriterion> inTransit = DispatchAdmissionCriteria.InTransit(
            [criterion], Microsoft.Extensions.Options.Options.Create(new JourneyRuntimeOptions()));

        Assert.Contains(criterion, inTransit);
        Assert.Equal(19, criterion.Order);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(SqliteConnection connection, ControlServerDbContext context, SlotConfigurationAuthorityStore authority, SlotConfigurationReadinessGate gate)
        {
            Connection = connection;
            Context = context;
            Authority = authority;
            Gate = gate;
        }

        private SqliteConnection Connection { get; }

        private ControlServerDbContext Context { get; }

        private SlotConfigurationAuthorityStore Authority { get; }

        public SlotConfigurationReadinessGate Gate { get; }

        public static async Task<Fixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            GovernanceStore governance = new(
                context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
            SlotConfigurationAuthorityStore authority = new(context, new GovernedConfigurationPublisher(governance, governance));
            return new Fixture(connection, context, authority, new SlotConfigurationReadinessGate(context, governance));
        }

        public async Task<string> PublishApprovedModelAsync() =>
            (await Authority.EnsureApprovedHardwareFactsAsync(Now, TestContext.Current.CancellationToken)).SlotModelVersionId;

        public async Task BindAllSlotsAsync(string agvId, string slotModelVersionId) =>
            await Authority.PublishIoBindingsAsync(
                agvId, slotModelVersionId, [.. ApprovedSlotHardwareFacts.IoBindings], Now, TestContext.Current.CancellationToken);

        public Task VerifyAllSlotsAsync(string agvId, string slotModelVersionId, DateTimeOffset? at = null) =>
            Gate.RecordVerificationAsync(
                agvId,
                slotModelVersionId,
                [.. Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount).Select(slot => new SlotVerificationConfirmation(slot, true, true, true))],
                at ?? Now,
                TestContext.Current.CancellationToken);

        public Task<string> EvaluateAsync(
            SlotConfigurationGateMode mode,
            string agvId,
            SlotConfigurationReadinessLog? log = null,
            ILogger<SlotConfigurationReadinessCriterion>? logger = null) =>
            new SlotConfigurationReadinessCriterion(new SlotConfigurationGateSwitch(mode), Gate, log ?? new SlotConfigurationReadinessLog(), logger)
                .EvaluateAsync(new DispatchCandidateEvaluation(null!, null!, Facts(agvId)), TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }

        private static DispatchVehicleFacts Facts(string agvId) =>
            new(
                "BROKERX-" + agvId,
                agvId,
                new OnboardDispatchFacts(7, [3, 4], true, true, true, true, false),
                new RiotVehicleObservation("BROKERX-" + agvId, true, true, "IDLE", "MAP-26", 12, 80, "NO_CHARGE", 0, Now, 0, null),
                Now);
    }
}
