using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;

namespace ControlServer.Tests;

/// <summary>
/// Batch 7-04 (control-server#209): which eligible candidate a vehicle takes is decided exactly as it was before the
/// ranking became a list of comparison layers.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReferenceRanker"/> is <c>RouteGraphCostRanker</c> and <c>FirstSeenDispatchCandidateRanker</c> as they
/// stood on <c>fp/v2-impl@cc8e7992</c>, copied here verbatim in behaviour, so the comparison does not depend on
/// anything the move touches. Every generated set is ranked by both, and both the pick and the whole order (the pick
/// taken again from what is left, until nothing is) must agree candidate for candidate.
/// </para>
/// <para>
/// The sets come from a fixed seed and are drawn from small value domains on purpose: ties are where two orderings
/// part, and a corpus of distinct values would agree with almost any sort.
/// </para>
/// </remarks>
public sealed class DispatchCandidateOrderingTests
{
    private const int Seed = 20260920;
    private const int SetsPerShape = 400;
    private static readonly DateTimeOffset Origin = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    public enum Shape
    {
        AllPriced,
        NonePriced,
        SomePriced,
        EqualCosts,
        EqualFirstSeen,
        OnlyDemandIdDiffers,
    }

    [Theory]
    [InlineData(Shape.AllPriced)]
    [InlineData(Shape.NonePriced)]
    [InlineData(Shape.SomePriced)]
    [InlineData(Shape.EqualCosts)]
    [InlineData(Shape.EqualFirstSeen)]
    [InlineData(Shape.OnlyDemandIdDiffers)]
    public void TheProductionRankerOrdersEveryGeneratedSetAsTheRankerBeforeTheMoveDid(Shape shape)
    {
        RouteGraphCostRanker production = ProductionRanker();
        ReferenceRanker reference = new(swapFirstSeenAndCreatedAt: false);

        foreach (EligibleDispatchCandidate[] set in Generate(shape))
        {
            Assert.Same(reference.SelectNext(set), production.SelectNext(set));
            Assert.Equal(
                FullOrder(reference, set).Select(candidate => candidate.Snapshot.DemandId),
                FullOrder(production, set).Select(candidate => candidate.Snapshot.DemandId));
        }
    }

    /// <summary>
    /// The corpus can tell two orderings apart: the reference with its first-seen and created-at layers swapped
    /// picks differently on some generated set. Without this the equivalence above could pass on a corpus too
    /// uniform to notice a reordered layer.
    /// </summary>
    [Fact]
    public void TheCorpusSeparatesAnOrderingWithTwoLayersSwapped()
    {
        ReferenceRanker reference = new(swapFirstSeenAndCreatedAt: false);
        ReferenceRanker swapped = new(swapFirstSeenAndCreatedAt: true);

        Assert.Contains(
            Enum.GetValues<Shape>().SelectMany(Generate),
            set => !ReferenceEquals(reference.SelectNext(set), swapped.SelectNext(set)));
    }

    /// <summary>The ranker the host registers.</summary>
    private static RouteGraphCostRanker ProductionRanker() => new();

    /// <summary>Picks, then picks again from what is left, until every candidate has been picked.</summary>
    private static List<EligibleDispatchCandidate> FullOrder(
        IDispatchCandidateRanker ranker,
        IEnumerable<EligibleDispatchCandidate> set)
    {
        List<EligibleDispatchCandidate> left = [.. set];
        List<EligibleDispatchCandidate> order = [];
        while (left.Count > 0)
        {
            EligibleDispatchCandidate next = ranker.SelectNext(left);
            order.Add(next);
            left.Remove(next);
        }

        return order;
    }

    private static IEnumerable<EligibleDispatchCandidate[]> Generate(Shape shape)
    {
        Random random = new(Seed + (int)shape);
        for (int set = 0; set < SetsPerShape; set++)
        {
            int size = random.Next(2, 8);
            // Distinct ids whose ordinal order is not their case-insensitive order, so a culture-aware
            // comparison of the last layer would show.
            string[] ids = [.. Enumerable.Range(0, size)
                .Select(index => $"D-{(char)(random.Next(2) == 0 ? 'a' + index : 'A' + index)}{index}")
                .OrderBy(_ => random.Next())];
            yield return [.. ids.Select(id => Candidate(shape, id, random))];
        }
    }

    private static EligibleDispatchCandidate Candidate(Shape shape, string demandId, Random random)
    {
        long? cost = shape switch
        {
            Shape.AllPriced => random.Next(1, 4) * 1000L,
            Shape.NonePriced => null,
            Shape.SomePriced => random.Next(2) == 0 ? null : random.Next(1, 4) * 1000L,
            Shape.EqualCosts => 5000L,
            Shape.EqualFirstSeen => random.Next(3) == 0 ? null : random.Next(1, 3) * 1000L,
            Shape.OnlyDemandIdDiffers => 5000L,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        DateTimeOffset firstSeen = shape is Shape.EqualFirstSeen or Shape.OnlyDemandIdDiffers
            ? Origin
            : Origin.AddMinutes(random.Next(0, 3));
        DateTimeOffset createdAt = shape is Shape.OnlyDemandIdDiffers
            ? Origin.AddHours(-1)
            : Origin.AddHours(-1).AddMinutes(random.Next(0, 3));
        return new EligibleDispatchCandidate(
            new AcceptedDemandSnapshot(
                demandId,
                $"{demandId}|WIRE_TO_GATE",
                1,
                "11111111-1111-4111-8111-111111111111",
                21,
                Origin,
                CreatedAt: createdAt),
            new ResolvedJourneyRoute(
                "MAP-25-WIRE_TO_GATE",
                "MAPCAT-1",
                "N1-1",
                12,
                "关卡",
                210,
                FixedTaskStationResolution.Resolved(
                    "WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
            1,
            [1],
            firstSeen,
            cost);
    }

    /// <summary>
    /// <c>RouteGraphCostRanker</c> over <c>FirstSeenDispatchCandidateRanker</c> as they stood before the move.
    /// </summary>
    /// <param name="swapFirstSeenAndCreatedAt">
    /// Orders by created-at before first-seen: the injected fault the corpus must be able to see.
    /// </param>
    private sealed class ReferenceRanker(bool swapFirstSeenAndCreatedAt) : IDispatchCandidateRanker
    {
        public EligibleDispatchCandidate SelectNext(IReadOnlyList<EligibleDispatchCandidate> eligible)
        {
            List<EligibleDispatchCandidate> priced =
                eligible.Where(candidate => candidate.GraphTraversalCostMm.HasValue).ToList();
            IEnumerable<EligibleDispatchCandidate> pool = priced.Count == 0 ? eligible : priced;
            IOrderedEnumerable<EligibleDispatchCandidate> ordered = priced.Count == 0
                ? pool.OrderBy(_ => 0)
                : pool.OrderBy(candidate => candidate.GraphTraversalCostMm!.Value);
            ordered = swapFirstSeenAndCreatedAt
                ? ordered.ThenBy(candidate => candidate.Snapshot.CreatedAt).ThenBy(candidate => candidate.FirstSeenAt)
                : ordered.ThenBy(candidate => candidate.FirstSeenAt).ThenBy(candidate => candidate.Snapshot.CreatedAt);
            return ordered
                .ThenBy(candidate => candidate.Snapshot.DemandId, StringComparer.Ordinal)
                .First();
        }
    }
}
