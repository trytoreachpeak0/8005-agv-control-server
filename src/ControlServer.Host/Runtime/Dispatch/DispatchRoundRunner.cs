using System.Security.Cryptography;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// One dispatch round: reads the catalog, judges every candidate for every free vehicle in series, and takes on at
/// most one demand per vehicle (control-server#209 moved it here, unchanged, out of <see cref="JourneyRuntimeEngine"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>It shares the engine's scope.</b> Registered scoped, it is handed the same <see cref="ControlServerDbContext"/>
/// the engine and every store of the round are, and that is load-bearing: a vehicle whose segment ends early clears
/// the change tracker here, and that has to be the tracker every later save of the round goes through.
/// </para>
/// <para>
/// <b>One vehicle's trouble is its own</b> (control-server#231): a segment that runs out its budget or throws is
/// dropped, and the round carries on with the vehicles behind it and still reports at its end.
/// </para>
/// <para>
/// It logs under <see cref="JourneyRuntimeEngine"/>'s category, with the event ids it had there (2101, 2104, 2106)
/// plus three of control-server#231's own — 2123 a segment that threw, 2124 a segment that broke one of this
/// server's invariants, 2125 an in-transit vehicle that could not be asked — so a log filter or an alert written
/// against the engine still sees the round.
/// </para>
/// </remarks>
public sealed class DispatchRoundRunner(
    ControlServerDbContext dbContext,
    IMesIngestCatalog catalog,
    IRiotVehicleFacts vehicleFacts,
    JourneyIntakeCoordinator intakeCoordinator,
    DispatchAdmissionChain admissionChain,
    InTransitDispatchAdmissionChain inTransitChain,
    IDispatchZoneParameterStore zoneParameterStore,
    IDispatchCandidateRanker candidateRanker,
    VehicleDispatchPolicyAccess dispatchPolicy,
    IAreaAssignmentStore areaAssignments,
    IVehicleSlotPositionReader slotPositions,
    IDispatchRoundOutcomeSink roundOutcomes,
    OnboardDispatchFactsReader onboardFacts,
    IOptions<JourneyRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<JourneyRuntimeEngine> logger)
{
    // The backlog's decision fingerprint is a hash over this serialisation, so it is the engine's setting exactly: a
    // different one would read every backlog row written before the move as a changed demand.
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly Action<ILogger, Exception?> LogCatalogPollFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(2101, nameof(LogCatalogPollFailed)),
        "MesIngest catalog polling failed closed; no journey was accepted.");
    private static readonly Action<ILogger, string, int, Exception?> LogVehicleRoundBudgetExhausted =
        LoggerMessage.Define<string, int>(
            LogLevel.Warning,
            new EventId(2104, nameof(LogVehicleRoundBudgetExhausted)),
            "Vehicle {AgvId} exhausted its {BudgetMilliseconds} ms dispatch budget; the round moved on " +
            "to the remaining vehicles.");
    private static readonly Action<ILogger, string, string, Exception?> LogVehicleOccupancyConflict =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2106, nameof(LogVehicleOccupancyConflict)),
            "Vehicle {AgvId} already holds an in-flight order; the claim for {UpperId} was refused by " +
            "the occupancy index.");
    private static readonly Action<ILogger, string, string, Exception?> LogVehicleRoundFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2123, nameof(LogVehicleRoundFailed)),
            "Vehicle {AgvId} could not be served this round: {ExceptionType}. The round moved on to the " +
            "remaining vehicles.");
    private static readonly Action<ILogger, string, string, Exception?> LogVehicleRoundFaulted =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(2124, nameof(LogVehicleRoundFaulted)),
            "Vehicle {AgvId} broke one of this server's own invariants and was skipped: {ExceptionType}. " +
            "The round moved on, but this is a defect rather than an unreachable peer -- it will not fix " +
            "itself next round.");
    /// <summary>
    /// 一辆<b>正被人等着处理</b>的车，却在这一轮里被判了一条候选——按设计它根本不该走到这里
    /// （批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两道排除本该让它进不来：<c>JourneyRuntimeEngine</c> 不把 Blocked 的车放进 <c>underWay</c>，
    /// <c>ReadEnRoutePlanAsync</c> 对 Blocked 的旅程读不出计划、于是 <c>TryAdmitToRoundAsync</c> 把它剔出这一轮。
    /// 真机上仍然出现过一条 <c>ONBOARD_FACTS_NOT_READY</c> 的积压记录，写在旅程 <c>BlockReasonSince</c> 之后
    /// 9 秒（<c>L2-LR-10</c>，run 35511760908），而本机跑不出来。这条日志是为了下一次真机撞上时，
    /// 现场能直接说出它是从哪条路进来的。
    /// </para>
    /// <para>
    /// <b>它判 Blocked 用的是一次独立的、当场发出的查询，不读轮次手上那份数据。</b>这一点是这条诊断能不能
    /// 成立的全部：如果问题恰恰是「轮次手上那份数据里它不是 Blocked」，那么从那份数据去问「它是不是 Blocked」
    /// 必然得到「不是」——探测器和被探测的东西共享同一个盲区，问题正在发生而诊断安静，而一次都不响会被
    /// 当成「没有问题」。
    /// </para>
    /// <para>
    /// <b>验过它会叫，没有只靠读代码。</b>把两处 Blocked 排除一起去掉，跑
    /// <c>ABlockedJourneyKeepsItsVehicleOutOfTheRoundEntirely</c>，这条日志命中 3 次（三辆车各一次），
    /// 而那一轮产生的事件 id 只有这一个——目标那一行确实被执行到了，不是「没跑到所以没叫」。
    /// 一个没验过会叫的探测器，和一个不存在的探测器，在报告里长得一模一样。
    /// </para>
    /// </remarks>
    private static readonly Action<ILogger, string, string, string, Exception?> LogBlockedVehicleJudgedACandidate =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2131, nameof(LogBlockedVehicleJudgedACandidate)),
            "Vehicle {AgvId} was judged for demand {DemandId} and refused with {Reason}, but a fresh read says " +
            "its journey is Blocked -- a blocked vehicle should never have entered this round at all. " +
            "Either it was not blocked when the round admitted it, or one of the two exclusions did not hold.");

    private static readonly Action<ILogger, string, string, Exception?> LogInTransitQualificationFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2125, nameof(LogInTransitQualificationFailed)),
            "Asking whether vehicle {AgvId} may take an appended demand failed: {ExceptionType}. The round " +
            "left it under way and went on to its end.");

    private readonly JourneyRuntimeOptions runtimeOptions = options.Value;

    /// <summary>
    /// Whether this is one of this server's own invariants being broken rather than a peer being unreachable.
    /// </summary>
    /// <remarks>
    /// Both of these mean a defect here -- a demand bound to content that does not match it, a plan frozen with
    /// two of its three versions (REQ-0305). Isolating the vehicle keeps the fleet moving, which is what
    /// control-server#231 is for, but it must not also make the defect look like weather: these are logged at
    /// Error under their own event id, because a peer that cannot be reached is fixed by the next round and this
    /// is not.
    /// </remarks>
    private static bool IsInvariantBreach(Exception error) =>
        error is BusinessIdentityConflictException or JourneyPlanFreezeIncompleteException;

    /// <summary>
    /// 一轮派车：<b>先定任务，再只为那条任务比车</b>（REQ-0200；批次7-06，control-server#211）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>轮次翻过来了。</b>在这之前是逐车循环，每辆车在自己的候选里挑最便宜的一条；空闲车只有一台时两者等价，
    /// 车队下不等价——先挑车再从它附近的任务里反向选单，正是 REQ-0200 明文禁止的。现在是：候选按任务侧的次序排一遍，
    /// 逐条问「哪些车接得了」，再用车辆侧的排序层为它选一辆。
    /// </para>
    /// <para>
    /// <b>每辆车的事实读一次，预算还是它自己的。</b>翻转之后一辆车的评估散在多条任务里，所以预算跟着车走而不是跟着
    /// 一段循环走：耗尽预算或读挂了的车退出这一轮，后面每条任务都不再问它，别的车照常。这正是 control-server#231 要的隔离，
    /// 只是换了个挂法。
    /// </para>
    /// <para>
    /// <b>在途车与空闲车在同一张表上竞争</b>（REQ-0205），各走各的资格链：空闲车走 <see cref="DispatchAdmissionChain"/>，
    /// 在途车走 <see cref="InTransitDispatchAdmissionChain"/>。身份本身不产生优先级——谁接得了、谁的边际成本低，就是谁。
    /// </para>
    /// </remarks>
    /// <param name="currentMap">The Map this round was read against.</param>
    /// <param name="fixedStations">This round's fixed stations.</param>
    /// <param name="vehicles">The idle vehicles, in roster order.</param>
    /// <param name="vehiclesUnderWay">The vehicles already carrying a journey.</param>
    /// <param name="admissionPolicyDrifted">Whether this round's admission policy refused to rebind.</param>
    /// <param name="cancellationToken">Ends the round on shutdown.</param>
    public async Task RunAsync(
        RiotMapStationCatalogSnapshot currentMap,
        IFixedTaskStationView fixedStations,
        IReadOnlyList<FleetVehicle> vehicles,
        IReadOnlyList<FleetVehicle> vehiclesUnderWay,
        bool admissionPolicyDrifted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicles);
        ArgumentNullException.ThrowIfNull(vehiclesUnderWay);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DemandCatalogSnapshot snapshot;
        try
        {
            snapshot = await catalog.ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogCatalogPollFailed(logger, error);
            return;
        }

        Dictionary<string, JourneyBacklogRow> backlogByDemandId = await dbContext.JourneyBacklog
            .ToDictionaryAsync(row => row.DemandId, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        await MarkBacklogLeftCatalogAsync(backlogByDemandId, snapshot, cancellationToken).ConfigureAwait(false);
        // The MesIngest catalog is MES's own list of open transport demands, and a journey of ours
        // reaching Completed does not take the demand out of it. A demand this server has accepted is
        // bound to its one journey permanently; it is never a candidate again, whatever stage that
        // journey reached. Kept as a live set rather than a snapshot: a demand taken earlier in this
        // same round has to stop being a candidate for the rest of it. See DispatchRoundFacts.
        HashSet<string> acceptedDemandIds = (await dbContext.AcceptedDemands
                .Select(row => row.DemandId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        VehicleDispatchPolicy policy = await dispatchPolicy.EnsureCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        // Read once with the policy and for the same reason: every candidate in the round is judged against
        // one version of the table, and that is the version a demand freezes.
        AreaAssignmentTableVersion? areaAssignmentTable = await areaAssignments
            .ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        // 每区派车参数同理读一次（批次7-06）：一轮里每次追加决策都按同一版判，记下的也是这个版本号。
        DispatchZoneParameterTableVersion? zoneParameters = await zoneParameterStore
            .ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> claimsIntakeRefused = new(StringComparer.Ordinal);
        DispatchRoundFacts round = new(
            snapshot, currentMap, fixedStations, acceptedDemandIds, now, policy, areaAssignmentTable,
            admissionPolicyDrifted)
        {
            ClaimsIntakeRefused = claimsIntakeRefused,
            ZoneParameters = zoneParameters,
        };

        // 「上次成功接单」从既有旅程记录推出（票面第 6 条带内层），一轮查一次。
        Dictionary<string, DateTimeOffset> lastDispatchedAt = LastDispatchAtByVehicle(
            await dbContext.JourneyRuntimes.AsNoTracking()
                .Select(row => new JourneyStart(row.AgvId, row.CreatedAt))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false));

        List<RoundVehicle> participants = [];
        foreach ((FleetVehicle vehicle, bool underWay) in
                 vehicles.Select(v => (v, false)).Concat(vehiclesUnderWay.Select(v => (v, true))))
        {
            RoundVehicle? participant = await TryAdmitToRoundAsync(
                vehicle, underWay, policy, lastDispatchedAt, backlogByDemandId, cancellationToken)
                .ConfigureAwait(false);
            if (participant is not null)
            {
                participants.Add(participant);
            }
        }

        // 任务层的次序：本票沿用今天的（先见先派，再按创建时刻与需求 id 定序）。优先级带、超时层与等待年龄由
        // 批次7-09（control-server#214）加在这一层上，与车辆侧不相交。
        IReadOnlyList<DispatchTask> tasksInOrder = candidateRanker.Order(
            [.. round.Catalog.Items.Select(item => new DispatchTask(
                item,
                backlogByDemandId.TryGetValue(item.DemandId, out JourneyBacklogRow? row) ? row.FirstSeenAt : now))]);
        foreach (DispatchTask task in tasksInOrder)
        {
            AcceptedDemandSnapshot candidate = task.Snapshot;
            List<CandidateOffer> offers = [];
            // 问的是这一轮还活着的每一辆车，接没接过单都问（批次7-06，control-server#211）。翻转之前逐车循环，
            // 每辆车把全部候选判一遍再挑一条接走，所以轮次结局里一辆车对每条候选都有结论；只问还能接活的车，
            // 那份结论就会缺口——而「全车队都接不了这条需求」正是靠它算出来的，缺一辆就可能漏报。
            // 接过单的车照判不误，只是不进出价：它这一轮不会再被选中。
            foreach (RoundVehicle participant in participants.Where(p => p.RanToItsEnd))
            {
                await JudgeForVehicleAsync(
                        round, participant, candidate, offers, backlogByDemandId, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (offers.Count == 0)
            {
                continue;
            }

            // 到这里才保存，而且一条任务至多两次（批次7-06，control-server#211）。判断本身只改内存里的积压行，
            // 攒着一起写；一条没人出价的任务因此一次保存都不产生。<b>这一条是有人盯着的</b>：
            // LargeCatalogBatchesBacklogPersistenceBeforeAcceptingEligibleJourney 拿 251 条候选跑一轮，
            // 保存次数超过十几次就红。一候选一次保存，在真实目录规模上就是几百次事务。
            //
            // 这一次保存不能省：受理那一步按 demandId 从库里读回积压行去改它的理由，行还没落库就读不到。
            UpsertBacklog(backlogByDemandId, candidate, DispatchAdmissionChain.Eligible, now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // 垮掉的车把这条任务让给下一个出价者（批次7-06，control-server#211）。翻转之前这是自动成立的：
            // 一辆车的段结束后它的认领被撤回，排在后面的车在自己的段里重新判到这条需求就接走了。翻转之后
            // 一条任务只处理一次，所以「让出去」要在这里写出来——不写的话，一辆车在受理里垮掉就等于把这条
            // 需求扣到下一轮，而它本来这一轮就有车能接。
            //
            // 只有<b>垮掉</b>才让：受理把它拒掉（最终重读发现候选变了、没了）是这条需求自己的结论，换一辆车
            // 再试一次只会得到同一个答案，还白占一辆车这一轮的名额。
            List<CandidateOffer> stillBidding = [.. offers];
            CandidateOffer? winner = null;
            bool winnerWroteItsOwnReason = false;
            while (stillBidding.Count > 0)
            {
                EligibleVehicleOffer selected =
                    DispatchVehicleOrdering.SelectNext([.. stillBidding.Select(offer => offer.Offer)]);
                CandidateOffer bidder = stillBidding.Single(offer => ReferenceEquals(offer.Offer, selected));
                // 选中就算接了：无论受理成功还是被最后一刻的重读拒掉，这辆车这一轮都不再参与后面的任务。
                // 拒掉之后还让它去抢下一条，等于用一次失败的尝试换一条别的车本可以接走的任务。
                bidder.Participant.TookADemand();
                CandidateDispatchOutcome outcome = await DispatchSelectedAsync(
                        round, bidder, acceptedDemandIds, claimsIntakeRefused, backlogByDemandId, now,
                        cancellationToken).ConfigureAwait(false);
                if (outcome != CandidateDispatchOutcome.VehicleCannotTake)
                {
                    winner = bidder;
                    winnerWroteItsOwnReason = outcome == CandidateDispatchOutcome.RefusedWithItsOwnReason;
                    break;
                }

                bidder.Participant.WithdrawFromRound();
                stillBidding.Remove(bidder);
            }

            // 裁决落在派车之后：积压行上留下的理由因此仍是落选者写的那一个，与翻转之前由排在最后的车写下的
            // 结果一致。受理过程中自己写的理由（例如最终重读发现候选没了）先落，再被它盖掉。
            RecordVerdictsForCandidate(
                offers, winner, winnerWroteItsOwnReason, backlogByDemandId, candidate, now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // 收尾：没人出价的那些任务，判断还攒在内存里，一起落库。
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // After every vehicle, a budget-exhausted one included. Whether any vehicle at all could take a demand
        // is only answerable across the fleet; JourneyBacklog, overwritten vehicle by vehicle, cannot say.
        await roundOutcomes.RecordAsync(
                new DispatchRoundOutcome(
                    round,
                    [.. participants.Where(p => p.RanToItsEnd)
                        .Select(p => new DispatchVehicleOutcome(p.Vehicle.AgvId, p.Vehicle.VehicleKey, p.Verdicts))]),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 一辆车参加这一轮所需的事实，用它自己的预算读；读不完或读挂了就不参加，别的车照常。
    /// </summary>
    /// <summary>一趟旅程的开始：哪辆车、什么时候（批次7-06，control-server#211）。</summary>
    internal readonly record struct JourneyStart(string AgvId, DateTimeOffset CreatedAt);

    /// <summary>每辆车<b>最近</b>一次接单的时刻——车辆侧带内那一层要的就是这个量。</summary>
    /// <remarks>
    /// <para>
    /// <b>抽成一个拿得到的函数，是因为把 <c>Max</c> 写成 <c>Min</c> 不会让任何东西报错。</b>排序照样出结果，
    /// 只是反了：一辆车跑得越多，它的「上次接单」越是停在第一趟上，于是永远被判为久未接单而优先——
    /// 本来防饥饿的那一层变成了制造饥饿的那一层。这种错没有异常、没有空值、没有越界，只有派车分布慢慢歪掉。
    /// </para>
    /// <para>
    /// <b>它收已经物化的行，不收 <c>IQueryable</c>，这一点是刻意的。</b>生产库是 SQLite，而 SQLite 拿
    /// <c>DateTimeOffset</c> 做聚合或排序会抛；这个签名让「先物化、再在内存里取最大值」成了调用方绕不开的
    /// 写法，而不是一句写在注释里、下一个人照样能改掉的提醒。
    /// </para>
    /// </remarks>
    internal static Dictionary<string, DateTimeOffset> LastDispatchAtByVehicle(IEnumerable<JourneyStart> starts)
    {
        ArgumentNullException.ThrowIfNull(starts);
        return starts
            .GroupBy(start => start.AgvId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(start => start.CreatedAt), StringComparer.Ordinal);
    }

    /// <summary>见 <see cref="LogBlockedVehicleJudgedACandidate"/>：那条日志要的新鲜读就在这里。</summary>
    private async Task WarnIfTheVehicleIsActuallyBlockedAsync(
        RoundVehicle participant,
        AcceptedDemandSnapshot candidate,
        string reason,
        CancellationToken cancellationToken)
    {
        // AsNoTracking 不只是为了省：这个 DbContext 里很可能已经跟踪着同一行的实例，而已跟踪的实体不会被
        // 数据库的新值刷新——那正是这条诊断要绕开的那份数据。
        bool blocked = await dbContext.JourneyRuntimes.AsNoTracking()
            .AnyAsync(
                row => row.AgvId == participant.Vehicle.AgvId && row.Stage == JourneyRuntimeStage.Blocked,
                cancellationToken)
            .ConfigureAwait(false);
        if (blocked)
        {
            LogBlockedVehicleJudgedACandidate(logger, participant.Vehicle.AgvId, candidate.DemandId, reason, null);
        }
    }

    private async Task<RoundVehicle?> TryAdmitToRoundAsync(
        FleetVehicle vehicle,
        bool underWay,
        VehicleDispatchPolicy policy,
        Dictionary<string, DateTimeOffset> lastDispatchedAt,
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        CancellationToken cancellationToken)
    {
        TimeSpan budget = RoundBudget(policy, vehicle.AgvId);
        long startedAt = timeProvider.GetTimestamp();
        using CancellationTokenSource expiry = new(budget, timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            VehicleSlotPositions? positions = await slotPositions.ReadAsync(vehicle.AgvId, linked.Token)
                .ConfigureAwait(false);
            OnboardDispatchFacts? onboard = await onboardFacts
                .ReadOnboardFactsAsync(vehicle.AgvId, linked.Token).ConfigureAwait(false);
            RiotVehicleObservation observation = await vehicleFacts
                .ReadVehicleAsync(vehicle.VehicleKey, linked.Token).ConfigureAwait(false);
            EnRouteVehiclePlan? plan = underWay
                ? await ReadEnRoutePlanAsync(vehicle, observation, linked.Token).ConfigureAwait(false)
                : null;
            if (underWay && plan is null)
            {
                // 在途却读不出计划：这一轮不问它追加，别的车照常。旅程正在被推进段改写时会短暂落进这里。
                return null;
            }

            DispatchVehicleFacts facts = new(
                vehicle.VehicleKey, vehicle.AgvId, onboard, observation, timeProvider.GetUtcNow(), positions, plan);
            return new RoundVehicle(vehicle, facts, underWay, budget)
            {
                LastDispatchedAt = lastDispatchedAt.TryGetValue(vehicle.AgvId, out DateTimeOffset at) ? at : null,
                Spent = timeProvider.GetElapsedTime(startedAt),
            };
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            LogVehicleRoundBudgetExhausted(logger, vehicle.AgvId, (int)budget.TotalMilliseconds, null);
            await DropWhatTheSegmentStagedAsync(backlogByDemandId, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (IsInvariantBreach(error))
            {
                LogVehicleRoundFaulted(logger, vehicle.AgvId, error.GetType().Name, error);
            }
            else if (underWay)
            {
                // 在途车问不动记它自己那个事件 id：车还在跑自己的旅程，运维什么都不用做；空闲车问不动才是
                // 一辆闲着的车白闲了一轮。两者一直是分开的，翻转不改这一点。
                LogInTransitQualificationFailed(logger, vehicle.AgvId, error.GetType().Name, error);
            }
            else
            {
                LogVehicleRoundFailed(logger, vehicle.AgvId, error.GetType().Name, error);
            }

            await DropWhatTheSegmentStagedAsync(backlogByDemandId, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// 一辆在途车此刻的计划：它还没完成的停靠、它在哪、每个停靠此刻的清单项数。
    /// </summary>
    private async Task<EnRouteVehiclePlan?> ReadEnRoutePlanAsync(
        FleetVehicle vehicle,
        RiotVehicleObservation observation,
        CancellationToken cancellationToken)
    {
        if (observation.CurrentStationId is not { } vehicleStation)
        {
            // 不知道车在哪，就算不出任何一段代价，也就谈不上追加。fail closed，与可达性判据同一条道理。
            return null;
        }

        // 排序在客户端做：SQLite 不支持把 DateTimeOffset 放进 ORDER BY，这个仓库的生产库就是 SQLite，
        // 所以写成数据库端排序会在真机上抛 NotSupportedException 而不只是在测试里。一辆车至多一条未完成旅程，
        // 取回来的本来就只有一行，排序只是把「锚需求是最早那一条」写明白。
        // Blocked 的旅程也排除（批次7-06，control-server#211）：这里答的是「这辆车的计划能不能被追加」，
        // 而一趟等人介入的旅程接不了追加——在途资格链无条件拒绝它，车上那张计划也不会再更新
        // （RefreshUpcomingStopPlanAsync 对 Blocked 直接返回），追加进去的需求会绑死在这辆车上、永不再是候选。
        //
        // 今天没有 Blocked 的车能走到这里：JourneyRuntimeEngine 已经把它们排除出 underWay，而那是唯一入口。
        // 写在这里是因为那条保证靠的是别处的集合怎么构造，这个查询自己对 Blocked 一无所知——改了那边，
        // 这里不会有东西变红。
        //
        // <b>这一处没有行为判据，实测过：</b>拆掉这两个字的排除，
        // MultiVehicleExecutionTests.ABlockedJourneyTakesNoAppendedDemand 照样绿，因为 Blocked 的车压根不进
        // underWay，走不到这个查询。它是纵深的第二道，留着是为了让这个查询自己也说得出它要什么。
        //
        // <b>它和 WireToGateStore.StageAndCommitAppendAsync 里那一处不是重复的，别删掉任何一处。</b>
        // 两处判的是两个不同时刻的两件不同的事：这里是<b>准入口径</b>——轮次开始时就已经 Blocked 的车，
        // 不值得为它算一遍插位；那里是<b>写入一致性</b>——旅程在轮次读过之后才变成 Blocked，那是一个竞态，
        // 只有和那次写在同一个事务里的判据挡得住。这一处在它自己的时刻是对的，挡不住也不该挡那个竞态。
        JourneyRuntimeRow? runtime = (await dbContext.JourneyRuntimes.AsNoTracking()
                .Where(row => row.AgvId == vehicle.AgvId &&
                    row.Stage != JourneyRuntimeStage.Completed &&
                    row.Stage != JourneyRuntimeStage.Blocked)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(row => row.CreatedAt)
            .FirstOrDefault();
        if (runtime is null)
        {
            return null;
        }

        JourneyStopCursor stops = await JourneyStopCursor
            .LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false);
        // 整条旅程的停靠都交给规划器，已完成的也在内（批次7-06，control-server#211）。序位是整条旅程的属性，
        // 而这之前只传未完成的那些，于是插入之后的重排从 1 重新数，与已经完成的停靠撞号。规划器自己把已完成的
        // 那一段当作不可移动的前缀：它们不参与代价、分区与上限，只占住自己的号。
        IReadOnlyList<JourneyStopRow> all = stops.Stops;
        if (stops.OpenStops.Count == 0)
        {
            return null;
        }

        // 取当前停靠自己的下标，而不是数已完成的个数：作废（Removed）的停靠也不开着、也不能移动，
        // 数完成数会把它算漏，前缀就会短一格。
        int currentNextStopIndex = all.ToList().IndexOf(stops.Current);

        return new EnRouteVehiclePlan(
            [.. all.Select(stop => new EnRouteStop(
                stop.StopId, stop.StationId, stop.StationRiotId, stop.DispatchZone, stop.StopRole))],
            vehicleStation,
            // 当前下一站是第一个还没完成的停靠（REQ-0196）。
            currentNextStopIndex,
            all.ToDictionary(
                stop => stop.StopId,
                stop => stops.AllAtStop(stop).Count(item => !JourneyStopCursor.IsDoneAt(stop, item)),
                StringComparer.Ordinal));
    }

    /// <summary>一辆车对一条任务的裁决：过了就成为一份出价，没过就只留下积压里的理由。</summary>
    private async Task JudgeForVehicleAsync(
        DispatchRoundFacts round,
        RoundVehicle participant,
        AcceptedDemandSnapshot candidate,
        List<CandidateOffer> offers,
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DispatchCandidateEvaluation evaluation = new(candidate, round, participant.Facts);
        string reason;
        // 预算是「这辆车自己花掉的时间」，不是墙钟（批次7-06，control-server#211）。翻转之后一辆车的评估散在多条
        // 任务里，中间隔着别的车在做事；一个从轮开始就走的定时器会把后面的车在轮到它之前就切断——那不是这辆车慢，
        // 是它在排队。所以每次评估各起一个剩余预算的定时器，评估完把真实耗时扣掉。
        long startedAt = timeProvider.GetTimestamp();
        using CancellationTokenSource expiry = new(participant.Remaining, timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            reason = participant.UnderWay
                ? await inTransitChain.EvaluateAsync(evaluation, linked.Token).ConfigureAwait(false)
                : await admissionChain.EvaluateAsync(evaluation, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            LogVehicleRoundBudgetExhausted(
                logger, participant.Vehicle.AgvId, (int)participant.Budget.TotalMilliseconds, null);
            participant.WithdrawFromRound();
            await DropWhatTheSegmentStagedAsync(backlogByDemandId, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            if (IsInvariantBreach(error))
            {
                LogVehicleRoundFaulted(logger, participant.Vehicle.AgvId, error.GetType().Name, error);
            }
            else if (participant.UnderWay)
            {
                // 在途车问不动，与空闲车问不动是两件事：前者车还在跑自己的旅程，运维什么都不用做；
                // 后者是一辆闲着的车白闲了一轮。两者的事件 id 因此一直是分开的，翻转不改这一点。
                LogInTransitQualificationFailed(logger, participant.Vehicle.AgvId, error.GetType().Name, error);
            }
            else
            {
                LogVehicleRoundFailed(logger, participant.Vehicle.AgvId, error.GetType().Name, error);
            }

            participant.WithdrawFromRound();
            await DropWhatTheSegmentStagedAsync(backlogByDemandId, cancellationToken).ConfigureAwait(false);
            return;
        }
        finally
        {
            participant.Spent += timeProvider.GetElapsedTime(startedAt);
        }

        if (!string.Equals(reason, DispatchAdmissionChain.Eligible, StringComparison.Ordinal) ||
            evaluation.Route is null)
        {
            // 没过的车当场落裁决：它的理由与别的车接没接走这条任务无关。
            await WarnIfTheVehicleIsActuallyBlockedAsync(participant, candidate, reason, cancellationToken)
                .ConfigureAwait(false);
            participant.Verdicts.Add(new DispatchCandidateVerdict(evaluation, reason));
            UpsertBacklog(backlogByDemandId, candidate, reason, now);
            return;
        }

        if (!participant.MayStillTakeWork)
        {
            // 这一轮已经接过单的车：结论照记，出价不收。它合格是真的，只是没有第二次机会。
            participant.Verdicts.Add(new DispatchCandidateVerdict(evaluation, reason));
            UpsertBacklog(backlogByDemandId, candidate, reason, now);
            return;
        }

        // 过了的先不落：这条任务还没决出归谁，而「合格」与「合格但被别人接走了」是两个不同的结论
        // （批次7-06，control-server#211）。翻转之前后者由排在后面的车自己算出来——那时一条需求一旦被前面的车
        // 认领，后面的车在判据链里就看到它已被接受；翻转之后一条任务上的车是同时判的，谁也看不见谁，
        // 所以这个区分改由决出胜者之后统一写下。
        JourneyBacklogRow backlog = backlogByDemandId.TryGetValue(candidate.DemandId, out JourneyBacklogRow? row)
            ? row
            : UpsertBacklog(backlogByDemandId, candidate, reason, now);

        EligibleDispatchCandidate eligible = new(
            candidate,
            evaluation.Route,
            evaluation.ExpectedBasketCount,
            evaluation.TargetSlots,
            backlog.FirstSeenAt,
            evaluation.GraphTraversalCostMm,
            evaluation.CatalogRevision,
            evaluation.AreaAssignmentVersion,
            evaluation.RequiredSlotPosition);
        offers.Add(new CandidateOffer(participant, evaluation, new EligibleVehicleOffer(
            participant.Vehicle,
            participant.Facts,
            eligible,
            // 在途车的边际成本由插位规划算出；空闲车的原计划是空的，增量就是走完这一趟的全程，而路网此刻只给到
            // 取货站那一段——两者因此都以「这一趟新增的行程」为准，空闲车少了取货到卸货那一段，PR 写明。
            evaluation.AppendPlacement?.MarginalCostMm ?? evaluation.GraphTraversalCostMm,
            evaluation.AppendPlacement,
            evaluation.DispatchZoneParameterVersion,
            participant.LastDispatchedAt)));
    }

    /// <summary>
    /// 一条任务决出胜者之后，把这一条上所有合格车的裁决一起落下：胜者合格，其余是「本轮已被接走」。
    /// </summary>
    /// <remarks>
    /// 积压行上留下的理由是落选者写的那一个——与翻转之前由排在最后的那辆车写下的结果一致。只有一辆车出价时
    /// 行上就是受理自己写的，那时本来也没有别人可以被挡。
    /// </remarks>
    /// <param name="winnerWroteItsOwnReason">
    /// 胜者是不是已经写下了这条需求自己那条更具体的积压理由（受理拒绝的四种之一）。
    /// <b>是的话就不要再把它盖成「已被接走」</b>（批次7-06，control-server#211）：那条需求一辆车都没接走，
    /// 而看板读的正是积压行的理由——它会指着一辆不存在的车，查的人先去找那辆车。
    ///
    /// 别的车的<b>裁决</b>仍然记「已被接走」，那是对的：从那辆车的视角，它判这条候选时它确实已被本轮认领。
    /// 两者视角不同，看板读的是积压行。
    /// </param>
    private void RecordVerdictsForCandidate(
        List<CandidateOffer> offers,
        CandidateOffer? winner,
        bool winnerWroteItsOwnReason,
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        AcceptedDemandSnapshot candidate,
        DateTimeOffset now)
    {
        if (winner is not null)
        {
            winner.Participant.Verdicts.Add(
                new DispatchCandidateVerdict(winner.Evaluation, DispatchAdmissionChain.Eligible));
        }

        // 垮在受理里的车不进轮次结局，它那一条裁决也就无处可记；这里跳过它们，留下的正是这条需求
        // 对还站着的那些车的结论。
        foreach (CandidateOffer other in offers.Where(offer =>
                     !ReferenceEquals(offer, winner) && offer.Participant.RanToItsEnd))
        {
            other.Participant.Verdicts.Add(
                new DispatchCandidateVerdict(other.Evaluation, AlreadyAcceptedCriterion.DemandAlreadyAccepted));
            // 照常 Upsert——它还负责 LastSeenAt 与决策指纹——但胜者写过自己的理由时把那条原样带回去，
            // 只是不覆盖它。
            string reason = winnerWroteItsOwnReason &&
                    backlogByDemandId.TryGetValue(candidate.DemandId, out JourneyBacklogRow? written)
                ? written.ReasonCode
                : AlreadyAcceptedCriterion.DemandAlreadyAccepted;
            UpsertBacklog(backlogByDemandId, candidate, reason, now);
        }
    }

    /// <summary>一辆车对一条任务的出价，外加落裁决要用的那两样。</summary>
    private sealed record CandidateOffer(
        RoundVehicle Participant,
        DispatchCandidateEvaluation Evaluation,
        EligibleVehicleOffer Offer);

    /// <summary>
    /// 一辆车、一条任务定下来之后：空闲车受理新旅程，在途车追加进它已有的旅程。
    /// </summary>
    /// <returns>
    /// 这条任务在这辆车手里的去向。垮在里面（预算耗尽、抛异常）报 <c>VehicleCannotTake</c>——已经认领的撤回，
    /// 这辆车退出本轮，别的车照常。
    /// </returns>
    private async Task<CandidateDispatchOutcome> DispatchSelectedAsync(
        DispatchRoundFacts round,
        CandidateOffer winner,
        HashSet<string> acceptedDemandIds,
        HashSet<string> claimsIntakeRefused,
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // 认领与受理整段都在这一层的保护里（批次7-06，control-server#211）。翻转之前它是逐车循环里的最后几步，
        // 与判据同在一个 try 内、同一个预算下；翻转之后判据按任务分散、受理按车集中，所以保护跟着搬过来，
        // 覆盖的范围一字未减——写积压理由、建旅程、认领占用，任何一步抛出来、或者读挂在那里把这辆车的预算耗光，
        // 都是这辆车退出本轮，别的车照常。<b>受理这一段尤其不能漏掉预算</b>：最终重读要问 RIoT 与车载端，
        // 而那正是「既不回答也不失败」最常出现的地方。
        RoundVehicle participant = winner.Participant;
        EligibleVehicleOffer selected = winner.Offer;
        List<string> claimedHere = [];
        long startedAt = timeProvider.GetTimestamp();
        using CancellationTokenSource expiry = new(participant.Remaining, timeProvider);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            return await DispatchSelectedCoreAsync(
                    round, selected, participant.UnderWay, acceptedDemandIds, claimsIntakeRefused, claimedHere,
                    now, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (expiry.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            LogVehicleRoundBudgetExhausted(
                logger, participant.Vehicle.AgvId, (int)participant.Budget.TotalMilliseconds, null);
            await DropWhatTheSegmentStagedAsync(
                    backlogByDemandId, claimedHere, acceptedDemandIds, claimsIntakeRefused, cancellationToken)
                .ConfigureAwait(false);
            return CandidateDispatchOutcome.VehicleCannotTake;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // 判据链里与这里用同一套分流：破坏本服务端自己的不变量是缺陷，记 Error 与它自己的事件 id，
            // 但同样只隔离这一辆车——下一轮它不会自己好，而别的车没有理由陪着停。
            if (IsInvariantBreach(error))
            {
                LogVehicleRoundFaulted(logger, selected.Vehicle.AgvId, error.GetType().Name, error);
            }
            else
            {
                LogVehicleRoundFailed(logger, selected.Vehicle.AgvId, error.GetType().Name, error);
            }

            await DropWhatTheSegmentStagedAsync(
                    backlogByDemandId, claimedHere, acceptedDemandIds, claimsIntakeRefused, cancellationToken)
                .ConfigureAwait(false);
            return CandidateDispatchOutcome.VehicleCannotTake;
        }
        finally
        {
            participant.Spent += timeProvider.GetElapsedTime(startedAt);
        }
    }

    /// <summary>上一个方法的内核：这一段本身，保护在外面那一层。</summary>
    /// <returns>这条任务在这一轮的去向，见 <see cref="CandidateDispatchOutcome"/>。</returns>
    private async Task<CandidateDispatchOutcome> DispatchSelectedCoreAsync(
        DispatchRoundFacts round,
        EligibleVehicleOffer selected,
        bool participantUnderWay,
        HashSet<string> acceptedDemandIds,
        HashSet<string> claimsIntakeRefused,
        List<string> claimedHere,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = round;
        string demandId = selected.Candidate.Snapshot.DemandId;
        long expectedSessionGeneration = selected.Facts.Onboard?.SessionGeneration
            ?? throw new InvalidOperationException("An eligible candidate requires current Onboard facts.");
        // 在途与否取轮次给这辆车的那个事实，不从插入位反推（批次7-06，control-server#211）。
        //
        // <c>Placement</c> 只有 <see cref="EnRouteAppendCriterion"/> 会填，而那条判据是<b>可选</b>的——
        // <c>DispatchAdmissionCriteria.InTransit</c> 只在 <c>routeGraph</c> 非空时加它。生产里 Program.cs 无条件
        // 注册路网，所以今天反推恰好对；没有路网的配置下，一辆在途车会被当成空闲车走完整条受理，
        // 去建一趟新旅程、认领它已经被占着的车。
        //
        // 两者不一致就是这台服务器自己的不变量被破坏了，所以抛而不是挑一个：在途车拿不到插入位却走到了这里，
        // 意味着在途资格链没有按它该有的样子装起来。
        //
        // <b>它在写下的当天就响了一次，而且指对了地方。</b>给 FleetFixture 装路网时，路网只传给了空闲链、
        // 漏传给 InTransit（那是另一个参数），于是在途车被判为合格却拿不到插入位。没有这一条，当时看到的
        // 会是「凭空多出第二趟旅程」，而第一反应会是去怀疑轮次逻辑——一道为防将来而加的守卫，价值不只在于
        // 将来会响，还在于它响的时候指的是对的地方。
        bool underWay = participantUnderWay;
        if (underWay != (selected.Placement is not null))
        {
            throw new BusinessIdentityConflictException(
                $"Vehicle '{selected.Vehicle.AgvId}' is under way but its offer carries no en-route placement; " +
                "the in-transit admission chain is missing its route graph.");
        }

        // 这两处返回 false：它们是<b>这辆车自己</b>的原因，换一辆车会得到不同的答案（批次7-06，control-server#211）。
        //
        // 出价循环那句「受理把它拒掉是这条需求自己的结论，换一辆车再试一次只会得到同一个答案」，对最终重读
        // 发现候选变了、没了那种情形成立，对下面这两种不成立：租约是<b>这一辆</b>车的租约，最终动态事实读的是
        // <b>这一辆</b>车的状态。翻转之前这两处从「这辆车自己那一段」返回，后面的车会重新判到这条需求；翻转之后
        // 它们落在同一个方法里，不区分就会把这条任务在本轮吃掉——而且积压行上还会被盖成 DEMAND_ALREADY_ACCEPTED，
        // 看板显示「已被接走」，而这条需求根本没有任何人接受。
        if (!underWay && await dbContext.VehicleDispatchLeases.AnyAsync(
                row => row.VehicleKey == selected.Vehicle.VehicleKey && row.ReleasedAt == null,
                cancellationToken).ConfigureAwait(false))
        {
            return CandidateDispatchOutcome.VehicleCannotTake;
        }

        if (!await FinalDynamicFactsReadyAsync(
                selected.Vehicle, underWay, expectedSessionGeneration, selected.Candidate.TargetSlots,
                cancellationToken).ConfigureAwait(false))
        {
            await SetBacklogReasonAsync(
                demandId, "FINAL_DYNAMIC_FACTS_NOT_READY", timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);
            // 换下一个出价者。这条理由不需要特殊照顾：真有车接走了，积压行本来就该记「已被接走」加上受理时刻；
            // 全都接不了时 winner 为 null，落裁决那个循环根本不执行，它原样留着。
            return CandidateDispatchOutcome.VehicleCannotTake;
        }

        DateTimeOffset intakeAt = timeProvider.GetUtcNow();
        JourneyExecutionPlan plan = new JourneyPlanBuilder(runtimeOptions)
            .CreatePlan(selected.Vehicle, selected.Candidate, intakeAt);
        // Taken before the call rather than after it: a candidate this vehicle is committing to must stop being
        // a candidate for the rest of the round whatever the intake then reports.
        acceptedDemandIds.Add(demandId);
        claimedHere.Add(demandId);
        JourneyIntakeResult result = underWay
            ? await AppendToJourneyAsync(selected, plan, intakeAt, expectedSessionGeneration, cancellationToken)
                .ConfigureAwait(false)
            : await intakeCoordinator.AcceptAndDispatchToPickupAsync(
                    selected.Candidate.Snapshot,
                    JourneyPlanBuilder.PickupIntent(plan, demandId, intakeAt),
                    plan,
                    token => FinalDynamicFactsReadyAsync(
                        selected.Vehicle, underWay, expectedSessionGeneration,
                        selected.Candidate.TargetSlots, token),
                    cancellationToken)
                .ConfigureAwait(false);

        if (result.IntakeOutcome != DemandIntakeOutcome.Accepted)
        {
            if (result.IntakeOutcome is DemandIntakeOutcome.CandidateChanged
                or DemandIntakeOutcome.FinalAdmissionRejected
                or DemandIntakeOutcome.JourneyPlanIncomplete)
            {
                claimsIntakeRefused.Add(demandId);
            }

            await SetBacklogReasonAsync(
                demandId,
                result.IntakeOutcome switch
                {
                    DemandIntakeOutcome.CandidateGone => "FINAL_CATALOG_CANDIDATE_GONE",
                    DemandIntakeOutcome.CandidateChanged => "FINAL_CATALOG_DECISION_FACT_CHANGED",
                    DemandIntakeOutcome.FinalAdmissionRejected => "FINAL_DYNAMIC_FACTS_NOT_READY",
                    DemandIntakeOutcome.JourneyPlanIncomplete => "FINAL_JOURNEY_PLAN_INCOMPLETE",
                    _ => throw new InvalidOperationException($"Unsupported intake outcome '{result.IntakeOutcome}'.")
                },
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            // 这条任务这一轮到此为止，即便其中 FinalAdmissionRejected 也是「这辆车自己」的原因。
            //
            // <b>界线在 acceptedDemandIds.Add 上，不在原因上。</b>上面那两处在它之前，这一处在它之后：
            // 这条需求已经被标记为本轮接走，换一辆车再试要先把那个标记连同这一段写下的东西一起撤回，
            // 而那是预算耗尽与抛异常两条路径上 DropWhatTheSegmentStagedAsync 做的事。在这里返回 false
            // 会让下一个出价者看到一条「已被接走」的需求，比现在更糟。
            return CandidateDispatchOutcome.RefusedWithItsOwnReason;
        }

        if (underWay)
        {
            // 追加「不认领车辆占用」（票面「车辆占用」那一条）：三套占用都是一车一行，这辆车已经被这趟旅程占着，
            // 再认领一次会被索引直接拒绝。
            return CandidateDispatchOutcome.Taken;
        }

        // The vehicle is now carrying this journey's first order, and that is what the occupancy claim records.
        if (!await dispatchPolicy.TryClaimVehicleOccupancyAsync(plan.PickupUpperId, cancellationToken)
                .ConfigureAwait(false))
        {
            LogVehicleOccupancyConflict(logger, selected.Vehicle.AgvId, plan.PickupUpperId, null);
            JourneyRuntimeRow conflicted = await dbContext.JourneyRuntimes
                .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
            Block(conflicted, "VEHICLE_OCCUPANCY_CONFLICT", timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            // 旅程已经建出来并且被 Block 了，这条需求这一轮有结论了。理由写在旅程的阻断码上，不在积压行上。
            return CandidateDispatchOutcome.Taken;
        }

        if (result.MovementDispatch?.Outcome != MovementDispatchOutcome.Confirmed)
        {
            JourneyRuntimeRow runtime = await dbContext.JourneyRuntimes
                .SingleAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
            runtime.SetBlockReason(
                result.MovementDispatch?.Outcome.ToString() ?? "PICKUP_DISPATCH_NOT_CONFIRMED", now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return CandidateDispatchOutcome.Taken;
    }

    /// <summary>一条任务在一辆车手里的去向（批次7-06，control-server#211）。</summary>
    private enum CandidateDispatchOutcome
    {
        /// <summary>接下了。这条任务这一轮结束。</summary>
        Taken,

        /// <summary>
        /// <b>这辆车</b>接不了，而换一辆车会得到不同的答案——租约是这一辆的租约，最终动态事实读的是这一辆的状态。
        /// 出价循环把这条任务交给下一个出价者。
        /// </summary>
        VehicleCannotTake,

        /// <summary>
        /// 这条任务这一轮结束，<b>而且它已经写下了自己那条更具体的积压理由</b>——最终重读发现候选没了、变了、
        /// 车的最终事实不就绪、计划冻结不全，四种之一。落裁决时不要再把它盖成「已被接走」。
        /// </summary>
        RefusedWithItsOwnReason
    }

    /// <summary>把这条需求追加进这辆在途车的旅程；不建订单、不认领占用。</summary>
    private async Task<JourneyIntakeResult> AppendToJourneyAsync(
        EligibleVehicleOffer selected,
        JourneyExecutionPlan plan,
        DateTimeOffset intakeAt,
        long expectedSessionGeneration,
        CancellationToken cancellationToken)
    {
        EnRouteAppendPlacement placement = selected.Placement
            ?? throw new InvalidOperationException("An appended demand has a placement.");
        string demandId = selected.Candidate.Snapshot.DemandId;
        // 客户端排序，理由与 ReadEnRoutePlanAsync 那一处相同：SQLite 的 ORDER BY 接不了 DateTimeOffset。
        JourneyRuntimeRow runtime = (await dbContext.JourneyRuntimes.AsNoTracking()
                .Where(row => row.AgvId == selected.Vehicle.AgvId && row.Stage != JourneyRuntimeStage.Completed)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(row => row.CreatedAt)
            .First();
        // 这里<b>不</b>判旅程还接不接得下追加，那件事在 WireToGateStore.StageAndCommitAppendAsync 的事务里判
        // （批次7-06，control-server#211）。在这里判会挡住一部分、放过另一部分：从这一读到那一写之间，
        // 车载端的一条入站消息仍然可以把旅程置成 Blocked，而这里已经判完了。一个挡得住一半的判据比没有更糟，
        // 它会让读代码的人以为这条路已经被看住了。
        JourneyAppendPlan append = new(
            runtime.JourneyId,
            demandId,
            plan,
            placement.MergeIntoPickupStopId ?? JourneyIdentity.AppendedPickupStopId(demandId),
            placement.MergeIntoUnloadStopId ?? JourneyIdentity.AppendedUnloadStopId(demandId),
            selected.Candidate.Route.DispatchZone,
            selected.DispatchZoneParameterVersion,
            [.. placement.Resequenced.Select(stop => new JourneyStopSequenceChange(stop.StopId, stop.Sequence))],
            intakeAt);
        DemandIntakeOutcome outcome = await intakeCoordinator.AppendToJourneyAsync(
                selected.Candidate.Snapshot,
                append,
                token => FinalDynamicFactsReadyAsync(
                    selected.Vehicle, underWay: true, expectedSessionGeneration,
                    selected.Candidate.TargetSlots, token),
                cancellationToken)
            .ConfigureAwait(false);
        return new JourneyIntakeResult(outcome, null);
    }

    /// <summary>
    /// 一辆车在这一轮里的身份：它的事实、它自己的预算，以及它对每条任务的裁决。
    /// </summary>
    private sealed class RoundVehicle(
        FleetVehicle vehicle,
        DispatchVehicleFacts facts,
        bool underWay,
        TimeSpan budget)
    {
        private bool _withdrawn;
        private bool _tookADemand;

        public FleetVehicle Vehicle { get; } = vehicle;

        public DispatchVehicleFacts Facts { get; } = facts;

        public bool UnderWay { get; } = underWay;

        public List<DispatchCandidateVerdict> Verdicts { get; } = [];

        public DateTimeOffset? LastDispatchedAt { get; init; }

        /// <summary>这辆车这一轮的预算，以及它已经花掉的部分。</summary>
        public TimeSpan Budget { get; } = budget;

        public TimeSpan Spent { get; set; }

        /// <summary>还剩多少。花光了就给一个零，下一次评估立刻被切断。</summary>
        public TimeSpan Remaining => Spent >= Budget ? TimeSpan.Zero : Budget - Spent;

        /// <summary>还能不能再接这一轮的任务：没退出，而且这一轮还没接过一条。</summary>
        /// <remarks>
        /// <b>一轮一车至多一条</b>，与轮次翻转之前逐字相同（那时是「每辆车挑最便宜的一条」，一段循环只走一次）。
        /// 翻转之后这件事要显式记下来：任务在外层循环，一辆车不标记就会在后面每一条任务上继续参与竞争，
        /// 而它本来就是最优的那辆——于是后面的任务全被它「赢走」再被租约挡下，一轮只派得出一条。
        /// </remarks>
        public bool MayStillTakeWork => !_withdrawn && !_tookADemand;

        /// <summary>这辆车这一轮已经接了一条，不再参与后面的任务。</summary>
        public void TookADemand() => _tookADemand = true;

        /// <summary>
        /// 这辆车退出本轮。它此后不再被问，<b>也不进本轮的裁决汇总</b>——一段没跑完的判断说不出这辆车拒绝了什么，
        /// 而结构性告警正是把「每辆车都拒绝」变成车队级告警的地方。
        /// </summary>
        public void WithdrawFromRound() => _withdrawn = true;

        public bool RanToItsEnd => !_withdrawn;
    }

    /// <summary>
    /// Drops whatever the segment that just ended had staged, and reads the backlog back as the database holds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the two ways a segment can end early -- its budget running out and it throwing -- because the
    /// hazard is one hazard. What the abandoned segment staged is not that vehicle's decision any more and must not
    /// be written under the next vehicle's <c>SaveChanges</c>: stopping the work is not what keeps one vehicle's
    /// trouble off the others, clearing the tracker they all share is.
    /// </para>
    /// <para>
    /// <b>This overload is for a path that claims nothing</b> -- today only the in-transit qualification below,
    /// which asks one question and writes nothing. The empty claim list is what leaves the round's accepted set
    /// alone: the overload below only ever removes what that list names.
    /// </para>
    /// <para>
    /// It passes a set of its own rather than one shared static empty one. The overload below removes from what it
    /// is handed, so a static would be mutable state shared across every instance of this class -- safe only for
    /// as long as nobody hands this path a claim, which is the kind of thing control-server#211 finds by breaking
    /// it.
    /// </para>
    /// </remarks>
    private Task DropWhatTheSegmentStagedAsync(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        CancellationToken cancellationToken) =>
        DropWhatTheSegmentStagedAsync(
            backlogByDemandId,
            [],
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            cancellationToken);

    /// <inheritdoc cref="DropWhatTheSegmentStagedAsync(Dictionary{string, JourneyBacklogRow}, CancellationToken)"/>
    /// <remarks>
    /// <para>
    /// <b>It also takes back a claim the segment never made good on.</b> A vehicle claims its pick in the round's
    /// live accepted set before calling intake, so that the vehicles behind it stop considering that demand
    /// whatever intake then reports. When the segment ends early instead of reporting -- throwing, or its budget
    /// running out -- that claim can be a lie: the round would carry a demand it never accepted, and the round-end
    /// hook clears a structural dispatch block for every demand the round says was accepted -- so a block standing
    /// against a demand nothing took would be cleared, and raised again as new the next round.
    /// </para>
    /// <para>
    /// Whether it was made good on is decided by the database rather than by how the segment ended: the tracker is
    /// cleared first, so this reads what the acceptance transaction actually committed. A demand whose row is
    /// there was accepted, whatever threw or expired afterwards, and its claim stands.
    /// </para>
    /// <para>
    /// <b>The round-end subtraction goes with the claim, unconditionally</b> (control-server#242). It only means
    /// anything while a claim is standing: withdrawn, the demand is back in play and the vehicle behind may accept
    /// it — and then the round-end hook must clear its block like any other acceptance. The window is narrow but
    /// real: intake reports a refusal, the demand is named in the subtraction, and the backlog write right after
    /// it runs out the budget or throws. Leaving the id behind would hold that block back for a round even though
    /// the demand was accepted after all.
    /// </para>
    /// </remarks>
    private async Task DropWhatTheSegmentStagedAsync(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        IReadOnlyList<string> claimedThisSegment,
        HashSet<string> acceptedDemandIds,
        HashSet<string> claimsIntakeRefused,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        foreach (string demandId in claimedThisSegment)
        {
            claimsIntakeRefused.Remove(demandId);
            bool accepted = await dbContext.AcceptedDemands
                .AnyAsync(row => row.DemandId == demandId, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                acceptedDemandIds.Remove(demandId);
            }
        }

        backlogByDemandId.Clear();
        foreach (JourneyBacklogRow row in await dbContext.JourneyBacklog
                     .ToArrayAsync(cancellationToken).ConfigureAwait(false))
        {
            backlogByDemandId[row.DemandId] = row;
        }
    }

    /// <summary>
    /// How long this vehicle's segment of the round may take.
    /// </summary>
    /// <remarks>
    /// A vehicle the policy does not mention still gets the default budget rather than none. It
    /// cannot be dispatched — the task-type criterion refuses an unconfigured vehicle — but the
    /// point of running it anyway is that refusal reaching the backlog under its own reason code.
    /// A zero budget would replace that diagnosis with a timeout every round.
    /// </remarks>
    private static TimeSpan RoundBudget(VehicleDispatchPolicy policy, string agvId)
    {
        VehicleDispatchProfile? profile = policy.Vehicles
            .FirstOrDefault(vehicle => string.Equals(vehicle.AgvId, agvId, StringComparison.Ordinal));
        int milliseconds = profile?.RoundTimeoutMilliseconds ?? 0;
        return TimeSpan.FromMilliseconds(milliseconds > 0
            ? milliseconds
            : new FleetVehicleOptions().RoundTimeoutMilliseconds);
    }

    /// <summary>
    /// Re-reads the dynamic facts immediately before intake and re-runs the same verdict the
    /// admission chain reached.
    /// </summary>
    /// <remarks>
    /// It calls <see cref="VehicleDynamicFactsCriterion.Evaluate"/> rather than repeating its
    /// clauses, so this check and the chain's cannot drift apart. The session generation and slot
    /// checks sit on top of it: they are what makes this a re-check of *this* decision rather than
    /// a fresh one.
    /// </remarks>
    private async Task<bool> FinalDynamicFactsReadyAsync(
        FleetVehicle fleetVehicle,
        bool underWay,
        long expectedSessionGeneration,
        IReadOnlyCollection<int> targetSlots,
        CancellationToken cancellationToken)
    {
        OnboardDispatchFacts? onboard = await onboardFacts.ReadOnboardFactsAsync(fleetVehicle.AgvId, cancellationToken)
            .ConfigureAwait(false);
        if (onboard is null || onboard.SessionGeneration != expectedSessionGeneration ||
            targetSlots.Any(slot => !onboard.AvailableSlots.Contains(slot)))
        {
            return false;
        }

        RiotVehicleObservation vehicle = await vehicleFacts.ReadVehicleAsync(
            fleetVehicle.VehicleKey,
            cancellationToken).ConfigureAwait(false);
        DispatchVehicleFacts facts = new(
            fleetVehicle.VehicleKey, fleetVehicle.AgvId, onboard, vehicle, timeProvider.GetUtcNow());
        // 复查走的是这辆车自己那条链的那一条判据，不是另一条：在途车永远过不了空闲车的 IDLE 与无订单，
        // 用空闲那条复查等于把每一次追加都拒掉。两条链各有一个可在链外调用的 Evaluate，就是为了这里不走岔。
        return string.Equals(
            underWay
                ? InTransitVehicleFactsCriterion.Evaluate(facts, runtimeOptions)
                : VehicleDynamicFactsCriterion.Evaluate(facts, runtimeOptions),
            DispatchAdmissionChain.Eligible,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Marks every unaccepted backlog row whose demand the catalog no longer lists, so it stops reading as waiting.
    /// </summary>
    /// <remarks>
    /// Rows are only ever written for demands in the catalog, so without this a demand MES closed before this
    /// server took it kept its last reason forever. Saved here, ahead of the vehicle loop, because a vehicle that
    /// runs out its budget clears the change tracker. <c>LastSeenAt</c> is left alone: it stays the last time the
    /// demand was in the catalog. See <see cref="DispatchReasonCodes.DemandLeftCatalog"/>.
    /// </remarks>
    private async Task MarkBacklogLeftCatalogAsync(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        DemandCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        HashSet<string> listed = snapshot.Items.Select(item => item.DemandId).ToHashSet(StringComparer.Ordinal);
        bool marked = false;
        foreach (JourneyBacklogRow row in backlogByDemandId.Values)
        {
            if (row.AcceptedAt is null &&
                !listed.Contains(row.DemandId) &&
                !string.Equals(row.ReasonCode, DispatchReasonCodes.DemandLeftCatalog, StringComparison.Ordinal))
            {
                row.ReasonCode = DispatchReasonCodes.DemandLeftCatalog;
                marked = true;
            }
        }
        if (marked)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private JourneyBacklogRow UpsertBacklog(
        Dictionary<string, JourneyBacklogRow> backlogByDemandId,
        AcceptedDemandSnapshot candidate,
        string reason,
        DateTimeOffset now)
    {
        string fingerprint = DecisionFingerprint(candidate);
        if (!backlogByDemandId.TryGetValue(candidate.DemandId, out JourneyBacklogRow? row))
        {
            row = new JourneyBacklogRow
            {
                DemandId = candidate.DemandId,
                TransportDemandKey = candidate.TransportDemandKey,
                FirstSeenAt = now,
                DemandCreatedAt = candidate.CreatedAt,
                DecisionFingerprint = fingerprint,
                ReasonCode = reason,
                LastSeenAt = now
            };
            dbContext.JourneyBacklog.Add(row);
            backlogByDemandId.Add(candidate.DemandId, row);
        }
        else
        {
            bool decisionFactsChanged = row.TransportDemandKey != candidate.TransportDemandKey ||
                                        row.DecisionFingerprint != fingerprint;
            row.TransportDemandKey = candidate.TransportDemandKey;
            row.DemandCreatedAt = candidate.CreatedAt;
            row.DecisionFingerprint = fingerprint;
            row.ReasonCode = decisionFactsChanged ? "DEMAND_DECISION_FACT_CHANGED" : reason;
            row.LastSeenAt = now;
        }
        return row;
    }

    private static string DecisionFingerprint(AcceptedDemandSnapshot candidate)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            candidate.DemandId,
            candidate.HistoryEpoch,
            candidate.SeriesId,
            candidate.TransportDemandKey,
            candidate.WorkType,
            candidate.Sublot,
            candidate.Generation,
            candidate.DemandRevision,
            candidate.CreatedAt,
            candidate.ValueObservedAt,
            candidate.ValuePollTraceId,
            candidate.ValueProjectionCommitId,
            candidate.LiveMesFields
        }, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private async Task SetBacklogReasonAsync(
        string demandId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        JourneyBacklogRow row = await dbContext.JourneyBacklog.SingleAsync(
            item => item.DemandId == demandId, cancellationToken).ConfigureAwait(false);
        row.ReasonCode = reason;
        row.LastSeenAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Blocks the journey the occupancy claim was refused for. The same three assignments as the engine's own
    /// <c>Block</c>, which the advance side keeps: a journey blocked here is advanced there, and both read it alike.
    /// </summary>
    private static void Block(JourneyRuntimeRow runtime, string reason, DateTimeOffset now)
    {
        runtime.Stage = JourneyRuntimeStage.Blocked;
        runtime.SetBlockReason(reason, now);
        runtime.UpdatedAt = now;
    }
}
