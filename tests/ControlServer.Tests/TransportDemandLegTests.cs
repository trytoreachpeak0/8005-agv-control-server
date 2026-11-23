using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 同一子批的两程 <c>STAGING_TO_WIRE</c>（control-server#911）：MES 给送焊线加了工序「焊线2」以后，同一个子批先送「焊线」机台、
/// 再送「焊线2」机台，两程业务键相同。需求身份是「业务键 + 程」，程取 MES <c>STEP</c>，只对 <c>STAGING_TO_WIRE</c> 分程。
/// </summary>
/// <remarks>
/// <para>
/// 判据按票面验收的三类：焊线那程送达或取消之后，焊线2 那程照常过第 11、12 道（用户 11-23 定按程压制）；同一程以新
/// <c>DemandId</c> 再发（MES 重复下发、换代）、取消后再现，照旧被挡；不分程的任务类型 <c>STEP</c> 不同也不分开。
/// 还有存储层：受理两程不撞唯一索引，两程各自写得下完成事实——只放开判据、不改完成表，焊线2 送到时会被判成身份冲突。
/// </para>
/// <para>
/// 种的受理行与抑制行的程经 <see cref="TransportDemandIdentity.LegOf"/> 算，与受理路径写行时同源：把 <c>LegOf</c> 变异成恒返回空串
/// （回到本票之前「只按业务键」的行为），种的行与候选一起退化，放行的几例就红——那正是缺陷版本的样子。
/// </para>
/// <para>
/// 「放行」判的是链走过了第 11、12 两道：这里的车没有车载端事实，放行的候选一路走到 <c>ONBOARD_FACTS_NOT_READY</c>
/// （同 <see cref="Batch7TransportDemandKeyAdmissionTests"/>）。
/// </para>
/// </remarks>
public sealed class TransportDemandLegTests : IAsyncDisposable
{
    private const string Sublot = "Q26113563-4";
    private const string WireLeg = "焊线";
    private const string Wire2Leg = "焊线2";
    private const string FirstLeg = "10000000-0000-4000-8000-000000000911";
    private const string SecondLeg = "10000000-0000-4000-8000-000000000912";
    private const string SameLegAgain = "10000000-0000-4000-8000-000000000913";
    private const string PassedTheKeyCriteria = "ONBOARD_FACTS_NOT_READY";

    private static readonly DateTimeOffset Now = new(2026, 11, 23, 6, 0, 0, TimeSpan.Zero);

    private static readonly RiotMapStation Gate = new(210, "关卡");

    private static readonly RiotMapStation Staging = new(305, "派工待送取货");

    private static readonly RiotMapStationCatalogSnapshot Map = new(
        25, Now, new string('c', 64), [new RiotMapStation(12, "N1-1"), Gate, Staging]);

