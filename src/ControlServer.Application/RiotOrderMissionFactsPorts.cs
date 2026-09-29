namespace ControlServer.Application;

/// <summary>
/// One mission of a RIoT order exactly as <c>detailByUpperId</c> reported it. Every value is raw and nullable: a field RIoT
/// did not send reads back as null, never as 0.
/// </summary>
/// <param name="Type"><c>move</c> or <c>act</c> as RIoT reported it; anything else is passed through unchanged.</param>
/// <param name="MapId">The move's map. RIoT reports 0 on an act mission.</param>
/// <param name="Destination">The move's station. RIoT reports 0 on an act mission.</param>
/// <param name="ActionId">The act's action, e.g. 78 for charging; RIoT reports 0 on a move mission.</param>
/// <param name="ActionParam1">For action 78: 1 starts charging, 2 leaves the charger.</param>
/// <param name="ActionParam2">The act's second parameter.</param>
/// <param name="ResultCode">
/// RIoT's per-mission <c>resultCode</c>. The only evidence field for "unable to charge" (REQ-0174); the free-text
/// <c>resultStr</c> next to it is deliberately not carried (REQ-0175).
/// </param>
public sealed record RiotOrderMissionFact(
    string? Type,
    int? MapId,
    int? Destination,
    int? ActionId,
    int? ActionParam1,
    int? ActionParam2,
    int? ResultCode);

/// <summary>What the by-upperId read could say about the order.</summary>
public enum RiotOrderMissionFactsStatus
{
    /// <summary>RIoT returned this order; <see cref="RiotOrderMissionFacts.OrderState"/> and the missions are its answer.</summary>
    Found,

    /// <summary>RIoT answered 404 for this upperId.</summary>
    NotFound,

    /// <summary>
    /// RIoT could not be asked, answered without a usable order, or answered for another upperId. Nothing may be concluded.
    /// </summary>
    Unknown,
}

/// <summary>
/// The raw facts of one order read by its upperId: the order state (9 is HANG) and every mission in RIoT's order,
/// including the act missions RIoT adds or keeps around the moves.
/// </summary>
/// <remarks>
/// <b>This is a reading, not a verdict.</b> Whether an act result code and a HANG together mean "confirmed unable to charge"
/// (REQ-0174), and with which code, is decided by the caller (control-server#406, batch 9-08); no code value is interpreted
/// here.
/// </remarks>
public sealed record RiotOrderMissionFacts(
    string UpperId,
    RiotOrderMissionFactsStatus Status,
    string? OrderId,
    int? OrderState,
    IReadOnlyList<RiotOrderMissionFact> Missions,
    DateTimeOffset ObservedAt);

/// <summary>
/// Reads one order's state and missions by upperId through the approved <c>detailByUpperId</c> observation call
/// (<c>FindOrderByUpperIdAsync</c>; no new endpoint). Never throws for a RIoT failure; that reads as
/// <see cref="RiotOrderMissionFactsStatus.Unknown"/>.
/// </summary>
public interface IRiotOrderMissionFacts
{
    Task<RiotOrderMissionFacts> ReadOrderMissionFactsAsync(string upperId, CancellationToken cancellationToken);
}
