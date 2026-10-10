using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Fleet;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// The round-end summary that tells "no vehicle right now" from "no vehicle ever" (REQ-0210, REQ-0352): it reads
/// every finished vehicle's verdict on every candidate, raises a structural dispatch block for a demand no vehicle
/// could take, and clears it once that stops being true.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is concluded per demand</b>, from the verdicts of the vehicles whose segment of the round finished,
/// sorted by <see cref="StructuralDispatchClassification"/>:
/// </para>
/// <list type="bullet">
/// <item>A <see cref="DispatchReasonClass.Structural"/> code from any of them raises that code.</item>
/// <item>
/// <see cref="DispatchReasonClass.StructuralWhenEveryVehicleReturnsIt"/> raises only when every vehicle on the
/// roster finished this round and every one returned it. A vehicle that was busy with a journey, or ran out its
/// budget, was not asked, so the round cannot say it would have refused.
/// </item>
/// <item>
/// <see cref="DispatchReasonClass.StructuralWhenNoVehicleHasTheSlots"/> is decided on the fleet's slot models, not
/// on the verdicts: once any vehicle's chain worked out the basket count, the demand is structural when that count
/// exceeds the largest physical slot count its group has on any roster vehicle
/// (<see cref="IVehicleSlotPositionReader.ReadLargestGroupCapacityAsync"/>), whatever is disabled or occupied. A
/// roster vehicle with no slot model on record leaves it undecided: it might be the one that fits.
/// </item>
/// </list>
/// <para>
/// <b>What clears a block</b>: the demand leaving the catalog, the demand being accepted, the demand turning out
/// silent (<see cref="DispatchReasonCodes.IsSilent"/>), or a verdict that proves the reason gone — a vehicle's
/// chain getting past the criterion that returned it with some other answer, or the fleet check coming out the
/// other way. <b>A round that proves nothing either way leaves the block as it is</b>, neither refreshed nor
/// cleared: a vehicle refused at its fault check never reached station resolution, and clearing on that would
/// raise the same alarm again, as new, the next round the vehicle is healthy.
/// </para>
/// <para>
/// <b>Nothing here changes what the round dispatched</b>: it runs after the vehicle loop, reads the verdicts the
/// chain already reached, and writes only <c>StructuralDispatchBlocks</c>. A structural demand is refused on every
/// vehicle's chain in the first place, so it is never selected and never partly loaded.
/// </para>
/// <para>
/// <b>REQ-0210 is only partly implemented here.</b> Escalating an alarm once a zone's anti-starvation threshold is
/// reached is <see cref="StarvationEscalationSink"/>'s (control-server#214), the round-end sink registered after this one.
/// </para>
/// </remarks>
public sealed class StructuralDispatchBlockSink(
    IStructuralDispatchBlockStore blocks,
    IVehicleSlotPositionReader slotPositions,
    VehicleRoster roster,
    ILogger<StructuralDispatchBlockSink> logger) : IDispatchRoundOutcomeSink
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, string, string, string, Exception?> LogBlockRaised =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2114, nameof(LogBlockRaised)),
            "Structural dispatch block raised: no vehicle can take demand {DemandId} ({TransportDemandKey}), " +
            "reason {ReasonCode}. It will not be dispatched; carry it by hand.");

    private static readonly Action<ILogger, string, string, Exception?> LogBlockCleared =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2115, nameof(LogBlockCleared)),
            "Structural dispatch block cleared for demand {DemandId}, reason {ReasonCode}.");

    public async Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        DispatchRoundFacts round = outcome.Round;
        DateTimeOffset now = round.Now;
        IReadOnlyList<StructuralDispatchBlock> uncleared =
            await blocks.ListUnclearedAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, AcceptedDemandSnapshot> candidates = new(StringComparer.Ordinal);
        foreach (AcceptedDemandSnapshot item in round.Catalog.Items)
        {
            candidates.TryAdd(item.DemandId, item);
        }

        // A demand the round no longer considers is not blocked by anything any more. A claim intake refused is
        // not one of those (control-server#242): the demand is still in the catalog and this server never took
        // it, so nothing about this block was disproved and clearing it would raise the same alarm as new next
        // round. The loop below leaves it alone too -- it is still in AcceptedDemandIds, which is what stops the
        // round drawing any conclusion about a demand one of its vehicles was busy with.
        HashSet<string> settled = new(StringComparer.Ordinal);
        foreach (StructuralDispatchBlock block in uncleared)
        {
            if (!candidates.ContainsKey(block.DemandId) ||
                (round.AcceptedDemandIds.Contains(block.DemandId) &&
                 !round.ClaimsIntakeRefused.Contains(block.DemandId)))
            {
                await ClearAsync(block, now, cancellationToken).ConfigureAwait(false);
                settled.Add(block.DemandId);
            }
        }

        Dictionary<string, List<RoundVerdict>> verdictsByDemand = new(StringComparer.Ordinal);
        foreach (DispatchVehicleOutcome vehicle in outcome.CompletedVehicles)
        {
            foreach (DispatchCandidateVerdict verdict in vehicle.Verdicts)
            {
                string demandId = verdict.Evaluation.Candidate.DemandId;
                if (!verdictsByDemand.TryGetValue(demandId, out List<RoundVerdict>? list))
                {
                    verdictsByDemand[demandId] = list = [];
                }
                list.Add(new RoundVerdict(vehicle.AgvId, verdict));
            }
        }

        string[] rosterAgvIds = [.. roster.Vehicles.Select(vehicle => vehicle.AgvId)];
        HashSet<string> finished = outcome.CompletedVehicles
            .Select(vehicle => vehicle.AgvId)
            .ToHashSet(StringComparer.Ordinal);
        bool everyVehicleFinished = rosterAgvIds.All(finished.Contains);
        Dictionary<string, SlotPositionGroupCapacity> capacityByGroup = new(StringComparer.Ordinal);

        foreach ((string demandId, AcceptedDemandSnapshot candidate) in candidates)
        {
            if (round.AcceptedDemandIds.Contains(demandId) ||
                !verdictsByDemand.TryGetValue(demandId, out List<RoundVerdict>? verdicts))
            {
                continue;
            }

            StructuralDispatchBlock[] existing =
                settled.Contains(demandId) ? [] : [.. uncleared.Where(block => block.DemandId == demandId)];
            DemandJudgement judgement = await JudgeAsync(
                verdicts, everyVehicleFinished, rosterAgvIds, capacityByGroup, cancellationToken).ConfigureAwait(false);

            foreach ((string reasonCode, object detail) in judgement.Holding)
            {
                bool isNew = !existing.Any(block => block.ReasonCode == reasonCode);
                await blocks.RaiseOrRefreshAsync(
                    demandId,
                    reasonCode,
                    candidate.TransportDemandKey,
                    JsonSerializer.Serialize(detail, SerializerOptions),
                    now,
                    cancellationToken).ConfigureAwait(false);
                if (isNew)
                {
                    LogBlockRaised(logger, demandId, candidate.TransportDemandKey, reasonCode, null);
                }
            }

            foreach (StructuralDispatchBlock block in existing)
            {
                if (!judgement.Holding.ContainsKey(block.ReasonCode) && judgement.IsGone(block.ReasonCode))
                {
                    await ClearAsync(block, now, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<DemandJudgement> JudgeAsync(
        List<RoundVerdict> verdicts,
        bool everyVehicleFinished,
        string[] rosterAgvIds,
        Dictionary<string, SlotPositionGroupCapacity> capacityByGroup,
        CancellationToken cancellationToken)
    {
        if (verdicts.Any(verdict => DispatchReasonCodes.IsSilent(verdict.ReasonCode)))
        {
            // The AREA is out of scope by configuration: nothing about this demand is alarmed, whatever else holds.
            return DemandJudgement.Silent;
        }

        Dictionary<string, object> holding = new(StringComparer.Ordinal);
        foreach (IGrouping<string, RoundVerdict> code in verdicts
                     .Where(verdict => StructuralDispatchClassification.ClassOf(verdict.ReasonCode)
                         == DispatchReasonClass.Structural)
                     .GroupBy(verdict => verdict.ReasonCode, StringComparer.Ordinal))
        {
            holding[code.Key] = new { agvIds = code.Select(verdict => verdict.AgvId).ToArray() };
        }

        foreach (IGrouping<string, RoundVerdict> code in verdicts
                     .Where(verdict => StructuralDispatchClassification.ClassOf(verdict.ReasonCode)
                         == DispatchReasonClass.StructuralWhenEveryVehicleReturnsIt)
                     .GroupBy(verdict => verdict.ReasonCode, StringComparer.Ordinal))
        {
            if (everyVehicleFinished && verdicts.All(verdict => verdict.ReasonCode == code.Key))
            {
                holding[code.Key] = new { agvIds = code.Select(verdict => verdict.AgvId).ToArray() };
            }
        }

        // The basket count is only worked out once a vehicle's chain reached the slot check; the latest one wins.
        bool? oversized = null;
        RoundVerdict? sized = verdicts.LastOrDefault(verdict =>
            verdict.Verdict.Evaluation.ExpectedBasketCount >= 1 &&
            verdict.Verdict.Evaluation.RequiredSlotPosition is not null);
        if (sized is not null)
        {
            DispatchCandidateEvaluation evaluation = sized.Verdict.Evaluation;
            string group = evaluation.RequiredSlotPosition!;
            if (!capacityByGroup.TryGetValue(group, out SlotPositionGroupCapacity? capacity))
            {
                capacity = await slotPositions
                    .ReadLargestGroupCapacityAsync(rosterAgvIds, group, cancellationToken).ConfigureAwait(false);
                capacityByGroup[group] = capacity;
            }
            if (capacity.UnresolvedAgvIds.Count == 0)
            {
                oversized = evaluation.ExpectedBasketCount > capacity.LargestPhysicalSlotCount;
                if (oversized.Value)
                {
                    holding[DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup] = new
                    {
                        expectedBasketCount = evaluation.ExpectedBasketCount,
                        slotPosition = group,
                        largestPhysicalSlotCount = capacity.LargestPhysicalSlotCount,
                        agvIds = rosterAgvIds,
                    };
                }
            }
        }

        return new DemandJudgement(holding, verdicts, oversized);
    }

    private async Task ClearAsync(StructuralDispatchBlock block, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await blocks.ClearAsync(block.DemandId, block.ReasonCode, now, cancellationToken).ConfigureAwait(false))
        {
            LogBlockCleared(logger, block.DemandId, block.ReasonCode, null);
        }
    }

    private sealed record RoundVerdict(string AgvId, DispatchCandidateVerdict Verdict)
    {
        public string ReasonCode => Verdict.ReasonCode;
    }

    /// <summary>What one round proved about one demand.</summary>
    /// <param name="Holding">The structural reasons that hold, with the detail each is raised with.</param>
    /// <param name="Verdicts">The finished vehicles' verdicts on the demand.</param>
    /// <param name="Oversized">
    /// Whether the fleet check found the demand larger than its group on every vehicle; null when it could not be
    /// decided this round.
    /// </param>
    private sealed record DemandJudgement(
        IReadOnlyDictionary<string, object> Holding,
        IReadOnlyList<RoundVerdict> Verdicts,
        bool? Oversized,
        bool IsSilent = false)
    {
        public static DemandJudgement Silent { get; } =
            new(new Dictionary<string, object>(StringComparer.Ordinal), [], null, IsSilent: true);

        /// <summary>Whether this round proves that <paramref name="reasonCode"/> no longer holds.</summary>
        public bool IsGone(string reasonCode)
        {
            if (IsSilent)
            {
                return true;
            }
            DispatchReasonClass reasonClass = StructuralDispatchClassification.ClassOf(reasonCode);
            int? order = StructuralDispatchClassification.ByCode.GetValueOrDefault(reasonCode)?.ChainOrder;
            return reasonClass switch
            {
                // Some vehicle's chain got as far as the criterion that returned it and answered otherwise.
                DispatchReasonClass.Structural => order is { } at && Verdicts.Any(verdict =>
                    verdict.ReasonCode != reasonCode &&
                    StructuralDispatchClassification.Reached(verdict.ReasonCode, at)),
                // Some vehicle's chain got past the reachability check.
                DispatchReasonClass.StructuralWhenEveryVehicleReturnsIt => order is { } at && Verdicts.Any(verdict =>
                    StructuralDispatchClassification.Reached(verdict.ReasonCode, at + 1)),
                DispatchReasonClass.StructuralWhenNoVehicleHasTheSlots => Oversized == false,
                // Not a structural reason at all; nothing should have raised it.
                _ => true,
            };
        }
    }
}
