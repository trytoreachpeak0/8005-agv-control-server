using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 服务端是仓位配置的唯一权威，两层模板各自版本化、各自不可改写。
/// </summary>
public sealed class SlotConfigurationAuthorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] BasketTypes = ["PDFN5", "TOLL"];
    private static readonly string[] WiderBasketTypes = ["PDFN5", "TOLL", "EURO"];

    [Fact]
    public async Task BothLayersVersionSeparatelyAndAPublishedVersionCannotBeRewritten()
    {
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();

        SlotTemplateRow template = await fixture.Store.PublishTemplateVersionAsync(
            "standard", Specification(300), Now, TestContext.Current.CancellationToken);
        Assert.Equal(1, template.Version);
        SlotTemplateRow second = await fixture.Store.PublishTemplateVersionAsync(
            "standard", Specification(350), Now.AddDays(1), TestContext.Current.CancellationToken);
        Assert.Equal(2, second.Version);

        // 改一版已发布的模板：被拒。改内容只能发新版本。
        template.HeightMm = 999;
        PublishedVersionImmutabilityException failure =
            await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() =>
                fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Publish a new version instead", failure.Message, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(300, (await fixture.Context.Set<SlotTemplateRow>().AsNoTracking()
            .SingleAsync(row => row.Version == 1, TestContext.Current.CancellationToken)).HeightMm);

        SlotModelVersionRow model = await fixture.Store.PublishModelVersionAsync(
            "model", ModelSlots("standard", 1), Now, TestContext.Current.CancellationToken);
        Assert.Equal(1, model.Version);
        model.SlotCount = 99;
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() =>
            fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task OneTemplateIsReferencedByManySlotsAndPublishingANewTemplateDoesNotTouchTheLiveConfiguration()
    {
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotTemplateRow template = await fixture.Store.PublishTemplateVersionAsync(
            "standard", Specification(300), Now, TestContext.Current.CancellationToken);
        SlotModelVersionRow model = await fixture.Store.PublishModelVersionAsync(
            "model", ModelSlots("standard", 1), Now, TestContext.Current.CancellationToken);

        SlotModelSlotRow[] slots = await fixture.Context.Set<SlotModelSlotRow>().AsNoTracking()
            .Where(row => row.SlotModelVersionId == model.SlotModelVersionId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, slots.Length);
        Assert.All(slots, slot => Assert.Equal(template.SlotTemplateId, slot.SlotTemplateId));

        // 发一版新模板不会动已发布的模型：模型仍然引用它冻结时的那一版。
        await fixture.Store.PublishTemplateVersionAsync(
            "standard", Specification(350), Now.AddDays(1), TestContext.Current.CancellationToken);
        SlotModelSlotRow[] afterTemplateChange = await fixture.Context.Set<SlotModelSlotRow>().AsNoTracking()
            .Where(row => row.SlotModelVersionId == model.SlotModelVersionId)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.All(afterTemplateChange, slot => Assert.Equal(template.SlotTemplateId, slot.SlotTemplateId));
    }

    [Theory]
    [InlineData("height", SlotConfigurationChangeRoute.TemplateSpecification)]
    [InlineData("baskets", SlotConfigurationChangeRoute.TemplateSpecification)]
    [InlineData("nothing", SlotConfigurationChangeRoute.NoChange)]
    public void ATemplateSpecificationEditIsRoutedToTemplateGovernance(string edit, SlotConfigurationChangeRoute expected)
    {
        SlotTemplateSpecification current = Specification(300);
        SlotTemplateSpecification proposed = edit switch
        {
            "height" => Specification(350),
            "baskets" => current with { CompatibleBasketTypes = WiderBasketTypes },
            _ => current
        };
        Assert.Equal(
            expected, SlotConfigurationChangeClassification.ClassifyTemplateChange(current, proposed));
    }

    [Theory]
    [InlineData("slotPosition")]
    [InlineData("physicalNumber")]
    [InlineData("slotSet")]
    public void AVehicleLayerEditIsRoutedToWholeVehicleMaintenance(string edit)
    {
        IReadOnlyList<SlotModelSlotSpecification> current = ModelSlots("standard", 1);
        IReadOnlyList<SlotModelSlotSpecification> proposed = edit switch
        {
            "slotPosition" => [current[0] with { SlotPosition = "REAR" }, .. current.Skip(1)],
            "physicalNumber" => [current[0] with { PhysicalSlotNumber = 9 }, .. current.Skip(1)],
            _ => [.. current.Take(7)]
        };
        Assert.Equal(
            SlotConfigurationChangeRoute.WholeVehicleMaintenance,
            SlotConfigurationChangeClassification.ClassifyModelChange(current, proposed));
    }

    [Theory]
    [InlineData("unlockOutput")]
    [InlineData("signalPolarity")]
    [InlineData("pulseReset")]
    [InlineData("lightCurtain")]
    public void AnIoEditIsRoutedToWholeVehicleMaintenanceBecauseTheServerMustKnowAboutIt(string edit)
    {
        IReadOnlyList<SlotIoBindingSpecification> current = ApprovedSlotHardwareFacts.IoBindings;
        IReadOnlyList<SlotIoBindingSpecification> proposed = edit switch
        {
            "unlockOutput" => [current[0] with { UnlockOutputPoint = "DO9" }, .. current.Skip(1)],
            "signalPolarity" => [current[0] with { SignalPolarity = "ACTIVE_LOW" }, .. current.Skip(1)],
            "pulseReset" => [current[0] with { PulseResetMilliseconds = 400 }, .. current.Skip(1)],
            _ => [current[0] with { LightCurtainInputPoint = "DI17" }, .. current.Skip(1)]
        };
        Assert.Equal(
            SlotConfigurationChangeRoute.WholeVehicleMaintenance,
            SlotConfigurationChangeClassification.ClassifyIoBindingChange(current, proposed));
        // 同时触及两层时归保守的那一侧：整车维护会重新核验，模板资料变更不会。
        Assert.Equal(
            SlotConfigurationChangeRoute.WholeVehicleMaintenance,
            SlotConfigurationChangeClassification.Combine(
                SlotConfigurationChangeRoute.TemplateSpecification,
                SlotConfigurationChangeRoute.WholeVehicleMaintenance));
    }

    [Fact]
    public async Task AVehicleDeclarationIsOnlyVerifiedAndNeverBecomesTheAuthority()
    {
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotModelVersionRow model = await fixture.Store.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01",
            model.SlotModelVersionId,
            ApprovedSlotHardwareFacts.IoBindings,
            Now,
            TestContext.Current.CancellationToken);

        VehicleDeclarationVerdict agreeing = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", ApprovedSlotHardwareFacts.IoBindings, TestContext.Current.CancellationToken);
        Assert.True(agreeing.Matches);
        Assert.Empty(agreeing.MismatchedFields);

        SlotIoBindingSpecification[] claimed =
        [
            ApprovedSlotHardwareFacts.IoBindings[0] with { UnlockOutputPoint = "DO9" },
            .. ApprovedSlotHardwareFacts.IoBindings.Skip(1)
        ];
        VehicleDeclarationVerdict disagreeing = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", claimed, TestContext.Current.CancellationToken);
        Assert.False(disagreeing.Matches);
        Assert.Equal(["slot1.unlockOutputPoint"], disagreeing.MismatchedFields);

        // 核验之后权威侧一个字节没变 —— 车说什么都不会成为权威。
        SlotIoBindingRow authoritative = await fixture.Context.Set<SlotIoBindingRow>().AsNoTracking()
            .SingleAsync(
                row => row.AgvId == "AGV-01" && row.PhysicalSlotNumber == 1,
                TestContext.Current.CancellationToken);
        Assert.Equal("DO1", authoritative.UnlockOutputPoint);
    }

    [Fact]
    public async Task ADeclarationIsVerifiedAgainstTheMostRecentlyBoundModelNotTheHighestVersionNumberOfAnyModel()
    {
        // IO 绑定的版本号按「车 + 车型版本」各自从 1 数起：同一台车先在车型 A 下绑两次（A 的 v2），
        // 再换到车型 B 绑一次（B 的 v1）。整台车里最大的版本号是 A 的 2，但车现在接的是 B 的线。
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotModelVersionRow modelA = await fixture.Store.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        SlotModelVersionRow modelB = await fixture.Store.PublishModelVersionAsync(
            "8005-eight-slot-rewired",
            ModelSlots(ApprovedSlotHardwareFacts.TemplateKey, 1),
            Now,
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelA.SlotModelVersionId, ApprovedSlotHardwareFacts.IoBindings, Now,
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelA.SlotModelVersionId, ApprovedSlotHardwareFacts.IoBindings, Now.AddHours(1),
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelB.SlotModelVersionId, RewiredIoBindings(), Now.AddHours(2),
            TestContext.Current.CancellationToken);

        VehicleDeclarationVerdict wiredAsB = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", RewiredIoBindings(), TestContext.Current.CancellationToken);
        Assert.True(wiredAsB.Matches);
        Assert.Empty(wiredAsB.MismatchedFields);

        VehicleDeclarationVerdict wiredAsA = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", ApprovedSlotHardwareFacts.IoBindings, TestContext.Current.CancellationToken);
        Assert.False(wiredAsA.Matches);
        Assert.Equal(EveryUnlockOutputPoint(), wiredAsA.MismatchedFields);
    }

    [Fact]
    public async Task TwoModelsThatShareAVersionNumberAreNeverMixedIntoOneBindingSet()
    {
        // 车型 A 与车型 B 各绑一次，两套都是 v1。按版本号挑会把两个车型的行混成一套，同一个仓号出现两次。
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotModelVersionRow modelA = await fixture.Store.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        SlotModelVersionRow modelB = await fixture.Store.PublishModelVersionAsync(
            "8005-eight-slot-rewired",
            ModelSlots(ApprovedSlotHardwareFacts.TemplateKey, 1),
            Now,
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelA.SlotModelVersionId, ApprovedSlotHardwareFacts.IoBindings, Now,
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelB.SlotModelVersionId, RewiredIoBindings(), Now.AddHours(1),
            TestContext.Current.CancellationToken);

        VehicleDeclarationVerdict wiredAsB = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", RewiredIoBindings(), TestContext.Current.CancellationToken);
        Assert.True(wiredAsB.Matches);

        VehicleDeclarationVerdict wiredAsA = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", ApprovedSlotHardwareFacts.IoBindings, TestContext.Current.CancellationToken);
        Assert.Equal(EveryUnlockOutputPoint(), wiredAsA.MismatchedFields);
    }

    [Fact]
    public async Task TheActivatedConfigurationOutranksABindingPublishedLaterForAnotherModel()
    {
        // 车上生效的是车型 A；之后给车型 B 发了绑定（两次），但还没激活。车此刻跑的仍是 A，核验就按 A 比，
        // 与派车读仓位分组时认的车型是同一个。
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotModelVersionRow modelA = await fixture.Store.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        SlotModelVersionRow modelB = await fixture.Store.PublishModelVersionAsync(
            "8005-eight-slot-rewired",
            ModelSlots(ApprovedSlotHardwareFacts.TemplateKey, 1),
            Now,
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelA.SlotModelVersionId, ApprovedSlotHardwareFacts.IoBindings, Now,
            TestContext.Current.CancellationToken);
        SlotConfigurationActivationCoordinator coordinator = new(
            fixture.Context,
            new GovernedConfigurationPublisher(fixture.Governance, fixture.Governance),
            fixture.Governance);
        SlotConfigurationActivationRow activation = await coordinator.IssueActivationAsync(
            "AGV-01", modelA.SlotModelVersionId, Now, TestContext.Current.CancellationToken);
        await coordinator.RecordResultAsync(
            new ActivationResultReport(activation.ActivationId, Succeeded: true, null, Now.AddMinutes(5)),
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelB.SlotModelVersionId, RewiredIoBindings(), Now.AddHours(1),
            TestContext.Current.CancellationToken);
        await fixture.Store.PublishIoBindingsAsync(
            "AGV-01", modelB.SlotModelVersionId, RewiredIoBindings(), Now.AddHours(2),
            TestContext.Current.CancellationToken);

        VehicleDeclarationVerdict wiredAsA = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", ApprovedSlotHardwareFacts.IoBindings, TestContext.Current.CancellationToken);
        Assert.True(wiredAsA.Matches);

        VehicleDeclarationVerdict wiredAsB = await fixture.Store.VerifyVehicleDeclarationAsync(
            "AGV-01", RewiredIoBindings(), TestContext.Current.CancellationToken);
        Assert.Equal(EveryUnlockOutputPoint(), wiredAsB.MismatchedFields);
    }

    [Fact]
    public async Task TheApprovedEightSlotHardwareFactsAreStoredAsAnImmutableApprovedVersion()
    {
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotModelVersionRow model = await fixture.Store.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);
        IReadOnlyList<SlotIoBindingRow> bindings = await fixture.Store.PublishIoBindingsAsync(
            "AGV-01",
            model.SlotModelVersionId,
            ApprovedSlotHardwareFacts.IoBindings,
            Now,
            TestContext.Current.CancellationToken);

        Assert.Equal(8, bindings.Count);
        Assert.Equal(
            ["DO1", "DO2", "DO3", "DO4", "DO5", "DO6", "DO7", "DO8"],
            bindings.OrderBy(row => row.PhysicalSlotNumber).Select(row => row.UnlockOutputPoint));
        Assert.Equal(
            ["DI1", "DI2", "DI3", "DI4", "DI5", "DI6", "DI7", "DI8"],
            bindings.OrderBy(row => row.PhysicalSlotNumber).Select(row => row.LockFeedbackInputPoint));
        Assert.Equal(
            ["DI9", "DI10", "DI11", "DI12", "DI13", "DI14", "DI15", "DI16"],
            bindings.OrderBy(row => row.PhysicalSlotNumber).Select(row => row.LightCurtainInputPoint));
        Assert.All(bindings, row => Assert.Equal(500, row.PulseResetMilliseconds));

        // 已批准的事实改不动。
        bindings[0].UnlockOutputPoint = "DO9";
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() =>
            fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        // 幂等：再调一次不会造出第二版。
        SlotModelVersionRow again = await fixture.Store.EnsureApprovedHardwareFactsAsync(
            Now.AddDays(1), TestContext.Current.CancellationToken);
        Assert.Equal(model.SlotModelVersionId, again.SlotModelVersionId);
    }

    [Fact]
    public async Task TheApprovedEightSlotModelPutsSlotsOneToFourInFrontAndFiveToEightInRear()
    {
        // REQ-0349：八仓车型取 FRONT（1～4 号，前侧仓门）与 REAR（5～8 号，后侧仓门）。这是已批准硬件事实
        // （REQ-0267）的数据录入，不是派车规则。批次 4 之前这里写的是 LEFT／RIGHT，指的是同一侧仓门。
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotModelVersionRow model = await fixture.Store.EnsureApprovedHardwareFactsAsync(
            Now, TestContext.Current.CancellationToken);

        string[] positions = await fixture.Context.Set<SlotModelSlotRow>().AsNoTracking()
            .Where(row => row.SlotModelVersionId == model.SlotModelVersionId)
            .OrderBy(row => row.PhysicalSlotNumber)
            .Select(row => row.SlotPosition)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["FRONT", "FRONT", "FRONT", "FRONT", "REAR", "REAR", "REAR", "REAR"],
            positions);
    }

    [Fact]
    public async Task EveryPublishedVersionCarriesItsOwnFrozenSnapshotAndAudit()
    {
        await using AuthorityFixture fixture = await AuthorityFixture.CreateAsync();
        SlotTemplateRow template = await fixture.Store.PublishTemplateVersionAsync(
            "standard", Specification(300), Now, TestContext.Current.CancellationToken);

        Assert.NotNull(template.SnapshotId);
        GovernedVersionView view = Assert.IsType<GovernedVersionView>(
            await fixture.Governance.ReadVersionAsync(
                GovernedObjectKind.SlotTemplate, "standard", 1, TestContext.Current.CancellationToken));
        Assert.Equal(template.SnapshotId, view.Snapshot.SnapshotId);
        GovernanceAuditView audit = Assert.Single(view.BusinessAudit);
        Assert.Equal("SLOT_TEMPLATE_VERSION_PUBLISHED", audit.Action);
        Assert.Equal(
            AuditActorAttribution.NotAttributableToNaturalPerson, audit.ActorAttribution);
    }

    private static SlotTemplateSpecification Specification(int heightMm) =>
        new(600, 400, heightMm, BasketTypes);

    /// <summary>另一套接线：每个仓的解锁输出挪到 DO9～DO16，其余与已批准事实相同。</summary>
    private static SlotIoBindingSpecification[] RewiredIoBindings() =>
    [
        .. ApprovedSlotHardwareFacts.IoBindings.Select(binding => binding with
        {
            UnlockOutputPoint = FormattableString.Invariant($"DO{binding.PhysicalSlotNumber + 8}")
        })
    ];

    private static string[] EveryUnlockOutputPoint() =>
    [
        .. Enumerable.Range(1, 8)
            .Select(slot => FormattableString.Invariant($"slot{slot}.unlockOutputPoint"))
            .Order(StringComparer.Ordinal)
    ];

    private static IReadOnlyList<SlotModelSlotSpecification> ModelSlots(string templateKey, long templateVersion) =>
    [
        .. Enumerable.Range(1, 8).Select(number => new SlotModelSlotSpecification(
            number, number <= 4 ? "FRONT" : "REAR", templateKey, templateVersion))
    ];

    private sealed class AuthorityFixture : IAsyncDisposable
    {
        private AuthorityFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            GovernanceStore governance,
            SlotConfigurationAuthorityStore store)
        {
            Connection = connection;
            Context = context;
            Governance = governance;
            Store = store;
        }

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public GovernanceStore Governance { get; }
        public SlotConfigurationAuthorityStore Store { get; }

        public static async Task<AuthorityFixture> CreateAsync()
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
            SlotConfigurationAuthorityStore store = new(
                context, new GovernedConfigurationPublisher(governance, governance));
            return new AuthorityFixture(connection, context, governance, store);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
