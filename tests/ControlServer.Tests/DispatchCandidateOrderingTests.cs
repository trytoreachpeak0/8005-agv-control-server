using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 任务侧没有超时、没有 <c>STAGING_TO_WIRE</c> 时的次序：<b>按本地建单时刻（等待年龄），再按首次看到与需求 id 定序</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>这个类的参照换过两次。</b>批次7-04（control-server#209）时它钉的是「某辆车该接哪一条候选」；批次7-06
/// （control-server#211）把轮次翻成任务优先之后，它钉的是翻转前那个「先见先派，再按创建时刻与需求 id」的次序。
/// </para>
/// <para>
/// <b>批次7-09（control-server#214）有意把首次看到与建单时刻两层对调了。</b>REQ-0201 要等待年龄「从本地首次创建
/// TransportDemand 起持续累积」，而那个时刻是 MesIngest 目录项的 <c>CreatedAt</c>；<c>FirstSeenAt</c> 只在派车轮里有车
/// 判过这条候选时才写，车辆短缺时不走——理由写在 <see cref="TaskStarvation"/>。所以 <see cref="ReferenceOrder"/>
/// 现在先比建单时刻、再比首次看到；超时层与优先级带在全是普通带、没有处境的任务上都分不出高下，这张语料照样只看这三层。
/// 那两层自己的测试在 <see cref="Batch7TaskPriorityOrderingTests"/>。
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
    public void TheTaskOrderIsLocalCreationThenFirstSeenThenDemandId(Shape shape)
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

    /// <summary>
    /// 把注册表里先见与创建两层对调回批次7-06 的次序：这样搭出来的分层排序在语料上与参照分道扬镳，
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

    /// <summary>
    /// 本票的次序原样写出来：建单时刻、首次看到、需求 id。<paramref name="swapFirstSeenAndCreatedAt"/> 为真时是批次7-06
    /// 的旧次序（首次看到在前）。
    /// </summary>
    private static List<DispatchTask> ReferenceOrder(
        IReadOnlyList<DispatchTask> set,
        bool swapFirstSeenAndCreatedAt)
    {
        IOrderedEnumerable<DispatchTask> ordered = swapFirstSeenAndCreatedAt
            ? set.OrderBy(task => task.FirstSeenAt).ThenBy(task => task.Snapshot.CreatedAt)
            : set.OrderBy(task => task.Snapshot.CreatedAt).ThenBy(task => task.FirstSeenAt);
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
