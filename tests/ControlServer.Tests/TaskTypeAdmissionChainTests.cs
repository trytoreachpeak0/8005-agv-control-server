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
/// 按任务类型准入（control-server#160，规格 5.3、8.3 判据 ①）：判据链的公开入口
/// <see cref="DispatchAdmissionCriteria.Default"/> 加 <see cref="DispatchAdmissionChain"/>，对四种原因与范围外各给一例，
/// 且同一轮里一个任务类型被挡不连带其它任务类型。
/// </summary>
public sealed class TaskTypeAdmissionChainTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    private static readonly RiotMapStation Gate = new(210, "关卡");

    private static readonly RiotMapStation Staging = new(305, "派工待送取货");

    private static readonly RiotMapStationCatalogSnapshot Map = new(
        25, Now, new string('c', 64), [new RiotMapStation(12, "N1-1"), Gate, Staging]);

    private static readonly JourneyRuntimeOptions Configured = new()
    {
        MapId = 25,
        AllowedWorkTypes = [.. TransportTaskTypes.All],
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
        MinimumBatteryPercent = 30,
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

    /// <summary>
    /// 解析器给出的每一种拒绝都原样成为判决：缺绑定、绑定站点缺失、已暂停，以及规则表不认识的任务类型。
    /// 它们是需求与配置的事实、与车无关，所以在车辆判据之前判定：这里用的车不在派车策略里，链若先走到车辆判据，
    /// 判决会是 <c>VEHICLE_NOT_IN_DISPATCH_POLICY</c>。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData(DispatchReasonCodes.TaskTypeBindingMissing)]
    [InlineData(TaskTypeStationReasonCodes.BindingStationNotInCatalog)]
    [InlineData(DispatchReasonCodes.TaskTypeHeld)]
    [InlineData(DispatchReasonCodes.OutOfScopeWorkType)]
    public async Task EachRefusalOfTheTaskTypesBindingIsTheCandidatesVerdict(string refusal)
    {
        DispatchAdmissionChain chain = await ChainAsync(Configured);
        DispatchRoundFacts round = Round(taskType =>
            FixedTaskStationResolution.Refused(taskType, FixedStationEnd.Destination, refusal));

        Assert.Equal(
            refusal,
            await chain.EvaluateAsync(Evaluation(round, TransportTaskTypes.WireToGate, agvId: "AGV-NOT-IN-POLICY"), Token));
    }

    /// <summary>部署的 <c>AllowedWorkTypes</c> 里没有的任务类型仍是 <c>OUT_OF_SCOPE_WORK_TYPE</c>，哪怕它有绑定。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task ATaskTypeTheDeploymentDoesNotAllowIsOutOfScopeEvenWhenBound()
    {
        DispatchAdmissionChain chain = await ChainAsync(new JourneyRuntimeOptions
        {
            MapId = 25,
            AllowedWorkTypes = [TransportTaskTypes.StagingToWire],
            AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
        });

        Assert.Equal(
            DispatchReasonCodes.OutOfScopeWorkType,
            await chain.EvaluateAsync(Evaluation(Round(Bound), TransportTaskTypes.WireToGate), Token));
    }

    /// <summary>
    /// 绑定齐全、但本构建还不会执行的任务类型是「尚未可执行」；同一任务类型缺绑定时报的是缺绑定——两者可区分，
    /// 缺绑定在前，所以一条未绑定的 <c>STAGING_TO_WIRE</c> 需求今天就报缺绑定，而不是笼统挡成范围外。
    /// 「尚未可执行」的例子自 control-server#163 起是同向的 <c>DIE_TO_OVEN</c>（批次 10）：<c>STAGING_TO_WIRE</c> 已可执行。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task ABoundTaskTypeThisBuildCannotExecuteIsNotYetExecutableAndAMissingBindingIsNamedFirst()
    {
        DispatchAdmissionChain chain = await ChainAsync(Configured);

        Assert.Equal(
            DispatchReasonCodes.TaskTypeNotYetExecutable,
            await chain.EvaluateAsync(Evaluation(Round(Bound), TransportTaskTypes.DieToOven), Token));
        Assert.Equal(
            DispatchReasonCodes.TaskTypeBindingMissing,
            await chain.EvaluateAsync(
                Evaluation(
                    Round(taskType => FixedTaskStationResolution.Refused(
                        taskType, FixedStationEnd.Origin, DispatchReasonCodes.TaskTypeBindingMissing)),
                    TransportTaskTypes.StagingToWire),
                Token));
    }

    /// <summary>
    /// 同一轮：<c>STAGING_TO_WIRE</c> 缺绑定被挡，<c>WIRE_TO_GATE</c> 照常往下判——它走过了站点解析（有路线），
    /// 停在后面与任务类型无关的车辆判据上（这里没给车载端事实）。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AMissingBindingStopsOnlyItsOwnTaskTypeAndTheRestOfTheRoundIsJudgedAsUsual()
    {
        DispatchAdmissionChain chain = await ChainAsync(Configured);
        DispatchRoundFacts round = Round(taskType => taskType == TransportTaskTypes.StagingToWire
            ? FixedTaskStationResolution.Refused(taskType, FixedStationEnd.Origin, DispatchReasonCodes.TaskTypeBindingMissing)
            : Bound(taskType));

        DispatchCandidateEvaluation staging = Evaluation(round, TransportTaskTypes.StagingToWire);
        DispatchCandidateEvaluation gate = Evaluation(round, TransportTaskTypes.WireToGate);

        Assert.Equal(DispatchReasonCodes.TaskTypeBindingMissing, await chain.EvaluateAsync(staging, Token));
        Assert.Equal("ONBOARD_FACTS_NOT_READY", await chain.EvaluateAsync(gate, Token));
        Assert.Equal(Gate.StationName, gate.Route?.DropoffStationId);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FixedTaskStationResolution Bound(string taskType) => taskType == TransportTaskTypes.StagingToWire
        ? FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Origin, Staging, 1, 1)
        : FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Destination, Gate, 1, 1);

    private async Task<DispatchAdmissionChain> ChainAsync(JourneyRuntimeOptions options)
    {
        if (_context is null)
        {
            await _connection.OpenAsync(Token);
            _context = new ControlServerDbContext(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);
            await _context.Database.EnsureCreatedAsync(Token);
        }
        return new DispatchAdmissionChain(DispatchAdmissionCriteria.Default(
            Options.Create(options),
            new MapStationResolver(),
            new PackageCapacityStore(_context),
            new WireToGateStore(_context),
            new VehicleFaultStore(_context),
            new JourneyRuntimeWorkerTestKit.RecordingBoxCounts(),
            NullLogger<SlotCapacityCriterion>.Instance));
    }

    private static DispatchRoundFacts Round(Func<string, FixedTaskStationResolution> resolve) => new(
        new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, []),
        Map,
        new ScriptedView(resolve),
        new HashSet<string>(StringComparer.Ordinal),
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

    private static DispatchCandidateEvaluation Evaluation(DispatchRoundFacts round, string workType, string agvId = "AGV-1")
    {
        string demandId = $"DEMAND-{workType}";
        AcceptedDemandSnapshot candidate = new(
            demandId,
            $"SUBLOT-001|{workType}",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            $"SERIES-{demandId}",
            workType,
            "SUBLOT-001",
            1,
            Now.AddMinutes(-10),
            Now.AddMinutes(-9),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now.AddMinutes(-10), "PDFN5×6-8L(12R)"));
        DispatchRoundFacts withCandidate = round with
        {
            Catalog = round.Catalog with { Items = [.. round.Catalog.Items, candidate] },
        };
        return new DispatchCandidateEvaluation(
            candidate,
            withCandidate,
            new DispatchVehicleFacts(
                "BROKERX-0001",
                agvId,
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now));
    }

    private sealed class ScriptedView(Func<string, FixedTaskStationResolution> resolve) : IFixedTaskStationView
    {
        public FixedTaskStationResolution Resolve(string taskType) => resolve(taskType);
    }
}
