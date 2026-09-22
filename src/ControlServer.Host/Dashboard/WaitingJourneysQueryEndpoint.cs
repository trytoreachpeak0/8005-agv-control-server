using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「等人中的旅程」的数据面（control-server#273）：每趟车停着等人的旅程一行——车、阶段、原因码、阶段起点与已等多久、
/// 监看最后一次记下的电量与读数时间、按两道线判出的电量等级、过没过告警门槛。
/// </summary>
/// <remarks>
/// <para>
/// <b>哪些旅程有一行，由 <see cref="JourneyWaitClassification.IsWaiting"/> 一处定</b>，与写日志的监看是同一个判法：停着的阶段一律列，
/// 路上的只在引擎说出了没到的原因时列，已完成的不列。这里不另写一份阶段清单。
/// </para>
/// <para>
/// <b>只读落库的事实。</b>已等多久 = 请求时刻 − <see cref="JourneyRuntimeRow.StageSince"/>（保存换阶段时由上下文盖章），不从
/// <c>UpdatedAt</c> 算——后者每写一次行就动。电量是 <see cref="WaitingJourneyWatch"/> 落库的那一次读数，连同读数时间原样给出；看板从不自己去问 RIoT。
/// 没读过、或读的那一刻 RIoT 没给百分比，百分比都是 null、等级是 <c>Unknown</c>，这一行照列。
/// </para>
/// <para>
/// 两道线与门槛取宿主上与引擎同一份 <see cref="JourneyRuntimeOptions"/>，随数据一并下发，卡片只把它们写成字。
/// </para>
/// </remarks>
internal sealed class WaitingJourneysQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly JourneyRuntimeOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>只供不带宿主的发现（列目录的测试）：门槛与两道线取 <see cref="JourneyRuntimeOptions"/> 的默认值。</summary>
    public WaitingJourneysQueryEndpoint()
        : this(new JourneyRuntimeOptions(), TimeProvider.System)
    {
    }

    /// <summary>挂在宿主上时用这一个：与引擎的监看用的是同一份配置（<see cref="DashboardQueryEndpointCatalog.Discover"/>）。</summary>
    [ActivatorUtilitiesConstructor]
    public WaitingJourneysQueryEndpoint(IOptions<JourneyRuntimeOptions> options)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value, TimeProvider.System)
    {
    }

    internal WaitingJourneysQueryEndpoint(JourneyRuntimeOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "waiting-journeys";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = _clock.GetUtcNow();
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken);

        return new
        {
            warningAfterSeconds = (long)_options.WaitingJourneyWarningAfter.TotalSeconds,
            minimumBatteryPercent = _options.MinimumBatteryPercent,
            rescueBatteryPercent = _options.WaitingJourneyRescueBatteryPercent,
            journeys = journeys
                .Where(JourneyWaitClassification.IsWaiting)
                .OrderBy(row => row.AgvId, StringComparer.Ordinal)
                .ThenBy(row => row.JourneyId, StringComparer.Ordinal)
                .Select(row => Fact(row, now))
                .ToArray()
        };
    }

    private object Fact(JourneyRuntimeRow journey, DateTimeOffset now)
    {
        DateTimeOffset? since = journey.StageSince;
        long? waitedSeconds = since is { } start ? (long)Math.Max(0, (now - start).TotalSeconds) : null;
        return new
        {
            journeyId = journey.JourneyId,
            agvId = journey.AgvId,
            stage = journey.Stage.ToString(),
            blockReasonCode = journey.BlockReasonCode,
            stageSince = since,
            waitedSeconds,
            pastWarningThreshold = waitedSeconds is { } seconds &&
                seconds >= (long)_options.WaitingJourneyWarningAfter.TotalSeconds,
            batteryPercent = journey.WaitingBatteryPercent,
            batteryObservedAt = journey.WaitingBatteryObservedAt,
            batteryLevel = JourneyWaitClassification.BatteryLevel(journey.WaitingBatteryPercent, _options).ToString()
        };
    }
}
