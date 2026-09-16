using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// 导入服务加上它读的那几份库内事实：已发布整车模型的分组、调度策略里的分区、在途需求的 AREA。
/// </summary>
/// <remarks>
/// 默认布置成一个现场刚起过一次服务端的样子——八仓事实已入库（分组 FRONT／REAR），调度策略里有
/// <see cref="Zone"/> 一个分区。每条测试只改它要证的那一处。
/// </remarks>
internal sealed class AreaAssignmentImportHarness : IAsyncDisposable
{
    internal const string Zone = "MAP-25-WIRE_TO_GATE";

    internal static readonly DateTimeOffset Seeded = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);

    internal static readonly DateTimeOffset Imported = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private AreaAssignmentImportHarness(AreaAssignmentPersistenceFixture fixture)
    {
        Fixture = fixture;
        Facts = new AreaAssignmentImportFacts(fixture.Context);
        Service = new AreaAssignmentImportService(fixture.AreaAssignments, fixture.Freezes, Facts);
    }

    public AreaAssignmentPersistenceFixture Fixture { get; }

    public IAreaAssignmentImportFacts Facts { get; }

    public AreaAssignmentImportService Service { get; }

    public ControlServerDbContext Context => Fixture.Context;

    public static async Task<AreaAssignmentImportHarness> CreateAsync(
        bool seedApprovedModel = true,
        params string[] zones)
    {
        AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        AreaAssignmentImportHarness harness = new(fixture);
        if (seedApprovedModel)
        {
            await fixture.SlotAuthority.EnsureApprovedHardwareFactsAsync(
                Seeded, TestContext.Current.CancellationToken);
        }
        await harness.RegisterZonesAsync(zones.Length == 0 ? [Zone] : zones);
        return harness;
    }

    /// <summary>调度策略里有车服务的分区，即服务端以目标配置启动过一次之后 <c>DispatchZoneVehicles</c> 的样子。</summary>
    public async Task RegisterZonesAsync(IReadOnlyCollection<string> zones)
    {
        foreach (string zone in zones)
        {
            Context.DispatchZoneVehicles.Add(new DispatchZoneVehicleRow
            {
                Zone = zone,
                AgvId = "AGV-TEST",
                ConfigurationVersion = "test",
                UpdatedAt = Seeded
            });
        }
        await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 发布一版按给定取值分组的整车模型，用来证分组集合确实从库里读，而不是代码里写死的 FRONT／REAR。
    /// </summary>
    /// <remarks>已发布的模型改不动（那正是治理要的），所以换分组只能靠发布另一个车型。</remarks>
    public async Task PublishModelGroupedAsync(string modelKey, params string[] groups)
    {
        await Fixture.SlotAuthority.PublishTemplateVersionAsync(
            modelKey,
            new SlotTemplateSpecification(600, 400, 300, ["STANDARD"]),
            Seeded,
            TestContext.Current.CancellationToken);
        await Fixture.SlotAuthority.PublishModelVersionAsync(
            modelKey,
            [.. groups.Select((group, index) => new SlotModelSlotSpecification(index + 1, group, modelKey, 1))],
            Seeded,
            TestContext.Current.CancellationToken);
    }

    /// <summary>一条已受理、还没跑完的需求，冻结在给定的指派版本上。</summary>
    public async Task AddInFlightDemandAsync(string demandId, string area, long frozenVersion)
    {
        Context.Set<AcceptedDemandRow>().Add(new AcceptedDemandRow
        {
            DemandId = demandId,
            SeriesId = demandId,
            TransportDemandKey = $"key:{demandId}",
            WorkType = "WIRE_TO_GATE",
            Sublot = demandId,
            Generation = 1,
            DemandRevision = 1,
            HistoryEpoch = "epoch",
            CatalogRevision = 1,
            CreatedAt = Seeded,
            ValueObservedAt = Seeded,
            ValuePollTraceId = "trace",
            ValueProjectionCommitId = "commit",
            LiveMesFieldsJson = JsonSerializer.Serialize(
                new LiveMesFieldSet(area, "EQP-1", "STEP-1", null, "PKG-1")),
            AcceptedAt = Seeded,
            Status = DemandExecutionStatus.Accepted
        });
        await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Fixture.Freezes.FreezeAsync(
            demandId, frozenVersion, Seeded.AddMinutes(5),
            TestContext.Current.CancellationToken);
    }

    /// <summary>让一条需求的旅程收尾，这样它不再算在途。</summary>
    public async Task CompleteJourneyAsync(string demandId)
    {
        Context.Set<JourneyRuntimeRow>().Add(new JourneyRuntimeRow
        {
            DemandId = demandId,
            Stage = JourneyRuntimeStage.Completed,
            AgvId = "AGV-TEST",
            VehicleKey = "VEHICLE-TEST",
            AgvLifecycleGeneration = 1,
            MapId = 25,
            MapIdentity = "MAP-25",
            DispatchZone = Zone,
            RouteEvidenceId = "ROUTE-01",
            PickupStationId = "PICKUP",
            PickupStationRiotId = 11,
            GateStationId = "GATE",
            GateStationRiotId = 22,
            ExpectedBasketCount = 1,
            TargetSlotsJson = "[1]",
            OperationSessionId = $"session-{demandId}",
            PickupMovementLegId = $"pickup-leg-{demandId}",
            PickupUpperId = $"UPPER-PICKUP-{demandId}",
            GateMovementLegId = $"gate-leg-{demandId}",
            GateUpperId = $"UPPER-GATE-{demandId}",
            DispatchGeneration = 1,
            VehicleBusinessRevision = 1,
            WorklistRevision = 1,
            PlanRevision = 1,
            VehicleBusinessMessageId = $"vb-{demandId}",
            WorklistMessageId = $"wl-{demandId}",
            PlanMessageId = $"plan-{demandId}",
            SublotRequestMessageId = $"sublot-{demandId}",
            LoadCommandMessageId = $"load-{demandId}",
            LoadSlotOperationAttemptId = $"load-attempt-{demandId}",
            PreDepartureSafetyCheckMessageId = $"safety-msg-{demandId}",
            PreDepartureSafetyCheckId = $"safety-{demandId}",
            GateVehicleBusinessMessageId = $"gate-vb-{demandId}",
            GateWorklistMessageId = $"gate-wl-{demandId}",
            GatePlanMessageId = $"gate-plan-{demandId}",
            UnloadCommandMessageId = $"unload-{demandId}",
            UnloadSlotOperationAttemptId = $"unload-attempt-{demandId}",
            CreatedAt = Seeded,
            UpdatedAt = Seeded
        });
        await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public Task<AreaAssignmentImportResult> ImportAsync(string csv, bool dryRun = false) =>
        Service.ImportAsync(
            csv, dryRun, Imported, TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => Fixture.DisposeAsync();
}
