using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Tests;

/// <summary>
/// <c>REQ-0187</c> 跨任务类型（control-server#160 第 10 条）：唯一性看本轮目录的全部观测行，不按任务类型过滤。
/// </summary>
public sealed class AreaEqpUniqueAcrossTaskTypesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 6, 0, 0, TimeSpan.Zero);

    /// <summary>另一个任务类型的需求行里，同一 AREA 对应了另一台 EQP：本 AREA 的 <c>WIRE_TO_GATE</c> 候选也挡。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-10")]
    public async Task AnotherTaskTypesRowNamingTheSameAreaWithAnotherEqpBlocksTheCandidate()
    {
        AcceptedDemandSnapshot gate = Demand("D-GATE", TransportTaskTypes.WireToGate, "N1-3", "EQP-A");
        AcceptedDemandSnapshot other = Demand("D-OTHER", TransportTaskTypes.StagingToWire, "N1-3", "EQP-B");

        Assert.Equal("AREA_EQP_NOT_UNIQUE", await EvaluateAsync(gate, other));
    }

    /// <summary>没有冲突时照常放行：另一个任务类型的行同 AREA 同 EQP，或不同 AREA。</summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-10")]
    [InlineData("N1-3", "EQP-A")]
    [InlineData("N1-5", "EQP-B")]
    public async Task AnotherTaskTypesRowThatAgreesOrNamesAnotherAreaLetsTheCandidateThrough(string area, string eqp)
    {
        AcceptedDemandSnapshot gate = Demand("D-GATE", TransportTaskTypes.WireToGate, "N1-3", "EQP-A");
        AcceptedDemandSnapshot other = Demand("D-OTHER", TransportTaskTypes.StagingToWire, area, eqp);

        Assert.Equal(DispatchAdmissionChain.Eligible, await EvaluateAsync(gate, other));
    }

    private static Task<string> EvaluateAsync(AcceptedDemandSnapshot candidate, params AcceptedDemandSnapshot[] others) =>
        new AreaEqpUniqueCriterion().EvaluateAsync(
            new DispatchCandidateEvaluation(
                candidate,
                new DispatchRoundFacts(
                    new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate, .. others]),
                    new RiotMapStationCatalogSnapshot(25, Now, new string('c', 64), []),
                    new SingleStationView(new RiotMapStation(210, "关卡")),
                    new HashSet<string>(StringComparer.Ordinal),
                    Now,
                    new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY")),
                new DispatchVehicleFacts(
                    "BROKERX-0001",
                    "AGV-1",
                    null,
                    new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                    Now)),
            TestContext.Current.CancellationToken);

    private static AcceptedDemandSnapshot Demand(string demandId, string taskType, string area, string eqp) => new(
        demandId,
        $"SUBLOT-{demandId}|{taskType}",
        7,
        "11111111-1111-4111-8111-111111111111",
        21,
        Now,
        $"SERIES-{demandId}",
        taskType,
        $"SUBLOT-{demandId}",
        1,
        Now.AddMinutes(-10),
        Now.AddMinutes(-9),
        $"TRACE-{demandId}",
        $"COMMIT-{demandId}",
        new LiveMesFieldSet(area, eqp, "STEP-01", Now, "PDFN5×6-8L(12R)"));
}
