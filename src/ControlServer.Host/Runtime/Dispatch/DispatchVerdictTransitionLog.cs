namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 每对（车, 需求）的派车判定码在两轮之间有没有变（control-server#884）：变了（含第一次判到、判定消失）就答出一条，不变就什么都不答。
/// 派车轮拿答出来的每一条记一条日志（2884），日志因此是判定的变化史，稳态不加一行。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要它。</b>11-06 凌晨 agv02 空闲两小时没派单，事后说不出是哪条判据挡住的：<see cref="SlotGroupFullnessBoard"/> 只留上一轮、整张替换；
/// 积压行的原因码被这一轮最后判它的那辆车覆盖；日志里没有这些需求。这里记的是每辆车自己的码怎么变的，以及变的那一刻它依据的几样输入。
/// </para>
/// <para>
/// <b>不加库读，也不加整表工作。</b>它只读派车轮已经在内存里的东西：轮次结局（<see cref="DispatchRoundOutcome"/>，与
/// <see cref="SlotGroupFullnessBoard"/> 同一份）和本轮开轮时读定的目录。每轮的工作量是这一轮的裁决条数加上正在跟踪的对数，都是派车轮
/// 自己已经遍历过一遍的规模（control-server#881 刚把一轮提速，这里不把它拖回去）。
/// </para>
/// <para>
/// <b>什么算「还在积压」。</b>在本轮目录里，且本轮结束时没有被接走（受理被拒的认领仍算在等，同 <see cref="StarvationEscalationSink"/>）。
/// 不在积压的需求不跟踪：更早就受理了的需求每轮仍会被判成 <c>DEMAND_ALREADY_ACCEPTED</c>，跟踪它只会在重启后多出一批无意义的「首次判到」。
/// 本轮被接走的那条需求，这一轮的裁决照常比对（看得到谁从什么码变成了 <c>ELIGIBLE</c>），比完就丢。
/// </para>
/// <para>
/// <b>一辆车这一轮没给出结论</b>（预算耗尽、读挂了、没参加），它的码保持不动、不记：那是「说不出来」，不是「不挡了」，同
/// <see cref="DispatchRoundOutcome.CompletedVehicles"/> 的口径。需求离开积压时，这一轮没判它的那些车各记一条「判定消失」（新码为空），然后整条丢掉。
/// </para>
/// <para>
/// <b>有上限。</b>最多跟踪 <see cref="DefaultCapacity"/> 对；满了之后新出现的对不跟踪、不记，已跟踪的照常。从未满变满的那一轮答一次
/// <see cref="DispatchVerdictTransitionRound.CapacityJustReached"/>，派车轮记一条警告（2885），免得日志静默地变得不全。
/// </para>
/// <para>
/// 进程内的，宿主里是单例（跨轮状态放单例不放静态字段：静态字段会被并行跑的测试互相清掉）。重启后第一轮里每一对都是「首次判到」，各记一条——
/// 那正是重启后第一份完整的基线。轮次号是这个进程里第几轮记录，重启从 1 重新数；跨重启按 <c>RoundAt</c> 对。
/// </para>
/// </remarks>
public sealed class DispatchVerdictTransitionLog
{
    /// <summary>
    /// 最多跟踪几对（车, 需求）。生产积压几十到一两百条、三辆车，几百对；两万对留足余量，又让一次 MES 目录失控不至于把内存吃掉。
    /// </summary>
    public const int DefaultCapacity = 20_000;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, Dictionary<string, PairState>> _demands = new(StringComparer.Ordinal);
    private int _pairs;
    private long _roundSequence;
    private bool _overCapacity;

    public DispatchVerdictTransitionLog()
        : this(DefaultCapacity)
    {
    }

