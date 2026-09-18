using System.Text.Json;

namespace ControlServer.Domain;

/// <summary>
/// One <c>slotResults</c> entry's own verdict on its slot: the outcome and the reason codes the vehicle
/// gave. The physical half of the same entry is <see cref="SlotPhysicalEvidence"/>.
/// </summary>
public sealed record SlotOutcomeReport(int SlotNumber, string Outcome, IReadOnlyList<string> ReasonCodes)
{
    /// <summary>Reads the outcome half of a wire <c>slotResults</c> array.</summary>
    public static SlotOutcomeReport[] FromSlotResults(JsonElement slotResults) =>
        slotResults.EnumerateArray()
            .Select(slot => new SlotOutcomeReport(
                slot.GetProperty("slotNo").GetInt32(),
                slot.GetProperty("outcome").GetString()
                    ?? throw new InvalidDataException("OperationResult slot outcome is required."),
                slot.GetProperty("reasonCodes").EnumerateArray()
                    .Select(code => code.GetString()
                        ?? throw new InvalidDataException("OperationResult slot reason code is required."))
                    .ToArray()))
            .ToArray();
}

/// <summary>
/// When this server received an OperationResult, and the station departure deadline (ADR-cross-0055) that
/// stood for its journey at that moment -- null when there was none.
/// </summary>
public sealed record OperationResultReceipt(DateTimeOffset ReceivedAt, DateTimeOffset? StationDepartureDeadline);

public enum DeterminateLoadFailureVerdict
{
    /// <summary>Not a complete account of a failed load; the ordinary recovery judgement applies.</summary>
    NotDeterminate,

    /// <summary>A determinate failure this server settles itself (ADR-cross-0058 decision 5).</summary>
    Settleable,

    /// <summary>A complete account of a failure that arrived while the stop could not yet have run out.</summary>
    BeforeStationDeadline,

    /// <summary>A complete account of a failure whose reason the protocol gives no terminal state.</summary>
    ReasonWithoutTerminalState
}

/// <summary>
/// ADR-cross-0058 decision 5: a load that ran out its station deadline with every door shut is a
/// determinate failure, settled by this server rather than sent to manual recovery.
/// </summary>
/// <remarks>
/// <para>
/// <b>A defensive path.</b> The v2 onboard HMI never reports <c>FAILED</c> or <c>OPERATOR_TIMEOUT</c>: after
/// its deadline a closed, empty slot is reopened rather than failed (8005-agv-program#55). The protocol
/// still allows another onboard version to send one, so the server settles it rather than treating a
/// legal message as a fault. Nothing in the shipped pair reaches this path; L1 and the synthetic peer
/// are its only proof.
/// </para>
/// <para>
/// <b>Why the server may end the stop.</b> A determinate failure can only follow the stop's deadline
/// (ADR-cross-0058 Verification, decision 5), so when one arrives the condition for closing the stop
/// already holds. One that arrives before the deadline cannot be explained that way, and is not
/// silently accepted: it goes to recovery under <see cref="FailedBeforeStationDeadline"/>.
/// </para>
/// </remarks>
public static class DeterminateLoadFailure
{
    /// <summary>A determinate-looking load failure received before its station deadline, or with none.</summary>
    public const string FailedBeforeStationDeadline = "LOAD_FAILED_BEFORE_STATION_DEADLINE";

    /// <summary>A determinate-looking load failure whose reason codes name no terminal state.</summary>
    public const string ReasonWithoutTerminalState = "LOAD_FAILURE_REASON_WITHOUT_TERMINAL_STATE";

    /// <summary>
    /// The terminal reason each determinate failure reason ends its demand with. Protocol 2.0.0 registers
    /// exactly one error code for <c>OperationResult</c>, <c>OPERATOR_TIMEOUT</c>, whose meaning is this
    /// settlement (ADR-cross-0058 decision 5, candidate <c>errors/error-codes.json</c>); the registry
    /// defines no other determinate failure, so no other reason has a terminal state to end with.
    /// </summary>
    private static readonly Dictionary<string, string> TerminalReasons = new(StringComparer.Ordinal)
    {
        [ServerReasonCodes.OperatorTimeout] = "CANCELLED_BY_STATION_TIMEOUT"
    };

    private static readonly string[] DeterminateSlotOutcomes = ["COMPLETED", "FAILED", "NOT_STARTED"];

    public static DeterminateLoadFailureVerdict Judge(
        StationOperationResult result,
        IReadOnlyList<int> expectedSlots,
        OperationResultReceipt? receipt)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(expectedSlots);

        // Unload is excluded: ADR-cross-0015 gives it no cancellation branch, so an unload that misses its
        // target keeps closing the loop until the slots are empty.
        if (result.OperationType != SlotOperationType.Load ||
            result.OverallOutcome != "FAILED" ||
            result.SlotOutcomes is not { } outcomes)
        {
            return DeterminateLoadFailureVerdict.NotDeterminate;
        }

        int[] expected = expectedSlots.Order().ToArray();
        // Every commanded slot, each once, in both halves of the account.
        bool everySlotAccounted =
            result.SlotEvidence.Select(item => item.SlotNumber).Order().SequenceEqual(expected) &&
            outcomes.Select(item => item.SlotNumber).Order().SequenceEqual(expected);
        bool physicallyDeterminate = result.SlotEvidence.All(item =>
            item.State != SlotBusinessState.Unknown && item.DoorLocked && item.UnlockOutputReset);
        // An unstarted slot says NOT_STARTED rather than UNKNOWN (decision 6), and at least one slot is
        // the failure the overall outcome reports.
        bool outcomesDeterminate =
            outcomes.All(item => DeterminateSlotOutcomes.Contains(item.Outcome, StringComparer.Ordinal)) &&
            outcomes.Any(item => item.Outcome == "FAILED");
        if (!everySlotAccounted || !physicallyDeterminate || !outcomesDeterminate)
        {
            return DeterminateLoadFailureVerdict.NotDeterminate;
        }

        // Measured on this server's clock, against the deadline it sent the vehicle.
        if (receipt?.StationDepartureDeadline is not { } deadline || receipt.ReceivedAt < deadline)
        {
            return DeterminateLoadFailureVerdict.BeforeStationDeadline;
        }

        return TerminalReason(outcomes) is null
            ? DeterminateLoadFailureVerdict.ReasonWithoutTerminalState
            : DeterminateLoadFailureVerdict.Settleable;
    }

    /// <summary>
    /// The terminal reason a determinate load failure ends its demand with, or null when its failed slots
    /// do not all name reasons that map to one.
    /// </summary>
    public static string? TerminalReason(IEnumerable<SlotOutcomeReport> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        string[] failedSlotReasons = [.. outcomes.Where(item => item.Outcome == "FAILED")
            .SelectMany(item => item.ReasonCodes.Count == 0 ? [string.Empty] : item.ReasonCodes)];
        if (failedSlotReasons.Length == 0 ||
            !failedSlotReasons.All(TerminalReasons.ContainsKey))
        {
            return null;
        }
        string[] terminal = [.. failedSlotReasons.Select(code => TerminalReasons[code]).Distinct(StringComparer.Ordinal)];
        return terminal.Length == 1 ? terminal[0] : null;
    }
}
