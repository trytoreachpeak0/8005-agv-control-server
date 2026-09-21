using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 任务侧的次序：<b>先见先派，再按需求创建时刻与需求 id 定序</b>——与批次7-06（control-server#211）把轮次翻成
/// 任务优先之前逐条相同。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个类的比较对象换了，因为被比较的东西换了。</b>批次7-04（control-server#209）时它钉的是
/// 「某辆车该接哪一条候选」，层里有两层比的是「这辆车到那个取货站多远」。翻成任务优先之后（REQ-0200：
/// 先定任务、再只为该任务选车），任务要在<b>还没有车</b>的时候就排出先后，与车有关的量在这一侧无从取值——
/// 那两层因此搬到了车辆侧，比的也从「到取货站的成本」换成了边际成本（REQ-0206）。
/// </para>
/// <para>
/// <b>没搬走的三层，次序一字未动</b>，而这正是本票对任务次序的承诺（票面第 5 条：任务层沿用今天的次序，
/// 优先级带与等待年龄由批次7-09（control-server#214）加在这一层上）。
/// <see cref="ReferenceOrder"/> 是 <c>FirstSeenDispatchCandidateRanker</c> 在 <c>fp/v2-impl@cc8e7992</c> 上的那个
/// 排序写出来的，所以对照不依赖本票动过的任何东西。
/// </para>
/// <para>
/// 语料从固定种子来，取值域故意很小：两个排序分道扬镳的地方是平手，而一堆互不相同的值几乎跟任何排序都一致。
/// </para>
/// </remarks>
public sealed class DispatchCandidateOrderingTests
{
    private const int Seed = 20260920;
    private const int SetsPerShape = 400;
    private static readonly DateTimeOffset Origin = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    public enum Shape
    {
        AllDistinct,
        EqualFirstSeen,
        EqualFirstSeenAndCreatedAt,
        OnlyDemandIdDiffers,
    }

    [Theory]
    [InlineData(Shape.AllDistinct)]
    [InlineData(Shape.EqualFirstSeen)]
    [InlineData(Shape.EqualFirstSeenAndCreatedAt)]
    [InlineData(Shape.OnlyDemandIdDiffers)]
    public void TheTaskOrderMatchesTheOneBeforeTheRoundWasInverted(Shape shape)
    {
        IDispatchCandidateRanker production = HostRanker();

        foreach (DispatchTask[] set in Generate(shape))
        {
            Assert.Equal(
                ReferenceOrder(set, swapFirstSeenAndCreatedAt: false).Select(task => task.Snapshot.DemandId),
                production.Order(set).Select(task => task.Snapshot.DemandId));
        }
    }

    /// <summary>
    /// 语料分得出两个排序：把参照里的先见与创建两层对调，它在某些集合上排出不同的次序。没有这一条，
    /// 上面那条等价可能只是因为语料太齐整，看不出一层被换了位置。
    /// </summary>
    [Fact]
    public void TheCorpusSeparatesAnOrderingWithTwoLayersSwapped() =>
        Assert.Contains(
            Enum.GetValues<Shape>().SelectMany(Generate),
            set => !ReferenceOrder(set, swapFirstSeenAndCreatedAt: false)
                .Select(task => task.Snapshot.DemandId)
                .SequenceEqual(ReferenceOrder(set, swapFirstSeenAndCreatedAt: true)
                    .Select(task => task.Snapshot.DemandId)));

    /// <summary>注册表里就这三层，按这个次序——成本那两层已经在车辆侧。</summary>
    [Fact]
    public void TheTaskSideRegistersExactlyTheThreeLayersThatDoNotNeedAVehicle() =>
        Assert.Equal(
            [typeof(FirstSeenLayer), typeof(DemandCreatedAtLayer), typeof(DemandIdOrdinalLayer)],
            DispatchCandidateOrdering.Layers().Select(layer => layer.GetType()).ToArray());

    /// <summary>
    /// 票面点名的那个注入故障：把注册表里先见与创建两层对调。这样搭出来的分层排序在语料上与参照分道扬镳，
    /// 所以上面那条等价正好在这个错误上变红。
    /// </summary>
    [Fact]
    public void SwappingTheFirstSeenAndCreatedAtLayersIsCaughtByTheCorpus()
    {
        IDispatchCandidateComparisonLayer[] layers = [.. DispatchCandidateOrdering.Layers()];
        int firstSeen = Array.FindIndex(layers, layer => layer is FirstSeenLayer);
        int createdAt = Array.FindIndex(layers, layer => layer is DemandCreatedAtLayer);
        (layers[firstSeen], layers[createdAt]) = (layers[createdAt], layers[firstSeen]);
        LayeredDispatchCandidateRanker swapped = new(layers);

        Assert.Contains(
            Enum.GetValues<Shape>().SelectMany(Generate),
            set => !ReferenceOrder(set, swapFirstSeenAndCreatedAt: false)
                .Select(task => task.Snapshot.DemandId)
                .SequenceEqual(swapped.Order(set).Select(task => task.Snapshot.DemandId)));
    }

    /// <summary>The ranker the host resolves: whatever <c>AddDispatchAdmission</c> registers.</summary>
    private static IDispatchCandidateRanker HostRanker()
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDispatchCandidateRanker>();
    }

    /// <summary><c>FirstSeenDispatchCandidateRanker</c> 在轮次翻转之前的那个排序，原样写出来。</summary>
    private static List<DispatchTask> ReferenceOrder(
        IReadOnlyList<DispatchTask> set,
        bool swapFirstSeenAndCreatedAt)
    {
        IOrderedEnumerable<DispatchTask> ordered = swapFirstSeenAndCreatedAt
            ? set.OrderBy(task => task.Snapshot.CreatedAt).ThenBy(task => task.FirstSeenAt)
            : set.OrderBy(task => task.FirstSeenAt).ThenBy(task => task.Snapshot.CreatedAt);
        return [.. ordered.ThenBy(task => task.Snapshot.DemandId, StringComparer.Ordinal)];
    }

    private static IEnumerable<DispatchTask[]> Generate(Shape shape)
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
            yield return [.. ids.Select(id => Task(shape, id, random))];
        }
    }

    private static DispatchTask Task(Shape shape, string demandId, Random random)
    {
        DateTimeOffset firstSeen = shape is Shape.AllDistinct
            ? Origin.AddMinutes(random.Next(0, 3))
            : Origin;
        DateTimeOffset createdAt = shape switch
        {
            Shape.AllDistinct or Shape.EqualFirstSeen => Origin.AddHours(-1).AddMinutes(random.Next(0, 3)),
            _ => Origin.AddHours(-1),
        };
        return new DispatchTask(
            new AcceptedDemandSnapshot(
                demandId,
                $"{demandId}|WIRE_TO_GATE",
                1,
                "11111111-1111-4111-8111-111111111111",
                21,
                Origin,
                CreatedAt: createdAt),
            firstSeen);
    }
}