    internal DispatchVerdictTransitionLog(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>正在跟踪的（车, 需求）对数。</summary>
    public int TrackedPairCount
    {
        get
        {
            lock (_gate)
            {
                return _pairs;
            }
        }
    }

    /// <summary>把一轮的结局与上一次记下的比对，答出变了的每一对；没有轮次事实的结局（只为测按侧那张表构造的）什么都不答、不计轮次。</summary>
    public DispatchVerdictTransitionRound Record(DispatchRoundOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        DispatchRoundFacts? round = outcome.Round;
        if (round is null)
        {
            return DispatchVerdictTransitionRound.None;
        }

        lock (_gate)
        {
            long sequence = ++_roundSequence;
            List<DispatchVerdictTransition> transitions = [];
            Dictionary<string, DispatchVehicleFacts?> completed = new(StringComparer.Ordinal);
            int untracked = 0;

            foreach (DispatchVehicleOutcome vehicle in outcome.CompletedVehicles)
            {
                completed[vehicle.AgvId] = vehicle.Verdicts.Count > 0 ? vehicle.Verdicts[0].Evaluation.Vehicle : null;
                foreach (DispatchCandidateVerdict verdict in vehicle.Verdicts)
                {
                    string demandId = verdict.Evaluation.Candidate.DemandId;
                    _demands.TryGetValue(demandId, out Dictionary<string, PairState>? pairs);
                    if (pairs is null && !StillWaiting(round, demandId))
                    {
                        continue;
                    }

                    PairState? pair = null;
                    if (pairs?.TryGetValue(vehicle.AgvId, out pair) != true)
                    {
                        if (_pairs >= _capacity)
                        {
                            untracked++;
                            continue;
                        }

                        if (pairs is null)
                        {
                            pairs = new Dictionary<string, PairState>(StringComparer.Ordinal);
                            _demands.Add(demandId, pairs);
                        }

                        pair = new PairState();
                        pairs.Add(vehicle.AgvId, pair);
                        _pairs++;
                    }

                    pair!.LastRound = sequence;
                    pair.Area = verdict.Evaluation.Candidate.LiveMesFields?.Area;
                    pair.TaskType = verdict.Evaluation.Candidate.WorkType;
                    if (string.Equals(pair.Code, verdict.ReasonCode, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    transitions.Add(Transition(
                        sequence, round.Now, vehicle.AgvId, demandId, pair.Code, verdict.ReasonCode,
                        verdict.Evaluation.Vehicle, pair));
                    pair.Code = verdict.ReasonCode;
                }
            }

            HashSet<string> inCatalog = new(round.Catalog.Items.Select(item => item.DemandId), StringComparer.Ordinal);
            List<string> left = [];
            foreach ((string demandId, Dictionary<string, PairState> pairs) in _demands)
            {
                bool leaving = !inCatalog.Contains(demandId) || !StillWaiting(round, demandId);
                List<string>? cleared = null;
                foreach ((string agvId, PairState pair) in pairs)
                {
                    if (pair.LastRound == sequence || (!leaving && !completed.ContainsKey(agvId)))
                    {
                        continue;
                    }

                    transitions.Add(Transition(
                        sequence, round.Now, agvId, demandId, pair.Code, null,
                        completed.GetValueOrDefault(agvId), pair));
                    (cleared ??= []).Add(agvId);
                }

                if (leaving)
                {
                    left.Add(demandId);
                    continue;
                }

                foreach (string agvId in cleared ?? [])
                {
                    pairs.Remove(agvId);
                    _pairs--;
                }

                if (pairs.Count == 0)
                {
                    left.Add(demandId);
                }
            }

            foreach (string demandId in left)
            {
                _pairs -= _demands[demandId].Count;
                _demands.Remove(demandId);
            }

            bool justReached = untracked > 0 && !_overCapacity;
            _overCapacity = untracked > 0;
            return new DispatchVerdictTransitionRound(transitions, untracked, justReached, _capacity);
        }
    }

    /// <summary>本轮结束时它是否还在等：同 <see cref="StarvationEscalationSink"/> 的口径，受理被拒的认领仍在等。</summary>
    private static bool StillWaiting(DispatchRoundFacts round, string demandId) =>
        !round.AcceptedDemandIds.Contains(demandId) || round.ClaimsIntakeRefused.Contains(demandId);

    private static DispatchVerdictTransition Transition(
        long sequence,
        DateTimeOffset roundAt,
        string agvId,
        string demandId,
        string? oldCode,
        string? newCode,
        DispatchVehicleFacts? vehicle,
        PairState pair) =>
        new(
            sequence,
            roundAt,
            agvId,
            demandId,
            oldCode,
            newCode,
            vehicle?.Onboard?.SessionGeneration,
            vehicle?.Vehicle.BatteryPercent,
            vehicle?.Vehicle.BatteryState,
            vehicle?.Vehicle.CurrentStationId,
            pair.Area,
            pair.TaskType);

    private sealed class PairState
    {
        public string? Code { get; set; }

        public long LastRound { get; set; }

        public string? Area { get; set; }

        public string TaskType { get; set; } = "";
    }
}

/// <summary>
/// 一对（车, 需求）的判定码变了（control-server#884）。<paramref name="OldReasonCode"/> 为空是第一次判到，<paramref name="NewReasonCode"/>
/// 为空是判定消失（需求离开积压而这辆车这一轮没判它）。车的几样输入取自这一轮这辆车的事实；判定消失且这辆车这一轮没给出结论时为空。
/// 区与任务类型取自这条需求最近一次被判时的目录。
/// </summary>
public sealed record DispatchVerdictTransition(
    long RoundSequence,
    DateTimeOffset RoundAt,
    string AgvId,
    string DemandId,
    string? OldReasonCode,
    string? NewReasonCode,
    long? SessionGeneration,
    int? BatteryPercent,
    string? BatteryState,
    int? CurrentStationId,
    string? Area,
    string TaskType);

/// <summary>一轮比对的结果：变了的那几对，以及因为满了而没跟踪的对数。</summary>
/// <param name="CapacityJustReached">这一轮从「没满」变成「满了、有对没跟踪」：派车轮记一次警告，之后一直满着不再记。</param>
/// <param name="Capacity">上限是多少对，进警告。</param>
public sealed record DispatchVerdictTransitionRound(
    IReadOnlyList<DispatchVerdictTransition> Transitions,
    int Untracked,
    bool CapacityJustReached,
    int Capacity)
{
    public static DispatchVerdictTransitionRound None { get; } = new([], 0, false, 0);
}

/// <summary>判定变化的两条日志（control-server#884）。记在派车轮的类别下（<c>JourneyRuntimeEngine</c>），与它别的事件在一处。</summary>
internal static partial class DispatchVerdictTransitionLogMessages
{
    [LoggerMessage(EventId = 2884, Level = LogLevel.Information,
        Message = "Dispatch verdict for vehicle {AgvId} on demand {DemandId} changed from {OldReasonCode} to {NewReasonCode} " +
                  "(round {RoundSequence} at {RoundAt}); session generation {SessionGeneration}, battery {BatteryPercent}% " +
                  "{BatteryState}, station {CurrentStationId}, area {Area}, task type {TaskType}.")]
    public static partial void LogVerdictChanged(
        ILogger logger,
        string agvId,
        string demandId,
        string oldReasonCode,
        string newReasonCode,
        long roundSequence,
        string roundAt,
        long? sessionGeneration,
        int? batteryPercent,
        string? batteryState,
        int? currentStationId,
        string? area,
        string taskType);

    [LoggerMessage(EventId = 2885, Level = LogLevel.Warning,
        Message = "Dispatch verdict change tracking is full ({Capacity} vehicle/demand pairs); {Untracked} pair(s) this round are not " +
                  "tracked and their verdict changes will not be logged until tracked demands leave the backlog.")]
    public static partial void LogTrackingFull(ILogger logger, int capacity, int untracked);

    /// <summary>空码（第一次判到、判定消失）在日志里写成这个，好按字面搜。</summary>
    public const string NoVerdict = "NONE";

    public static void Log(ILogger logger, DispatchVerdictTransitionRound round)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(round);
        foreach (DispatchVerdictTransition transition in round.Transitions)
        {
            LogVerdictChanged(
                logger,
                transition.AgvId,
                transition.DemandId,
                transition.OldReasonCode ?? NoVerdict,
                transition.NewReasonCode ?? NoVerdict,
                transition.RoundSequence,
                transition.RoundAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                transition.SessionGeneration,
                transition.BatteryPercent,
                transition.BatteryState,
                transition.CurrentStationId,
                transition.Area,
                transition.TaskType);
        }

        if (round.CapacityJustReached)
        {
            LogTrackingFull(logger, round.Capacity, round.Untracked);
        }
    }
}
