using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Host.Dashboard;

/// <summary>
/// 车队视图的期待动作超时数据面：每个等操作员等了太久的仓一行（REQ-0358，CP-0005 第三节第 1 条，control-server#142）。
/// </summary>
/// <remarks>
/// <para>
/// 行来自已投影的车载告警里的 <c>SLOT_EXPECTED_ACTION_OVERDUE</c>（<see cref="OnboardAlarmProjectionStore"/>），所以车载端撤下告警，
/// 这一行就跟着消失；车失联时不列它失联前的超时，而是把车列进 <c>unavailableVehicles</c>（REQ-0269）。
/// </para>
/// <para>
/// 每行合并三样东西：告警本身（仓位、期待的动作、越过门槛的时刻）；这台车在途旅程的站点与操作类型，以及它是否同时挂着
/// <c>STATION_TIMEOUT_DOOR_NOT_CLOSED</c>——装货站期限过后两者会同时出现，合成这一行，不另起一行；以及车载端最近一份
/// <c>SafetyStateSnapshot</c> 里这个仓的锁、仓内光幕与开锁输出读数。那份快照是 Host 在告警到达、或超时仓的安全状态变化时
/// 向车要的（<c>OnboardMessageProcessor</c>）；读数之后车又报告这个仓变了、新读数还没到的，如实标出来。
/// </para>
/// <para>
/// 每行还带出这个仓在当前装卸上最近的一次人工判故障（REQ-0359）：状态、判定时刻，车载端拒绝时它给的原因。数据取自判定接口
/// （control-server#383）落的判定记录，这里只读，不另算。判定的入口是看板的「人工判故障」动作（control-server#384），
/// 它把提交转给判定接口；这一行在车载端撤下告警之前一直在，判定状态就标在行上。
/// </para>
/// <para>
/// 只读：不写任何行，不下发任何消息，不改阻塞看板的升级分档。
/// </para>
/// </remarks>
internal sealed class ExpectedActionOverdueQueryEndpoint : IDashboardQueryEndpoint
{
    private readonly ExpectedActionOverdueOptions _options;
    private readonly TimeProvider _clock;

    public ExpectedActionOverdueQueryEndpoint()
        : this(ExpectedActionOverdueOptions.Load(AppContext.BaseDirectory), TimeProvider.System)
    {
    }

    internal ExpectedActionOverdueQueryEndpoint(ExpectedActionOverdueOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _clock = clock;
    }

    public string Path => DashboardQueryEndpointCatalog.QueryPrefix + "expected-action-overdue";

    public async Task<object> ReadAsync(ControlServerDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        DateTimeOffset now = _clock.GetUtcNow();
        IReadOnlyList<VehicleAlarmProjection> projections =
            await new OnboardAlarmProjectionStore(dbContext, _clock).ReadDashboardProjectionAsync(cancellationToken);

        List<object> slots = [];
        foreach (VehicleAlarmProjection vehicle in projections.Where(projection => projection.IsAvailable))
        {
            OnboardAlarmEntry[] overdue = [.. vehicle.Alarms
                .Where(OnboardAlarmCodes.IsSlotExpectedActionOverdue)
                .OrderBy(alarm => alarm.PhysicalSlotNumber)];
            if (overdue.Length == 0)
            {
                continue;
            }
            JourneyContext journey = await JourneyAsync(dbContext, vehicle.AgvId, cancellationToken);
            SafetyReadings readings = await SafetyReadingsAsync(dbContext, vehicle.AgvId, cancellationToken);
            Declarations declarations = await DeclarationsAsync(dbContext, vehicle.AgvId, cancellationToken);
            foreach (OnboardAlarmEntry alarm in overdue)
            {
                int slot = alarm.PhysicalSlotNumber!.Value;
                TimeSpan sinceOverdue = now > alarm.RaisedAt ? now - alarm.RaisedAt : TimeSpan.Zero;
                slots.Add(new
                {
                    agvId = vehicle.AgvId,
                    slotNo = slot,
                    stationId = journey.StationId,
                    operationType = journey.OperationType,
                    expectedAction = alarm.Message,
                    raisedAt = alarm.RaisedAt,
                    waitedSeconds = (long)(_options.Threshold + sinceOverdue).TotalSeconds,
                    stationTimeoutDoorNotClosed = journey.StationTimeoutDoorNotClosed,
                    readings = readings.For(slot),
                    declaration = declarations.For(slot)
                });
            }
        }

        return new
        {
            thresholdSeconds = (long)_options.Threshold.TotalSeconds,
            unavailableVehicles = projections
                .Where(projection => !projection.IsAvailable)
                .Select(projection => new { agvId = projection.AgvId, reason = projection.UnavailableReason })
                .ToArray(),
            slots
        };
    }

