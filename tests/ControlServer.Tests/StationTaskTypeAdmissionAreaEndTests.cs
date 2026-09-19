using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 站点任务类型准入跟着路线的 AREA 机台端走（control-server#160 第 9 条，推翻 I6 判据一侧）：固定端是终点的路线查取货端，
/// 固定端是起点的路线（<c>STAGING_TO_WIRE</c>）查卸货端。<c>WIRE_TO_GATE</c> 的判定与改动前相同。
/// </summary>
public sealed class StationTaskTypeAdmissionAreaEndTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private ControlServerDbContext? _context;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// <c>WIRE_TO_GATE</c>（固定端是终点）：AREA 站是取货端，准入看它；只准入了关卡而没准入机台站时被拒——与改动前一样。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData("N1-1", DispatchAdmissionChain.Eligible)]
    [InlineData("关卡", "TASK_TYPE_NOT_ALLOWED_AT_STATION")]
    public async Task ADestinationEndRouteIsAdmittedAtItsPickupStation(string admittedStation, string expected)
    {
        WireToGateStore store = await StoreAsync(new StationTaskTypeAdmission(admittedStation, TransportTaskTypes.WireToGate));

        string verdict = await new StationTaskTypeAdmissionCriterion(store).EvaluateAsync(
            Evaluation(TransportTaskTypes.WireToGate, FixedStationEnd.Destination, pickup: "N1-1", dropoff: "关卡"), Token);

        Assert.Equal(expected, verdict);
    }

    /// <summary>
    /// 固定端是起点的路线（<c>STAGING_TO_WIRE</c>）：取货端是固定站，AREA 机台是卸货端，准入看卸货端。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData("N1-1", DispatchAdmissionChain.Eligible)]
    [InlineData("派工待送取货", "TASK_TYPE_NOT_ALLOWED_AT_STATION")]
    public async Task AnOriginEndRouteIsAdmittedAtItsDropoffStation(string admittedStation, string expected)
    {
        WireToGateStore store = await StoreAsync(new StationTaskTypeAdmission(admittedStation, TransportTaskTypes.StagingToWire));

        string verdict = await new StationTaskTypeAdmissionCriterion(store).EvaluateAsync(
            Evaluation(TransportTaskTypes.StagingToWire, FixedStationEnd.Origin, pickup: "派工待送取货", dropoff: "N1-1"), Token);

        Assert.Equal(expected, verdict);
    }

    private async Task<WireToGateStore> StoreAsync(StationTaskTypeAdmission admission)
    {
        await _connection.OpenAsync(Token);
        _context = new ControlServerDbContext(
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync(Token);
        WireToGateStore store = new(_context);
        await store.ApplyAdmissionPolicyAsync(new AdmissionPolicyDefinition(1, "TEST-DEPLOYMENT", [admission], Now), Token);
        return store;
    }

    private static DispatchCandidateEvaluation Evaluation(string taskType, FixedStationEnd end, string pickup, string dropoff)
    {
        AcceptedDemandSnapshot candidate = new(
            "DEMAND-1",
            $"SUBLOT-001|{taskType}",
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            "SERIES-1",
            taskType,
            "SUBLOT-001",
            1,
            Now.AddMinutes(-10),
            Now.AddMinutes(-9),
            "TRACE-1",
            "COMMIT-1",
            new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now, "PDFN5×6-8L(12R)"));
        RiotMapStation fixedStation = end == FixedStationEnd.Origin ? new(305, pickup) : new(210, dropoff);
        return new DispatchCandidateEvaluation(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate]),
                new RiotMapStationCatalogSnapshot(25, Now, new string('c', 64), []),
                new SingleStationView(fixedStation),
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY")),
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now))
        {
            Route = new ResolvedJourneyRoute(
                "MAP-25-WIRE_TO_GATE",
                "MAPCAT-1",
                pickup,
                end == FixedStationEnd.Origin ? 305 : 12,
                dropoff,
                end == FixedStationEnd.Origin ? 12 : 210,
                FixedTaskStationResolution.Resolved(taskType, end, fixedStation, 1, 1)),
        };
    }
}
