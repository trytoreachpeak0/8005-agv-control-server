using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图「等人中的旅程」的数据面（control-server#273）：每趟车停着等人的旅程一行——车、阶段、原因码、开始等人的时刻与已等多久、
/// 监看最后一次记下的电量与读数时间、按两道线判出的电量等级、过没过告警门槛。
/// </summary>
/// <remarks>
/// <para>
/// <b>哪些旅程有一行，由 <see cref="JourneyWaitClassification.IsWaiting"/> 一处定</b>，与写日志的监看是同一个判法：停着的阶段一律列，
/// 路上的只在引擎说出了没到的原因时列，已完成的不列。这里不另写一份阶段清单。
/// </para>
/// <para>
/// <b>只读落库的事实。</b>已等多久 = 请求时刻 − <see cref="JourneyRuntimeRow.WaitingSince"/>（保存时由上下文按同一个判法维护：
/// 在等人阶段之间切换不重置，出发或完成才清掉；路上的从原因出现算），不从 <c>UpdatedAt</c> 算——后者每写一次行就动。电量是 <see cref="WaitingJourneyWatch"/> 落库的那一次读数，连同读数时间原样给出；看板从不自己去问 RIoT。
/// 没读过、或读的那一刻 RIoT 没给百分比，百分比都是 null、等级是 <c>Unknown</c>，这一行照列。
/// </para>
/// <para>
/// 救命线与门槛取宿主上与引擎同一份 <see cref="JourneyRuntimeOptions"/>，随数据一并下发，卡片只把它们写成字。另一道线是这辆车当前充电策略的
/// <c>MandatoryChargeEntryThreshold</c>（批次9-05，control-server#403；此前是 <c>MinimumBatteryPercent</c>），逐行给出，没有已批准策略时为 null、
/// 等级为 <c>Unknown</c>。
/// </para>
/// <para>
/// <b>停在待送点取货停靠上的旅程多一项 <c>stagingStop</c></b>（control-server#709）：那里没有倒计时，车等的是客户点出发，看板要让人看出它在等什么、
/// 车上已经有多少、还能装多少——已装篮数（已装上的需求的花篮数之和）、各侧还空着的物理仓位（与派车、判满同一个账本
/// <see cref="JourneyAwareSlotLedger"/>；车载端事实或仓位模型读不到时为 null）、这趟旅程还待装几条。其余旅程、以及待送点规则关着时，
/// 这一行与之前逐字相同，不多这个字段。
/// </para>
/// </remarks>
internal sealed class WaitingJourneysQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly JourneyRuntimeOptions _options;
    private readonly TimeProvider _clock;
    private readonly IServiceScopeFactory? _scopes;

    /// <summary>
    /// 只供不带宿主的发现（列目录的测试）：门槛与救命线取 <see cref="JourneyRuntimeOptions"/> 的默认值，读不到充电策略（每行的强制充电线为 null）。
    /// </summary>
    public WaitingJourneysQueryEndpoint()
        : this(new JourneyRuntimeOptions(), TimeProvider.System, null)
    {
    }

    /// <summary>挂在宿主上时用这一个：与引擎的监看用的是同一份配置（<see cref="DashboardQueryEndpointCatalog.Discover"/>）。</summary>
    [ActivatorUtilitiesConstructor]
    public WaitingJourneysQueryEndpoint(IOptions<JourneyRuntimeOptions> options, IServiceScopeFactory scopes)
        : this((options ?? throw new ArgumentNullException(nameof(options))).Value, TimeProvider.System, scopes)
    {
    }

    /// <param name="scopes">
    /// 每次请求开一个作用域取 <see cref="IChargingPolicyResolver"/>（它是作用域的，端点是宿主根上造的）；为空时每行的强制充电线为 null。
    /// </param>
    internal WaitingJourneysQueryEndpoint(JourneyRuntimeOptions options, TimeProvider clock, IServiceScopeFactory? scopes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
        _scopes = scopes;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "waiting-journeys";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = _clock.GetUtcNow();
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken);

        await using AsyncServiceScope? scope = _scopes?.CreateAsyncScope();
        IChargingPolicyResolver? resolver = scope?.ServiceProvider.GetRequiredService<IChargingPolicyResolver>();
        List<object> facts = [];
        foreach (JourneyRuntimeRow row in journeys
                     .Where(row => JourneyWaitClassification.IsWaiting(row.Stage, row.BlockReasonCode))
                     .OrderBy(row => row.AgvId, StringComparer.Ordinal)
                     .ThenBy(row => row.JourneyId, StringComparer.Ordinal))
        {
            int? entry = resolver is null
                ? null
                : await WaitingJourneyBattery.MandatoryChargeEntryPercentAsync(resolver, row.VehicleKey, cancellationToken);
            facts.Add(await StagingStopAsync(dbContext, row, cancellationToken) is { } staging
                ? WithStagingStop(Fact(row, entry, now), staging)
                : Fact(row, entry, now));
        }

        return new
        {
            warningAfterSeconds = (long)_options.WaitingJourneyWarningAfter.TotalSeconds,
            rescueBatteryPercent = _options.WaitingJourneyRescueBatteryPercent,
            journeys = facts.ToArray()
        };
    }

    /// <summary>
    /// 这趟旅程此刻停在待送点取货停靠上时，卡片要的那几样；规则关着、不在待送点、不在待送点上那三个阶段时为空。
    /// </summary>
    private async Task<StagingStopFacts?> StagingStopAsync(
        ControlServerDbContext dbContext,
        JourneyRuntimeRow journey,
        CancellationToken cancellationToken)
    {
        if (!_options.StagingDepartureRules.Enabled ||
            journey.Stage is not (JourneyRuntimeStage.AwaitingSublot or
                JourneyRuntimeStage.AwaitingLoadResult or
                JourneyRuntimeStage.AwaitingStationDeparture) ||
            !await StagingPickupStops.CurrentStopIsStagingAsync(
                dbContext, journey, _options.StagingDepartureRules, cancellationToken))
        {
            return null;
        }

        JourneyStopCursor stops = await JourneyStopCursor.LoadAsync(dbContext, journey, cancellationToken);
        int loadedBaskets = stops.Demands
            .Where(item => item.Membership.Status == JourneyDemandStatuses.Loaded)
            .Sum(item => item.Membership.ExpectedBasketCount);
        int pendingDemands = stops.Demands.Count(item =>
            item.Membership.Status is JourneyDemandStatuses.PendingLoad or JourneyDemandStatuses.Loading);

        Dictionary<string, int>? freeSlots = null;
        VehicleSlotPositions? positions = await new VehicleSlotPositionReader(dbContext)
            .ReadAsync(journey.AgvId, cancellationToken);
        OnboardDispatchFacts? onboard = await new OnboardDispatchFactsReader(dbContext, Options.Create(_options), _clock)
            .ReadOnboardFactsAsync(journey.AgvId, cancellationToken);
        if (positions is not null && onboard is not null && positions.PhysicalSlotCountByGroup.Count > 0)
        {
            JourneyAwareSlotLedger ledger = new(dbContext);
            freeSlots = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string group in positions.PhysicalSlotCountByGroup.Keys.Order(StringComparer.Ordinal))
            {
                freeSlots[group] = (await ledger.ReadAvailableSlotsAsync(
                    journey.AgvId, onboard, positions, group, cancellationToken)).Count;
            }
        }

        return new StagingStopFacts(loadedBaskets, freeSlots, pendingDemands);
    }

    private sealed record StagingStopFacts(int LoadedBaskets, Dictionary<string, int>? FreeSlotsByGroup, int PendingDemands);

    /// <summary>与 <see cref="Fact"/> 同一行，多一项 <c>stagingStop</c>。另写一个形状，是为了不在待送点的行与之前逐字相同。</summary>
    private static System.Text.Json.Nodes.JsonObject WithStagingStop(object fact, StagingStopFacts staging)
    {
        System.Text.Json.Nodes.JsonObject row = System.Text.Json.JsonSerializer.SerializeToNode(fact)!.AsObject();
        row["stagingStop"] = System.Text.Json.JsonSerializer.SerializeToNode(new
        {
            loadedBaskets = staging.LoadedBaskets,
            freeSlotsByGroup = staging.FreeSlotsByGroup,
            pendingDemands = staging.PendingDemands
        });
        return row;
    }

    private object Fact(JourneyRuntimeRow journey, int? mandatoryChargeEntryPercent, DateTimeOffset now)
    {
        DateTimeOffset? since = journey.WaitingSince;
        long? waitedSeconds = since is { } start ? (long)Math.Max(0, (now - start).TotalSeconds) : null;
        return new
        {
            journeyId = journey.JourneyId,
            agvId = journey.AgvId,
            stage = journey.Stage.ToString(),
            blockReasonCode = journey.BlockReasonCode,
            waitingSince = since,
            waitedSeconds,
            pastWarningThreshold = waitedSeconds is { } seconds &&
                seconds >= (long)_options.WaitingJourneyWarningAfter.TotalSeconds,
            batteryPercent = journey.WaitingBatteryPercent,
            batteryObservedAt = journey.WaitingBatteryObservedAt,
            mandatoryChargeEntryPercent,
            batteryLevel = WaitingJourneyBattery.Level(journey.WaitingBatteryPercent, mandatoryChargeEntryPercent, _options).ToString()
        };
    }
}