    private sealed record JourneyContext(string? StationId, string? OperationType, bool StationTimeoutDoorNotClosed);

    /// <summary>
    /// 这台车在途的那条旅程停在哪个站、在做装货还是卸货。说不出来的（没有在途旅程，或它不在装卸那一段）如实为 null，不猜。
    /// </summary>
    private static async Task<JourneyContext> JourneyAsync(
        ControlServerDbContext dbContext, string agvId, CancellationToken cancellationToken)
    {
        JourneyRuntimeRow[] open = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        JourneyRuntimeRow? runtime = open.OrderByDescending(row => row.CreatedAt).FirstOrDefault();
        if (runtime is null)
        {
            return new JourneyContext(null, null, false);
        }
        bool stationTimeout = string.Equals(
            runtime.BlockReasonCode, JourneyRuntimeEngine.StationTimeoutDoorNotClosedReason, StringComparison.Ordinal);
        return runtime.Stage switch
        {
            JourneyRuntimeStage.AwaitingSublot
                or JourneyRuntimeStage.AwaitingLoadResult
                or JourneyRuntimeStage.AwaitingStationDeparture => new JourneyContext(runtime.PickupStationId, "LOAD", stationTimeout),
            JourneyRuntimeStage.AwaitingUnloadResult => new JourneyContext(runtime.GateStationId, "UNLOAD", stationTimeout),
            _ => new JourneyContext(null, null, stationTimeout)
        };
    }

    /// <summary>
    /// 这台车当前装卸上的人工判故障。「当前装卸」与判定接口认的是同一个（<c>SlotFaultDeclarationService</c>）：在途旅程及其需求的
    /// 各次装卸里还在进行的那一次；没有进行中的，就是最近的那一次——判定生效后那次装卸进 <c>RecoveryRequired</c>，不再进行，
    /// 行上仍要标「已生效」。别的装卸上的判定不替这一行说话。
    /// </summary>
    private static async Task<Declarations> DeclarationsAsync(
        ControlServerDbContext dbContext, string agvId, CancellationToken cancellationToken)
    {
        JourneyRuntimeRow[] journeys = await dbContext.JourneyRuntimes.AsNoTracking()
            .Where(row => row.AgvId == agvId && row.Stage != JourneyRuntimeStage.Completed)
            .ToArrayAsync(cancellationToken);
        string[] journeyIds = [.. journeys.Select(row => row.JourneyId)];
        JourneyDemandRow[] members = await dbContext.Set<JourneyDemandRow>().AsNoTracking()
            .Where(row => journeyIds.Contains(row.JourneyId) && row.RemovedAt == null)
            .ToArrayAsync(cancellationToken);
        string[] attempts =
        [
            .. journeys.SelectMany(row => new[] { row.LoadSlotOperationAttemptId, row.UnloadSlotOperationAttemptId })
                .Concat(members.SelectMany(row => new[] { row.LoadSlotOperationAttemptId, row.UnloadSlotOperationAttemptId }))
                .Where(attempt => attempt is not null)
                .Select(attempt => attempt!)
                .Distinct(StringComparer.Ordinal)
        ];
        StationOperationRow[] operations = await dbContext.StationOperations.AsNoTracking()
            .Where(row => attempts.Contains(row.SlotOperationAttemptId))
            .ToArrayAsync(cancellationToken);
        // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
        StationOperationRow[] latestFirst = [.. operations.OrderByDescending(row => row.CreatedAt)];
        StationOperationRow? current = latestFirst.FirstOrDefault(row => row.Status == StationOperationStatus.Prepared)
                                       ?? latestFirst.FirstOrDefault();
        if (current is null)
        {
            return Declarations.None;
        }
        SlotFaultDeclarationRow[] rows = await dbContext.Set<SlotFaultDeclarationRow>().AsNoTracking()
            .Where(row => row.AgvId == agvId && row.SlotOperationAttemptId == current.SlotOperationAttemptId)
            .ToArrayAsync(cancellationToken);
        return new Declarations(rows);
    }

    private sealed class Declarations(IReadOnlyList<SlotFaultDeclarationRow> rows)
    {
        public static Declarations None { get; } = new([]);

