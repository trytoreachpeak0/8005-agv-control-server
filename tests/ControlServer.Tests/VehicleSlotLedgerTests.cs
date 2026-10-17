using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using Microsoft.Extensions.Logging.Abstractions;

namespace ControlServer.Tests;

/// <summary>
/// Batch 7-04 (control-server#209): how many of a vehicle's slots are free on each side reaches dispatch through one port,
/// and for an idle vehicle the port answers exactly what the slot capacity criterion used to read for itself -- the
/// session baseline Onboard reported.
/// </summary>
/// <remarks>
/// Since control-server#754 (REQ-0352) the port answers a count: acceptance reserves baskets, not slot numbers. The
/// baseline the count comes from is still the slots below, so the first three cases keep asserting the slots
/// (<see cref="SessionBaselineSlotLedger.BaselineSlots"/>) and that the port's count is theirs.
/// </remarks>
/// <remarks>
/// <see cref="BaselineBeforeTheMove"/> is the expression <c>SlotCapacityCriterion</c> used on
/// <c>fp/v2-impl@cc8e7992</c>, written out here so the four cases compare the port against it rather than against
/// itself.
/// </remarks>
public sealed class VehicleSlotLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    private static readonly VehicleSlotPositions EightSlot = new(
        "AGV-1",
        "SLOT-MODEL-1",
        VehicleSlotPositionSource.ActiveSlotConfiguration,
        new Dictionary<int, string>
        {
            [1] = "FRONT",
            [2] = "FRONT",
            [3] = "FRONT",
            [4] = "FRONT",
            [5] = "REAR",
            [6] = "REAR",
            [7] = "REAR",
            [8] = "REAR",
        });

    [Theory]
    [InlineData("FRONT", "1,2,3")]
    [InlineData("REAR", "6,8")]
    public async Task AnIdleVehiclesFreeSlotsOnEachSideAreItsSessionBaseline(string side, string expected)
    {
        int[] baseline = [8, 1, 3, 6, 2];

        DispatchVehicleFacts vehicle = Vehicle(baseline);
        IReadOnlyList<int> free = SessionBaselineSlotLedger.BaselineSlots(vehicle.Onboard, vehicle.SlotPositions, side);

        Assert.Equal(BaselineBeforeTheMove(baseline, side), free);
        Assert.Equal(expected, string.Join(',', free));
        Assert.Equal(free.Count, await new SessionBaselineSlotLedger().ReadAvailableSlotCountAsync(
            vehicle, side, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A slot Onboard reported disabled is not in the session baseline, and so not on either side: the ledger adds
    /// nothing back.
    /// </summary>
    [Fact]
    public async Task ADisabledSlotStaysOutOfItsSide()
    {
        int[] baselineWithoutDisabledSlotThree = [1, 2, 4, 5, 6, 7, 8];

        DispatchVehicleFacts vehicle = Vehicle(baselineWithoutDisabledSlotThree);
        IReadOnlyList<int> free = SessionBaselineSlotLedger.BaselineSlots(vehicle.Onboard, vehicle.SlotPositions, "FRONT");

        Assert.Equal(BaselineBeforeTheMove(baselineWithoutDisabledSlotThree, "FRONT"), free);
        Assert.Equal([1, 2, 4], free);
        Assert.Equal(3, await new SessionBaselineSlotLedger().ReadAvailableSlotCountAsync(
            vehicle, "FRONT", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// No session baseline -- no Ready session, or its snapshots not in -- is no free slot on any side, never an
    /// assumed empty vehicle.
    /// </summary>
    [Fact]
    public async Task AVehicleWithoutASessionBaselineHasNoFreeSlot()
    {
        DispatchVehicleFacts vehicle = Vehicle(available: null);

        Assert.Equal(0, await new SessionBaselineSlotLedger().ReadAvailableSlotCountAsync(
            vehicle, "FRONT", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The slot capacity criterion judges against what the ledger reports, not against the vehicle's facts: a full
    /// session baseline with a ledger that reports nothing free is refused, and the same baseline with one free slot
    /// on the ledger is admitted. Since control-server#754 the criterion chooses no slot numbers either way.
    /// </summary>
    [Theory]
    [InlineData(0, DispatchReasonCodes.SlotGroupOccupiedByOwnCargo)]
    [InlineData(1, DispatchAdmissionChain.Eligible)]
    public async Task TheSlotCapacityCriterionJudgesWhatTheLedgerReports(int ledgerCount, string expected)
    {
        DispatchCandidateEvaluation evaluation = Evaluation(Vehicle([1, 2, 3, 4, 5, 6, 7, 8]));

        string reason = await new SlotCapacityCriterion(
                new FourBoxes(), NullLogger<SlotCapacityCriterion>.Instance, new FixedLedger(ledgerCount))
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(expected, reason);
    }

    /// <summary>What <c>SlotCapacityCriterion</c> read for itself before the port existed.</summary>
    private static int[] BaselineBeforeTheMove(int[] available, string side) =>
    [
        .. available
            .Where(slot => EightSlot.SlotPositionByPhysicalSlot.TryGetValue(slot, out string? position) &&
                string.Equals(position, side, StringComparison.Ordinal))
            .Distinct()
            .Order()
    ];

    private static DispatchVehicleFacts Vehicle(int[]? available) => new(
        "BROKERX-1",
        "AGV-1",
        available is null ? null : new OnboardDispatchFacts(1, available, true, true, true, true, false),
        new RiotVehicleObservation("BROKERX-1", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
        Now,
        EightSlot);

    private static DispatchCandidateEvaluation Evaluation(DispatchVehicleFacts vehicle) =>
        new(
            new AcceptedDemandSnapshot(
                "10000000-0000-4000-8000-000000000001",
                "SUBLOT-001|WIRE_TO_GATE",
                7,
                "11111111-1111-4111-8111-111111111111",
                21,
                Now,
                Sublot: "SUBLOT-001"),
            new DispatchRoundFacts(
                new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, []),
                new RiotMapStationCatalogSnapshot(25, Now, new string('c', 64), []),
                null!,
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                new VehicleDispatchPolicy(
                    [], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY")),
            vehicle)
        {
            PackageCapacity = 4,
            AreaAssignment = new AreaAssignment("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"),
        };

    private sealed class FourBoxes : ISublotBoxCountReader
    {
        public Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken)
        {
            _ = sublot;
            _ = cancellationToken;
            return Task.FromResult<int?>(4);
        }
    }

    private sealed class FixedLedger(int free) : IVehicleSlotLedger
    {
        public Task<int> ReadAvailableSlotCountAsync(
            DispatchVehicleFacts vehicle,
            string slotPosition,
            CancellationToken cancellationToken)
        {
            _ = vehicle;
            _ = slotPosition;
            _ = cancellationToken;
            return Task.FromResult(free);
        }
    }
}
