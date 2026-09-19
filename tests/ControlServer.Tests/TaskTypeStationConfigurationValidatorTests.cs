using ControlServer.Application;

namespace ControlServer.Tests;

/// <summary>
/// 启动期 fail-closed 校验（control-server#159）：每个拒绝启动项违反即拒、原因码精确、多项违规一次全列；
/// 目录相关的一半只让该任务类型不投运。
/// </summary>
public sealed class TaskTypeStationConfigurationValidatorTests
{
    private const int Map = 25;

    private static readonly TransitionalGateStation Gate = new(210, "关卡");

    private static readonly TaskTypeStationRule[] SixRules =
    [
        new(TransportTaskTypes.DieToWireStaging, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.DieToOven, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.WireToGate, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.WireToOptical, TaskTypeFixedEnd.Destination),
        new(TransportTaskTypes.StagingToWire, TaskTypeFixedEnd.Origin),
        new(TransportTaskTypes.WireToNitrogen, TaskTypeFixedEnd.Destination),
    ];

    private static readonly TaskTypeStationBinding GateBinding =
        new(TransportTaskTypes.WireToGate, 210, "关卡", "MAP-25-WIRE_TO_GATE-20260827");

    private static readonly TaskTypeStationBinding StagingBinding =
        new(TransportTaskTypes.StagingToWire, 305, "派工待送取货", "SITE-CHECK-STAGING");

    private static TaskTypeStationConfiguration Configuration(
        IReadOnlyList<TaskTypeStationRule>? rules = null,
        IReadOnlyList<string>? required = null,
        IReadOnlyList<TaskTypeStationBinding>? bindings = null,
        int mapId = Map) =>
        new(
            rules ?? SixRules,
            new TaskTypeStationMapConfiguration(
                mapId,
                required ?? [TransportTaskTypes.WireToGate],
                bindings ?? [GateBinding]));

    private static IReadOnlyList<TaskTypeStationViolation> Validate(TaskTypeStationConfiguration configuration) =>
        TaskTypeStationConfigurationValidator.ValidateStatic(configuration, Map, Gate);

    [Fact]
    public void TheFactoryShapedConfigurationPasses()
    {
        Assert.Empty(Validate(Configuration()));
    }

