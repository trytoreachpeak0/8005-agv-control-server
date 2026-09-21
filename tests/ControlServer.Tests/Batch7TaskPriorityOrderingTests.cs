using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 任务侧的三层（批次7-09，control-server#214）：优先级带（REQ-0202）、等待年龄（REQ-0201）、超时层（REQ-0202、REQ-0203）。
/// </summary>
/// <remarks>
/// <para>
/// 排序经宿主注册的排序器走（<c>AddDispatchAdmission</c>），不经某一层单独比：层写对了而注册表里次序排错，
/// 单层测试照样绿。处境（<see cref="TaskStarvationStanding"/>）经 <see cref="TaskStarvation.Assess"/> 算，
/// 与派车轮算它的是同一个函数。
/// </para>
/// <para>
/// 年龄故意与首次看到反着排：两者次序相同的语料分不出排序用的是哪一个——而本票定下的起点正是二者之一。
/// </para>
/// </remarks>
public sealed class Batch7TaskPriorityOrderingTests
{
    private const string Zone = "MAP-25-WIRE_TO_GATE";
    private const string OtherZone = "MAP-25-OTHER";
    private const string Area = "N1-1";
    private const string OtherZoneArea = "N2-1";
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] NormalBandTypes =
    [
        TransportTaskTypes.WireToOptical,
        TransportTaskTypes.WireToNitrogen,
        TransportTaskTypes.WireToGate,
        TransportTaskTypes.DieToWireStaging,
        TransportTaskTypes.DieToOven,
    ];

    // ---- 优先级带 ------------------------------------------------------------------------------------

    /// <summary>刚建的 <c>STAGING_TO_WIRE</c> 排在等了更久的普通带任务之前（REQ-0202）。</summary>
    [Fact]
    public void ANewStagingToWireTaskIsTakenBeforeAnOlderNormalBandTask()
    {
        DispatchTask olderWireToGate = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 30);
        DispatchTask newStaging = Task("D-2", TransportTaskTypes.StagingToWire, createdMinutesAgo: 1);

        Assert.Equal(["D-2", "D-1"], Order(olderWireToGate, newStaging));
    }

    /// <summary>
    /// 普通带五类之间不按类型细分，只按年龄：把五类按类型名反序、按年龄正序排出来，次序只跟年龄走。
    /// </summary>
    [Fact]
    public void TheFiveNormalBandTaskTypesAreOrderedByWaitingAgeAlone()
    {
        // 类型名的序数次序与年龄次序相反，所以任何按类型细分的比较都会把它们排乱。
        DispatchTask[] tasks =
        [
            .. NormalBandTypes
                .Order(StringComparer.Ordinal)
                .Select((type, index) => Task($"D-{index}", type, createdMinutesAgo: 10 + index)),
        ];

        Assert.Equal(
            [.. tasks.OrderByDescending(task => Now - task.Snapshot.CreatedAt).Select(task => task.Snapshot.DemandId)],
            Order(tasks));
    }

    // ---- 等待年龄 ------------------------------------------------------------------------------------

    /// <summary>
    /// 年龄从 MesIngest 目录项的 <c>CreatedAt</c>（本地首次创建 TransportDemand）起算，<c>FirstSeenAt</c> 只作平手：
    /// 建得早、却被本服务端晚看到的那条先派。
    /// </summary>
    [Fact]
    public void WaitingAgeRunsFromTheLocalCreationOfTheDemandNotFromWhenThisServerFirstSawIt()
    {
        DispatchTask createdEarlierSeenLater = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 40, firstSeenMinutesAgo: 2);
        DispatchTask createdLaterSeenEarlier = Task("D-2", TransportTaskTypes.WireToGate, createdMinutesAgo: 20, firstSeenMinutesAgo: 19);

        Assert.Equal(["D-1", "D-2"], Order(createdLaterSeenEarlier, createdEarlierSeenLater));
    }

    /// <summary>建单时刻相同时，先被看到的先派：<c>FirstSeenAt</c> 仍是平手键。</summary>
    [Fact]
    public void FirstSeenStillBreaksATieBetweenTwoDemandsCreatedTogether()
    {
        DispatchTask seenLater = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 20, firstSeenMinutesAgo: 1);
        DispatchTask seenEarlier = Task("D-2", TransportTaskTypes.WireToGate, createdMinutesAgo: 20, firstSeenMinutesAgo: 5);

        Assert.Equal(["D-2", "D-1"], Order(seenLater, seenEarlier));
    }

    /// <summary>
    /// MES 的 <c>DATES</c>（<see cref="LiveMesFieldSet.MesSourceDate"/>）不进排序：把它改成与年龄相反的次序，
    /// 次序不变（REQ-0201）。
    /// </summary>
    [Fact]
    public void ChangingTheMesDatesDoesNotChangeTheOrder()
    {
        DispatchTask older = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 30, mesSourceMinutesAgo: 1);
        DispatchTask younger = Task("D-2", TransportTaskTypes.WireToGate, createdMinutesAgo: 10, mesSourceMinutesAgo: 600);
        string[] before = Order(older, younger);

        DispatchTask olderWithOtherDates = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 30, mesSourceMinutesAgo: 900);

        Assert.Equal(["D-1", "D-2"], before);
        Assert.Equal(before, Order(olderWithOtherDates, younger));
    }

    /// <summary>年龄 = 此刻减去本地建单时刻；MesIngest 的钟快过本服务端时算作零岁，不为负。</summary>
    [Fact]
    public void TheWaitingAgeIsNowLessTheLocalCreationAndNeverNegative()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(25),
            TaskStarvation.WaitingAge(Snapshot("D-1", TransportTaskTypes.WireToGate, Now.AddMinutes(-25)), Now));
        Assert.Equal(
            TimeSpan.Zero,
            TaskStarvation.WaitingAge(Snapshot("D-2", TransportTaskTypes.WireToGate, Now.AddSeconds(5)), Now));
    }

    // ---- 超时层 --------------------------------------------------------------------------------------

    /// <summary>越过本区阈值的普通任务排在未超时的 <c>STAGING_TO_WIRE</c> 之前（REQ-0202）。</summary>
    [Fact]
    public void AnOverdueNormalTaskIsTakenBeforeAStagingToWireTaskThatIsNotOverdue()
    {
        DispatchZoneParameterTableVersion parameters = Parameters(7, (Zone, 600));
        DispatchTask overdue = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 11, parameters: parameters);
        DispatchTask staging = Task("D-2", TransportTaskTypes.StagingToWire, createdMinutesAgo: 9, parameters: parameters);

        Assert.True(overdue.Starvation!.Overdue);
        Assert.False(staging.Starvation!.Overdue);
        Assert.Equal(["D-1", "D-2"], Order(staging, overdue));
    }

    /// <summary>
    /// 超时任务之间仍按年龄排，不按超出阈值多少：两个分区阈值不同时，超出得多的那条未必是等得久的那条。
    /// </summary>
    [Fact]
    public void OverdueTasksAreOrderedAmongThemselvesByWaitingAge()
    {
        DispatchZoneParameterTableVersion parameters = Parameters(7, (Zone, 3000), (OtherZone, 60));
        // D-1 等了 60 分钟、超出 10 分钟；D-2 等了 30 分钟、超出 29 分钟。按年龄 D-1 先。
        DispatchTask olderByLittle = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 60, parameters: parameters);
        DispatchTask youngerByMuch = Task(
            "D-2", TransportTaskTypes.WireToOptical, createdMinutesAgo: 30, parameters: parameters, area: OtherZoneArea);

        Assert.True(olderByLittle.Starvation!.Overdue);
        Assert.True(youngerByMuch.Starvation!.Overdue);
        Assert.Equal(["D-1", "D-2"], Order(youngerByMuch, olderByLittle));
    }

    /// <summary>达到阈值即超时（「达到防饥饿阈值后」），差一秒不超时。</summary>
    [Fact]
    public void ATaskIsOverdueFromTheMomentItsAgeReachesTheThreshold()
    {
        DispatchZoneParameterTableVersion parameters = Parameters(3, (Zone, 600));

        Assert.True(Standing(Now.AddSeconds(-600), parameters).Overdue);
        Assert.False(Standing(Now.AddSeconds(-599), parameters).Overdue);
    }

    /// <summary>
    /// 阈值未配置（未批准）时不升级、只计龄（REQ-0203 降级路径，也是现场上线时实际走的那条）：
    /// 一版参数都没有、有版本但本区不在表里、本区在表里但阈值留空，三种都不进超时层，
    /// 于是等了一整天的普通任务仍排在刚建的 <c>STAGING_TO_WIRE</c> 之后——但它的年龄照样算出来了。
    /// </summary>
    [Theory]
    [InlineData("no-version")]
    [InlineData("zone-absent")]
    [InlineData("threshold-empty")]
    public void WithNoApprovedThresholdNothingEscalatesButTheAgeIsStillCounted(string shape)
    {
        DispatchZoneParameterTableVersion? parameters = shape switch
        {
            "no-version" => null,
            "zone-absent" => Parameters(4, (OtherZone, 60)),
            _ => new DispatchZoneParameterTableVersion(
                4, "sha", null, Now, "test",
                new Dictionary<string, DispatchZoneParameters>(StringComparer.Ordinal)
                {
                    [Zone] = new(Zone, 500, null),
                }),
        };
        DispatchTask dayOld = Task("D-1", TransportTaskTypes.WireToGate, createdMinutesAgo: 24 * 60, parameters: parameters);
        DispatchTask staging = Task("D-2", TransportTaskTypes.StagingToWire, createdMinutesAgo: 1, parameters: parameters);

        Assert.False(dayOld.Starvation!.Overdue);
        Assert.Null(dayOld.Starvation.ThresholdSeconds);
        Assert.Equal(TimeSpan.FromDays(1), dayOld.Starvation.WaitingAge);
        Assert.Equal(["D-2", "D-1"], Order(dayOld, staging));
    }

    /// <summary><c>STAGING_TO_WIRE</c> 本就在最高带，不会进超时层：超时层只收普通带任务。</summary>
    [Fact]
    public void AStagingToWireTaskNeverEntersTheTimeoutTier()
    {
        TaskStarvationStanding standing = TaskStarvation.Assess(
            Snapshot("D-1", TransportTaskTypes.StagingToWire, Now.AddHours(-5)), Now, Areas(), Parameters(2, (Zone, 60)));

        Assert.False(standing.Overdue);
    }

    /// <summary>
    /// 分区取本轮分区归属表给这个 AREA 的那一个，阈值取本轮参数版本里这个分区的那一个，记下所用的参数版本。
    /// 表里没有的 AREA（例如共晶类，REQ-0185）没有分区，也就没有阈值、不会超时。
    /// </summary>
    [Fact]
    public void TheStandingRecordsTheZoneTheThresholdAndTheParameterVersionItWasJudgedUnder()
    {
        DispatchZoneParameterTableVersion parameters = Parameters(9, (Zone, 600), (OtherZone, 60));

        TaskStarvationStanding standing = Standing(Now.AddMinutes(-5), parameters, OtherZoneArea);
        TaskStarvationStanding unmapped = Standing(Now.AddHours(-5), parameters, "T5-1");

        Assert.Equal(OtherZone, standing.DispatchZone);
        Assert.Equal(60, standing.ThresholdSeconds);
        Assert.Equal(9, standing.ParameterVersion);
        Assert.True(standing.Overdue);
        Assert.Null(unmapped.DispatchZone);
        Assert.False(unmapped.Overdue);
    }

    /// <summary>注册表：超时层、优先级带、建单时刻（年龄）、首次看到、需求 id。</summary>
    [Fact]
    public void TheTaskSideRegistersTheTimeoutTierAndTheBandAheadOfTheAge() =>
        Assert.Equal(
            [
                typeof(StarvationTimeoutLayer), typeof(PriorityBandLayer), typeof(DemandCreatedAtLayer),
                typeof(FirstSeenLayer), typeof(DemandIdOrdinalLayer),
            ],
            DispatchCandidateOrdering.Layers().Select(layer => layer.GetType()).ToArray());

    // ---- helpers -------------------------------------------------------------------------------------

    private static string[] Order(params DispatchTask[] tasks)
    {
        ServiceCollection services = new();
        services.AddDispatchAdmission();
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        return [.. scope.ServiceProvider.GetRequiredService<IDispatchCandidateRanker>()
            .Order(tasks)
            .Select(task => task.Snapshot.DemandId)];
    }

    private static TaskStarvationStanding Standing(
        DateTimeOffset createdAt, DispatchZoneParameterTableVersion? parameters, string area = Area) =>
        TaskStarvation.Assess(
            Snapshot("D-x", TransportTaskTypes.WireToGate, createdAt, area: area), Now, Areas(), parameters);

    private static DispatchTask Task(
        string demandId,
        string taskType,
        int createdMinutesAgo,
        int firstSeenMinutesAgo = 0,
        int mesSourceMinutesAgo = 0,
        DispatchZoneParameterTableVersion? parameters = null,
        string area = Area)
    {
        AcceptedDemandSnapshot snapshot = Snapshot(
            demandId, taskType, Now.AddMinutes(-createdMinutesAgo), Now.AddMinutes(-mesSourceMinutesAgo), area);
        return new DispatchTask(snapshot, Now.AddMinutes(-firstSeenMinutesAgo))
        {
            Starvation = TaskStarvation.Assess(snapshot, Now, Areas(), parameters),
        };
    }

    private static AcceptedDemandSnapshot Snapshot(
        string demandId,
        string taskType,
        DateTimeOffset createdAt,
        DateTimeOffset? mesSourceDate = null,
        string area = Area) => new(
        demandId,
        $"SUBLOT-{demandId}|{taskType}",
        1,
        "11111111-1111-4111-8111-111111111111",
        21,
        Now,
        $"SERIES-{demandId}",
        taskType,
        $"SUBLOT-{demandId}",
        1,
        createdAt,
        createdAt,
        $"TRACE-{demandId}",
        $"COMMIT-{demandId}",
        new LiveMesFieldSet(area, "EQP-01", "STEP-01", mesSourceDate ?? createdAt, "PKG"));

    private static AreaAssignmentTableVersion Areas() => new(
        1,
        "sha",
        "snapshot-1",
        Now,
        new Dictionary<string, AreaAssignment>(StringComparer.Ordinal)
        {
            [Area] = new(Area, Zone, "FRONT"),
            [OtherZoneArea] = new(OtherZoneArea, OtherZone, "REAR"),
        });

    private static DispatchZoneParameterTableVersion Parameters(long version, params (string Zone, long Seconds)[] zones) => new(
        version,
        "sha",
        null,
        Now,
        "test",
        zones.ToDictionary(
            zone => zone.Zone,
            zone => new DispatchZoneParameters(zone.Zone, null, zone.Seconds),
            StringComparer.Ordinal));
}