        /// <summary>这个仓最近的一次判定；没有时为 null，看板给出判定入口。</summary>
        public object? For(int slot)
        {
            // Ordered in memory: SQLite cannot ORDER BY a DateTimeOffset.
            SlotFaultDeclarationRow? latest = rows
                .Where(row => row.SlotNo == slot)
                .OrderByDescending(row => row.DeclaredAt)
                .FirstOrDefault();
            if (latest is null)
            {
                return null;
            }
            string? reasonCode = null;
            string? displayMessage = null;
            if (latest.ResultProblemJson is string problemJson)
            {
                using JsonDocument problem = JsonDocument.Parse(problemJson);
                reasonCode = problem.RootElement.TryGetProperty("reasonCode", out JsonElement code)
                             && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
                displayMessage = problem.RootElement.TryGetProperty("displayMessage", out JsonElement message)
                                 && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
            }
            return new
            {
                state = latest.State,
                declaredAt = latest.DeclaredAt,
                administratorId = latest.AdministratorId,
                faultCategory = latest.FaultCategory,
                resultReceivedAt = latest.ResultReceivedAt,
                reasonCode,
                displayMessage
            };
        }
    }

    /// <summary>
    /// 车载端在本会话里最近一份 <c>SafetyStateSnapshot</c> 的仓位读数，以及它之后车报告过哪些仓又变了。
    /// </summary>
    /// <remarks>
    /// 「最近」按 <c>safetyStateVersion</c> 比：同一会话里它单调前进（中途快照取下一个版本，hmi#109），不依赖任何一端的时钟。
    /// 只认会话行上那一代；上一代的读数再新也不替这一代说话。
    /// </remarks>
    private static async Task<SafetyReadings> SafetyReadingsAsync(
        ControlServerDbContext dbContext, string agvId, CancellationToken cancellationToken)
    {
        long? generation = await dbContext.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == agvId)
            .Select(row => (long?)row.SessionGeneration)
            .FirstOrDefaultAsync(cancellationToken);
        if (generation is null)
        {
            return SafetyReadings.None;
        }
        string[] rows = await dbContext.ProtocolInbox.AsNoTracking()
            .Where(row => row.MessageType == "SafetyStateSnapshot" || row.MessageType == "SafetyStateChanged")
            .Select(row => row.RequestJson)
            .ToArrayAsync(cancellationToken);

        JsonElement? latestSnapshot = null;
        long latestVersion = long.MinValue;
        List<(long Version, int[] Slots)> changes = [];
        foreach (string json in rows)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("agvId", out JsonElement agv) || agv.GetString() != agvId
                || !root.TryGetProperty("sessionGeneration", out JsonElement session)
                || session.ValueKind != JsonValueKind.Number || session.GetInt64() != generation)
            {
                continue;
            }
            JsonElement payload = root.GetProperty("payload");
            long version = payload.GetProperty("safetyStateVersion").GetInt64();
            if (root.GetProperty("messageType").GetString() == "SafetyStateSnapshot")
            {
                if (version > latestVersion)
                {
                    latestVersion = version;
                    latestSnapshot = payload.Clone();
                }
            }
            else if (payload.TryGetProperty("affectedSlots", out JsonElement affected)
                     && affected.ValueKind == JsonValueKind.Array)
            {
                changes.Add((version, [.. affected.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Number)
                    .Select(item => item.GetInt32())]));
            }
        }
        return latestSnapshot is JsonElement snapshot
            ? new SafetyReadings(snapshot, latestVersion, changes)
            : SafetyReadings.None;
    }

    private sealed class SafetyReadings(JsonElement? snapshot, long version, IReadOnlyList<(long Version, int[] Slots)> changes)
    {
        public static SafetyReadings None { get; } = new(null, 0, []);

        /// <summary>这个仓的读数；没有快照、或快照里没有这个仓时为 null，看板显示「尚无读数」。</summary>
        public object? For(int slot)
        {
            if (snapshot is not JsonElement payload
                || !payload.TryGetProperty("slotStates", out JsonElement slotStates)
                || slotStates.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (JsonElement state in slotStates.EnumerateArray())
            {
                if (state.TryGetProperty("slotNo", out JsonElement number) && number.GetInt32() == slot)
                {
                    return new
                    {
                        observedAt = payload.GetProperty("observedAt").GetDateTimeOffset(),
                        safetyStateVersion = version,
                        lockState = state.GetProperty("lockState").GetString(),
                        physicalState = state.GetProperty("physicalState").GetString(),
                        unlockOutputState = state.GetProperty("unlockOutputState").GetString(),
                        changedSinceObserved = changes.Any(change => change.Version > version && change.Slots.Contains(slot))
                    };
                }
            }
            return null;
        }
    }
}