    [Fact]
    public void ARuleForATaskTypeThatIsNotOneOfTheSixMesLiteralsRefusesStart()
    {
        IReadOnlyList<TaskTypeStationViolation> violations =
            Validate(Configuration(rules: [.. SixRules, new("WIRE_TO_MOON", TaskTypeFixedEnd.Destination)]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.RuleUnknownTaskType, violation.ReasonCode);
        Assert.Equal("WIRE_TO_MOON", violation.TaskType);
    }

    [Fact]
    public void TwoRulesForTheSameTaskTypeRefuseStart()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            rules: [.. SixRules, new(TransportTaskTypes.WireToGate, TaskTypeFixedEnd.Destination)]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.RuleDuplicate, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.WireToGate, violation.TaskType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("destination")]
    [InlineData("BOTH")]
    public void AFixedEndThatIsNeitherOriginNorDestinationRefusesStart(string fixedEnd)
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            rules: [.. SixRules.Where(rule => rule.TaskType != TransportTaskTypes.WireToGate),
                new(TransportTaskTypes.WireToGate, fixedEnd)]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.RuleFixedEndInvalid, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.WireToGate, violation.TaskType);
    }

    [Fact]
    public void ATaskTypeInTheRequirementSetWithoutARuleRefusesStart()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            rules: [.. SixRules.Where(rule => rule.TaskType != TransportTaskTypes.StagingToWire)],
            required: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings: [GateBinding, StagingBinding]));

        // The requirement set and the binding each name the task type; one violation per task type, not per mention.
        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.RuleMissing, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.StagingToWire, violation.TaskType);
    }

    [Fact]
    public void ABindingForATaskTypeWithoutARuleRefusesStartEvenWhenTheTaskTypeIsNotRequired()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            rules: [.. SixRules.Where(rule => rule.TaskType != TransportTaskTypes.StagingToWire)],
            bindings: [GateBinding, StagingBinding]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.RuleMissing, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.StagingToWire, violation.TaskType);
    }

    [Fact]
    public void TwoBindingsForTheSameTaskTypeOnOneMapRefuseStart()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            bindings: [GateBinding, new(TransportTaskTypes.WireToGate, 211, "关卡2", "SITE-CHECK-211")]));

        TaskTypeStationViolation duplicate = Assert.Single(
            violations, violation => violation.ReasonCode == TaskTypeStationReasonCodes.BindingDuplicate);
        Assert.Equal(TransportTaskTypes.WireToGate, duplicate.TaskType);
    }

    [Fact]
    public void OneStationBoundToTwoTaskTypesRefusesStartAndNamesTheStationAndBothTaskTypes()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            bindings: [GateBinding, new(TransportTaskTypes.StagingToWire, 210, "关卡", "SITE-CHECK-STAGING")]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.StationReused, violation.ReasonCode);
        Assert.Equal(210, violation.StationRiotId);
        Assert.Contains("关卡", violation.Detail, StringComparison.Ordinal);
        Assert.Contains(TransportTaskTypes.WireToGate, violation.Detail, StringComparison.Ordinal);
        Assert.Contains(TransportTaskTypes.StagingToWire, violation.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequiredTaskTypeWithoutABindingRefusesStart()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            required: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingRequiredMissing, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.StagingToWire, violation.TaskType);
    }

    [Fact]
    public void ATaskTypeThatIsNeitherRequiredNorBoundIsNotEnabledAndDoesNotRefuseStart()
    {
        // STAGING_TO_WIRE has a rule but is neither in the requirement set nor bound: that is "not enabled",
        // which control-server#160 turns away at admission. It is not a misconfiguration.
        Assert.Empty(Validate(Configuration(required: [TransportTaskTypes.WireToGate], bindings: [GateBinding])));
    }

    [Theory]
    [InlineData(0, "关卡")]
    [InlineData(-1, "关卡")]
    [InlineData(210, "")]
    [InlineData(210, "   ")]
    public void AStationWhoseIdIsNotPositiveOrWhoseNameIsBlankRefusesStart(int stationRiotId, string stationName)
    {
        IReadOnlyList<TaskTypeStationViolation> violations = TaskTypeStationConfigurationValidator.ValidateStatic(
            Configuration(bindings: [GateBinding with { StationRiotId = stationRiotId, StationName = stationName }]),
            Map,
            gateScalar: null);

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingIdentityInvalid, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.WireToGate, violation.TaskType);
    }

    [Fact]
    public void AMapIdThatIsNotPositiveRefusesStart()
    {
        IReadOnlyList<TaskTypeStationViolation> violations =
            TaskTypeStationConfigurationValidator.ValidateStatic(Configuration(mapId: 0), 0, Gate);

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingIdentityInvalid, violation.ReasonCode);
        Assert.Null(violation.TaskType);
    }

    [Theory]
    [InlineData("N01-1")]
    [InlineData("N01-1_N01-2")]
    [InlineData("T01-3_T02-1_T03-9")]
    public void AStationNamedAfterAnAreaIsAMachineStationAndRefusesStart(string areaNamedStation)
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            required: [TransportTaskTypes.WireToGate, TransportTaskTypes.StagingToWire],
            bindings: [GateBinding, StagingBinding with { StationName = areaNamedStation }]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingAreaNamedStation, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.StagingToWire, violation.TaskType);
    }

    [Theory]
    [InlineData("N01-1", true)]
    [InlineData("N01-1_N01-2", true)]
    [InlineData("T01-3_T02-1_T03-9", true)]
    [InlineData("N01-1_N01-2_N01-3_N01-4", false)]
    [InlineData("N01-1_N01-1", false)]
    [InlineData("n01-1", false)]
    [InlineData("关卡", false)]
    [InlineData("N01", false)]
    [InlineData("N01-1_关卡", false)]
    public void TheValidatorsAreaRuleIsTheSameAsTheResolversMachineStationRule(string stationName, bool areaNamed)
    {
        // The validator lives in Application and cannot reach MapStationResolver in Host; this keeps the two copies
        // of the rule from drifting apart.
        bool resolverSays = new ControlServer.Host.Runtime.MapStationResolver().ParseAreaNamedMachineStations(
            new RiotMapStationCatalogSnapshot(Map, DateTimeOffset.UnixEpoch, "sha", [new RiotMapStation(1, stationName)]))
            .Count == 1;

        Assert.Equal(areaNamed, resolverSays);
        Assert.Equal(areaNamed, ControlServer.Domain.AreaNamedStationName.IsAreaNamed(stationName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABindingWithoutASiteVerificationReferenceRefusesStart(string reference)
    {
        IReadOnlyList<TaskTypeStationViolation> violations =
            Validate(Configuration(bindings: [GateBinding with { SiteVerificationRef = reference }]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingSiteVerificationMissing, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.WireToGate, violation.TaskType);
    }

    [Theory]
    [InlineData(211, "关卡")]
    [InlineData(210, "关卡2")]
    public void AWireToGateBindingThatDiffersFromTheGateScalarRefusesStart(int stationRiotId, string stationName)
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            bindings: [GateBinding with { StationRiotId = stationRiotId, StationName = stationName }]));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingGateScalarMismatch, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.WireToGate, violation.TaskType);
    }

    [Fact]
    public void APresetThatDoesNotBindWireToGateWhileTheGateScalarDoesRefusesStart()
    {
        // Two truths may not diverge: the scalar says the gate is 210, the preset says WIRE_TO_GATE has no station.
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(required: [], bindings: []));

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingGateScalarMismatch, violation.ReasonCode);
    }

    [Fact]
    public void APresetForAnotherMapThanTheJourneyRuntimeMapRefusesStart()
    {
        IReadOnlyList<TaskTypeStationViolation> violations =
            TaskTypeStationConfigurationValidator.ValidateStatic(Configuration(mapId: 26), Map, gateScalar: null);

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingMapMismatch, violation.ReasonCode);
    }

    [Fact]
    public void EveryViolationIsListedInOnePassNotJustTheFirst()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = Validate(Configuration(
            rules: [.. SixRules, new("WIRE_TO_MOON", TaskTypeFixedEnd.Destination)],
            required: [TransportTaskTypes.WireToGate, TransportTaskTypes.WireToOptical],
            bindings:
            [
                GateBinding with { SiteVerificationRef = "" },
                new(TransportTaskTypes.StagingToWire, 210, "关卡", "SITE-CHECK-STAGING"),
                new(TransportTaskTypes.DieToOven, 400, "N01-1", "SITE-CHECK-OVEN"),
            ]));

        Assert.Equal(
            [
                TaskTypeStationReasonCodes.BindingAreaNamedStation,
                TaskTypeStationReasonCodes.BindingRequiredMissing,
                TaskTypeStationReasonCodes.BindingSiteVerificationMissing,
                TaskTypeStationReasonCodes.RuleUnknownTaskType,
                TaskTypeStationReasonCodes.StationReused,
            ],
            violations.Select(violation => violation.ReasonCode).Order(StringComparer.Ordinal));
    }

    // ---- The catalog half: never refuses start, only takes the one task type out of service ----

    private static readonly TaskTypeStationBinding[] TwoBindings = [GateBinding, StagingBinding];

    private static RiotMapStationCatalogSnapshot Catalog(int mapId, params RiotMapStation[] stations) =>
        new(mapId, new DateTimeOffset(2026, 9, 19, 8, 0, 0, TimeSpan.Zero), "catalog-sha", stations);

    [Fact]
    public void BothStationsPresentByIdAndNameInAFreshCatalogOfTheSameMapGiveNoViolation()
    {
        Assert.Empty(TaskTypeStationConfigurationValidator.EvaluateCatalog(
            Map, TwoBindings, Catalog(Map, new RiotMapStation(210, "关卡"), new RiotMapStation(305, "派工待送取货"), new RiotMapStation(9, "N01-1")), catalogFresh: true));
    }

    [Fact]
    public void AStationMissingFromTheCatalogBlocksOnlyTheTaskTypeBoundToIt()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = TaskTypeStationConfigurationValidator.EvaluateCatalog(
            Map, TwoBindings, Catalog(Map, new RiotMapStation(210, "关卡")), catalogFresh: true);

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingStationNotInCatalog, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.StagingToWire, violation.TaskType);
        Assert.Equal(305, violation.StationRiotId);
    }

    [Fact]
    public void AStationRenamedUnderTheSameIdBlocksOnlyTheTaskTypeBoundToIt()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = TaskTypeStationConfigurationValidator.EvaluateCatalog(
            Map, TwoBindings, Catalog(Map, new RiotMapStation(210, "关卡-旧"), new RiotMapStation(305, "派工待送取货")), catalogFresh: true);

        TaskTypeStationViolation violation = Assert.Single(violations);
        Assert.Equal(TaskTypeStationReasonCodes.BindingStationNotInCatalog, violation.ReasonCode);
        Assert.Equal(TransportTaskTypes.WireToGate, violation.TaskType);
        Assert.Contains("关卡-旧", violation.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ACatalogOfAnotherMapConfirmsNoBindingOfThisMap()
    {
        IReadOnlyList<TaskTypeStationViolation> violations = TaskTypeStationConfigurationValidator.EvaluateCatalog(
            Map, TwoBindings, Catalog(26, new RiotMapStation(210, "关卡"), new RiotMapStation(305, "派工待送取货")), catalogFresh: true);

        Assert.Equal(
            [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
            violations.Select(violation => violation.TaskType).Order(StringComparer.Ordinal));
        Assert.All(violations, violation =>
            Assert.Equal(TaskTypeStationReasonCodes.BindingStationNotInCatalog, violation.ReasonCode));
    }

    [Fact]
    public void ACatalogThatIsNotFreshOrUnknownGivesEachBoundTaskTypeItsOwnNotFreshReason()
    {
        IReadOnlyList<TaskTypeStationViolation> stale = TaskTypeStationConfigurationValidator.EvaluateCatalog(
            Map, TwoBindings, Catalog(Map, new RiotMapStation(210, "关卡"), new RiotMapStation(305, "派工待送取货")), catalogFresh: false);
        IReadOnlyList<TaskTypeStationViolation> unknown = TaskTypeStationConfigurationValidator.EvaluateCatalog(
            Map, TwoBindings, catalog: null, catalogFresh: true);

        foreach (IReadOnlyList<TaskTypeStationViolation> violations in new[] { stale, unknown })
        {
            Assert.Equal(
                [TransportTaskTypes.StagingToWire, TransportTaskTypes.WireToGate],
                violations.Select(violation => violation.TaskType).Order(StringComparer.Ordinal));
            Assert.All(violations, violation =>
                Assert.Equal(TaskTypeStationReasonCodes.BindingCatalogNotFresh, violation.ReasonCode));
        }
    }
}