    private static readonly JourneyRuntimeOptions Configured = new()
    {
        MapId = 25,
        AllowedWorkTypes = [.. TransportTaskTypes.All],
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
        MaximumEvidenceAge = TimeSpan.FromMinutes(2),
    };

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private ControlServerDbContext? _context;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
        await _connection.DisposeAsync();
    }

    [Fact]
    public void OnlyStagingToWireIsSplitByStepAndABlankStepIsNoLeg()
    {
        Assert.Equal([TransportTaskTypes.StagingToWire], TransportDemandIdentity.LegSplitWorkTypes.Order(StringComparer.Ordinal));
        Assert.Equal(Wire2Leg, TransportDemandIdentity.LegOf(TransportTaskTypes.StagingToWire, Wire2Leg));
        Assert.Equal(TransportDemandIdentity.NoLeg, TransportDemandIdentity.LegOf(TransportTaskTypes.StagingToWire, " "));
        Assert.Equal(TransportDemandIdentity.NoLeg, TransportDemandIdentity.LegOf(TransportTaskTypes.StagingToWire, null));
        foreach (string other in TransportTaskTypes.All.Where(type => type != TransportTaskTypes.StagingToWire))
        {
            Assert.Equal(TransportDemandIdentity.NoLeg, TransportDemandIdentity.LegOf(other, Wire2Leg));
        }
    }

    /// <summary>焊线那程已送达：焊线2 那程是另一件活，第 12 道不挡（缺陷版本给 <c>TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED</c>）。</summary>
    [Fact]
    public async Task TheSecondLegPassesAfterTheFirstLegWasDelivered()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(FirstLeg, TransportTaskTypes.StagingToWire, WireLeg, DemandExecutionStatus.Succeeded);

        string verdict = await chain.EvaluateAsync(
            Evaluation(SecondLeg, TransportTaskTypes.StagingToWire, Wire2Leg, acceptedThisServer: [FirstLeg]), Token);

        Assert.Equal(PassedTheKeyCriteria, verdict);
    }

    /// <summary>
    /// 焊线那程被操作员取消并抑制：按程抑制，焊线2 那程照常派（缺陷版本给 <c>TRANSPORT_DEMAND_KEY_SUPPRESSED</c>，
    /// 现场 <c>Q26113563-4</c> 就是这样）。
    /// </summary>
    [Fact]
    public async Task TheSecondLegPassesAfterTheFirstLegWasCancelledAndSuppressed()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(FirstLeg, TransportTaskTypes.StagingToWire, WireLeg, DemandExecutionStatus.Cancelled);
        await SuppressAsync(FirstLeg, TransportTaskTypes.StagingToWire, WireLeg, "CANCELLED_BY_OPERATOR");

        string verdict = await chain.EvaluateAsync(
            Evaluation(SecondLeg, TransportTaskTypes.StagingToWire, Wire2Leg, acceptedThisServer: [FirstLeg]), Token);

        Assert.Equal(PassedTheKeyCriteria, verdict);
    }

    /// <summary>同一程送达后 MES 以新 <c>DemandId</c> 再发（现场 <c>Q26113631-3</c>）：<c>STEP</c> 不变，第 12 道照旧挡。</summary>
    [Fact]
    public async Task TheSameLegUnderANewDemandIdAfterDeliveryIsStillRefused()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(SecondLeg, TransportTaskTypes.StagingToWire, Wire2Leg, DemandExecutionStatus.Succeeded);

        string verdict = await chain.EvaluateAsync(
            Evaluation(SameLegAgain, TransportTaskTypes.StagingToWire, Wire2Leg, acceptedThisServer: [SecondLeg]), Token);

        Assert.Equal(TransportDemandKeyAlreadyAcceptedCriterion.Reason, verdict);
    }

    /// <summary>同一程取消后再现（换代）：按程抑制仍命中，REQ-0155 不变。</summary>
    [Fact]
    public async Task TheSameLegAfterACancellationIsStillSuppressed()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(FirstLeg, TransportTaskTypes.StagingToWire, WireLeg, DemandExecutionStatus.Cancelled);
        await SuppressAsync(FirstLeg, TransportTaskTypes.StagingToWire, WireLeg, "CANCELLED_BY_STATION_TIMEOUT");

        string verdict = await chain.EvaluateAsync(
            Evaluation(SameLegAgain, TransportTaskTypes.StagingToWire, WireLeg, acceptedThisServer: [FirstLeg]), Token);

        Assert.Equal(TransportDemandKeySuppressedCriterion.Reason, verdict);
    }

    /// <summary>不分程的任务类型：<c>STEP</c> 变了也是同一件活——分程清单只有 <c>STAGING_TO_WIRE</c>。</summary>
    [Fact]
    public async Task AnotherStepOnATaskTypeThatIsNotSplitIsStillTheSameDemand()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(FirstLeg, TransportTaskTypes.WireToGate, "焊线关卡", DemandExecutionStatus.Succeeded);

        string verdict = await chain.EvaluateAsync(
            Evaluation(SecondLeg, TransportTaskTypes.WireToGate, "焊线关卡2", acceptedThisServer: [FirstLeg]), Token);

        Assert.Equal(TransportDemandKeyAlreadyAcceptedCriterion.Reason, verdict);
    }

    /// <summary>
    /// 防饥饿排除名单与两道判据同一个判法（<see cref="StarvationExclusions"/>）：焊线2 那程不被排除，同一程再发被排除。
    /// </summary>
    [Fact]
    public async Task TheStarvationExclusionsJudgeLegsTheWayTheCriteriaDo()
    {
        await ChainAsync();
        await AcceptAsync(FirstLeg, TransportTaskTypes.StagingToWire, WireLeg, DemandExecutionStatus.Cancelled);
        await SuppressAsync(FirstLeg, TransportTaskTypes.StagingToWire, WireLeg, "CANCELLED_BY_OPERATOR");

        HashSet<string> excluded = await StarvationExclusions.ReadAsync(
            _context!,
            [
                Snapshot(SecondLeg, TransportTaskTypes.StagingToWire, Wire2Leg),
                Snapshot(SameLegAgain, TransportTaskTypes.StagingToWire, WireLeg),
            ],
            Token);

        Assert.Equal([SameLegAgain], excluded.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// 存储层：焊线那程经受理路径受理、送达，焊线2 那程照样受理得下（不撞受理唯一索引），送达时写得下自己的完成事实
    /// （缺陷版本撞完成表主键，报「同一业务键已有另一份完成记录」）。两程的受理行与完成行各带自己的程。
    /// </summary>
    [Fact]
    public async Task BothLegsAreAcceptedAndCompletedThroughTheStoreEachUnderItsOwnLeg()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await AcceptThroughTheStoreAsync(fixture, FirstLeg, WireLeg, "AGV-01", "VK-01", Now);
        await CompleteThroughTheStoreAsync(fixture, FirstLeg, Now.AddMinutes(10));
        await AcceptThroughTheStoreAsync(fixture, SecondLeg, Wire2Leg, "AGV-02", "VK-02", Now.AddHours(3));
        await CompleteThroughTheStoreAsync(fixture, SecondLeg, Now.AddHours(3).AddMinutes(10));

        await using ControlServerDbContext reading = fixture.NewContext();
        string key = TransportDemandKeys.Compose(Sublot, TransportTaskTypes.StagingToWire);
        Assert.Equal(
            [(FirstLeg, WireLeg, DemandExecutionStatus.Succeeded), (SecondLeg, Wire2Leg, DemandExecutionStatus.Succeeded)],
            (await reading.AcceptedDemands.AsNoTracking().Where(row => row.Sublot == Sublot).ToArrayAsync(Token))
                .Select(row => (row.DemandId, row.TransportLeg, row.Status)).Order());
        Assert.Equal(
            [(key, WireLeg, FirstLeg), (key, Wire2Leg, SecondLeg)],
            (await reading.TransportDemandCompletions.AsNoTracking().ToArrayAsync(Token))
                .Select(row => (row.TransportDemandKey, row.TransportLeg, row.DemandId)).Order());
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Key(string workType) => TransportDemandKeys.Compose(Sublot, workType);

    private static async Task AcceptThroughTheStoreAsync(
        Batch7JourneyFixture fixture, string demandId, string step, string agvId, string vehicleKey, DateTimeOffset at)
    {
        JourneyExecutionPlan plan = Batch7JourneyFixture.Plan(demandId, agvId, vehicleKey, at);
        await using ControlServerDbContext writing = fixture.NewContext();
        await new WireToGateStore(writing).AcceptWithOrderIntentAsync(
            Snapshot(demandId, TransportTaskTypes.StagingToWire, step) with { AcceptedAt = at },
            JourneyPlanBuilder.PickupIntent(plan, demandId, at),
            plan,
            Token);
    }

    private static async Task CompleteThroughTheStoreAsync(Batch7JourneyFixture fixture, string demandId, DateTimeOffset at)
    {
        await using ControlServerDbContext writing = fixture.NewContext();
        await new WireToGateStore(writing).CompleteDemandAfterUnloadAsync(
            $"UNLOAD-{demandId}",
            demandId,
            Key(TransportTaskTypes.StagingToWire),
            7,
            [new SlotPhysicalEvidence(1, SlotBusinessState.Empty, true, true)],
            "all-empty-locked-output-reset",
            at,
            Token);
    }

    private async Task<DispatchAdmissionChain> ChainAsync()
    {
        await _connection.OpenAsync(Token);
        _context = NewContext();
        await _context.Database.EnsureCreatedAsync(Token);
        return new DispatchAdmissionChain(DispatchAdmissionCriteria.Default(
            Options.Create(Configured),
            new MapStationResolver(),
            new PackageCapacityStore(_context),
            new WireToGateStore(_context),
            new VehicleFaultStore(_context),
            new JourneyRuntimeWorkerTestKit.RecordingBoxCounts(),
            NullLogger<SlotCapacityCriterion>.Instance,
            new TransportDemandSuppressionStore(_context),
            _context,
            TestChargingPolicies.AllApproved));
    }

    /// <summary>受理路径会写下的那一行：程按任务类型与受理时的 <c>STEP</c> 算（同 <c>WireToGateStore</c> 写行）。</summary>
    private async Task AcceptAsync(string demandId, string workType, string step, DemandExecutionStatus status)
    {
        await using ControlServerDbContext writing = NewContext();
        writing.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = demandId,
            SeriesId = $"SERIES-{Sublot}",
            TransportDemandKey = Key(workType),
            TransportLeg = TransportDemandIdentity.LegOf(workType, step),
            WorkType = workType,
            Sublot = Sublot,
            Generation = 1,
            DemandRevision = 7,
            HistoryEpoch = "11111111-1111-4111-8111-111111111111",
            CatalogRevision = 21,
            CreatedAt = Now.AddMinutes(-30),
            ValueObservedAt = Now.AddMinutes(-29),
            ValuePollTraceId = $"TRACE-{demandId}",
            ValueProjectionCommitId = $"COMMIT-{demandId}",
            LiveMesFieldsJson = System.Text.Json.JsonSerializer.Serialize(Fields(step)),
            AcceptedAt = Now.AddMinutes(-20),
            Status = status,
        });
        await writing.SaveChangesAsync(Token);
    }

    private async Task SuppressAsync(string demandId, string workType, string step, string reasonCode)
    {
        await using ControlServerDbContext writing = NewContext();
        await new TransportDemandSuppressionStore(writing).SuppressIfAbsentAsync(
            new TransportDemandSuppression(
                Key(workType), demandId, reasonCode, Now.AddMinutes(-10), TransportDemandIdentity.LegOf(workType, step)),
            Token);
    }

    private ControlServerDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);

    private static LiveMesFieldSet Fields(string step) =>
        new("N1-1", "EQP-01", step, Now.AddMinutes(-10), "PDFN5×6-8L(12R)");

    private static AcceptedDemandSnapshot Snapshot(string demandId, string workType, string step) => new(
        demandId,
        Key(workType),
        7,
        "11111111-1111-4111-8111-111111111111",
        21,
        Now,
        $"SERIES-{Sublot}",
        workType,
        Sublot,
        1,
        Now.AddMinutes(-10),
        Now.AddMinutes(-9),
        $"TRACE-{demandId}",
        $"COMMIT-{demandId}",
        Fields(step));

    private static DispatchCandidateEvaluation Evaluation(
        string demandId, string workType, string step, IReadOnlyCollection<string> acceptedThisServer)
    {
        AcceptedDemandSnapshot candidate = Snapshot(demandId, workType, step);
        DispatchRoundFacts round = new(
            new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, [candidate]),
            Map,
            new ScriptedView(taskType => taskType == TransportTaskTypes.StagingToWire
                ? FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Origin, Staging, 1, 1)
                : FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Destination, Gate, 1, 1)),
            new HashSet<string>(acceptedThisServer, StringComparer.Ordinal),
            Now,
            new VehicleDispatchPolicy(
                [new VehicleDispatchProfile(
                    "AGV-1",
                    new HashSet<string>(TransportTaskTypes.All, StringComparer.Ordinal),
                    30_000)],
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    ["MAP-25-WIRE_TO_GATE"] = new HashSet<string>(StringComparer.Ordinal) { "AGV-1" },
                },
                "TEST-POLICY"),
            new AreaAssignmentTableVersion(
                1,
                new string('a', 64),
                "snapshot-1",
                Now,
                new Dictionary<string, AreaAssignment>(StringComparer.Ordinal)
                {
                    ["N1-1"] = new("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"),
                }));
        return new DispatchCandidateEvaluation(
            candidate,
            round,
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now));
    }

    private sealed class ScriptedView(Func<string, FixedTaskStationResolution> resolve) : IFixedTaskStationView
    {
        public FixedTaskStationResolution Resolve(string taskType) => resolve(taskType);
    }
}
