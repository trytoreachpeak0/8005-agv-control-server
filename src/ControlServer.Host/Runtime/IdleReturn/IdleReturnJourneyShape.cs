using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.IdleReturn;

/// <summary>
/// 一次空闲返回物化成的三行（批次8-19，control-server#390）：旅程行、开往等待点的那一个停靠、它的订单意图。
/// </summary>
/// <remarks>
/// <para>
/// <b>身份全部从承诺的旅程 id 派生</b>（<see cref="IdleReturnIdentity.JourneyIdFor"/>，形如 <c>idle-return:{vehicleKey}:{时刻}</c>）：
/// 消息 id 与移动段 id 是 <c>StableUuid(journeyId|用途)</c>（<see cref="JourneyPlanBuilder.StableGuid"/>），RIoT 单号是
/// <see cref="UpperIdFor"/>。同一个承诺物化多少次、崩溃后重来多少次，都是同一组 id；两次承诺的旅程 id 不同，id 也就不撞。
/// </para>
/// <para>
/// <b>没有需求，就没有需求才有的列</b>：<c>DemandId</c>、关卡一段、仓位、装卸命令、录入请求、离站核验都为空。等待点这一段放在
/// 取货那几列上（<c>Pickup*</c>）与停靠行上——批次8 建表票（control-server#386）选的「方案 A：空闲返回是一趟没有需求的旅程」。
/// </para>
/// <para>
/// 订单形态经 <see cref="JourneyStopRoles.OrderShapeOf"/>，等待点停靠是单段移动（<c>byDefaultMissions</c> 的一段 <c>move</c>，<c>REQ-0294</c>），
/// 不另起名字。
/// </para>
/// </remarks>
internal static class IdleReturnJourneyShape
{
    /// <summary>订单意图的 <c>Purpose</c>：开往等待点。</summary>
    public const string IntentPurpose = "TO_WAITING_POINT";

    /// <summary>
    /// 旅程行上的派车分区：空闲返回不属于任何派车分区（分区是需求的属性），这一栏只为非空约束写一个一看就知道不是分区的值。
    /// </summary>
    public const string NoDispatchZone = "IDLE_RETURN";

    /// <summary>
    /// 这次空闲返回的 RIoT 单号，从旅程 id 派生：<c>idle-return:BROKERX-0001:20260929T081500123Z</c> →
    /// <c>W2G-IDLE-BROKERX-0001-20260929T081500123Z</c>。与搬运单号（<c>W2G-{需求}-PICKUP-{代次}</c>）同一个前缀、不同的第二段，看一眼就分得开。
    /// </summary>
    public static string UpperIdFor(string journeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        if (!journeyId.StartsWith(IdleReturnIdentity.JourneyIdPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{journeyId}' is not an idle return journey id.", nameof(journeyId));
        }
        return "W2G-IDLE-" + journeyId[IdleReturnIdentity.JourneyIdPrefix.Length..].Replace(':', '-');
    }

    public static (JourneyRuntimeRow Runtime, JourneyStopRow Stop, OrderIntent Intent) Build(
        string journeyId,
        FleetVehicle vehicle,
        int stationId,
        string stationName,
        JourneyRuntimeOptions options,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentException.ThrowIfNullOrWhiteSpace(stationName);
        ArgumentNullException.ThrowIfNull(options);

        string Id(string purpose) => JourneyPlanBuilder.StableGuid(journeyId, purpose);
        string upperId = UpperIdFor(journeyId);
        string legId = Id("waiting-point-leg");
        string operationSessionId = Id("operation-session");
        JourneyRuntimeRow runtime = new()
        {
            JourneyId = journeyId,
            DemandId = null,
            Stage = JourneyRuntimeStage.AwaitingPickupArrival,
            AgvId = vehicle.AgvId,
            VehicleKey = vehicle.VehicleKey,
            AgvLifecycleGeneration = vehicle.AgvLifecycleGeneration,
            MapId = options.MapId,
            MapIdentity = options.MapIdentity,
            DispatchZone = NoDispatchZone,
            RouteEvidenceId = journeyId,
            PickupStationId = stationName,
            PickupStationRiotId = stationId,
            OperationSessionId = operationSessionId,
            PickupMovementLegId = legId,
            PickupUpperId = upperId,
            DispatchGeneration = options.DispatchGeneration,
            VehicleBusinessRevision = 1,
            WorklistRevision = 1,
            PlanRevision = 1,
            VehicleBusinessMessageId = Id("waiting-point-vehicle-state"),
            WorklistMessageId = Id("waiting-point-worklist"),
            PlanMessageId = Id("waiting-point-plan"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        JourneyStopRow stop = new()
        {
            StopId = journeyId + ":waiting-point",
            JourneyId = journeyId,
            Sequence = 1,
            StopRole = JourneyStopRoles.WaitingPoint,
            StationId = stationName,
            StationRiotId = stationId,
            DispatchZone = NoDispatchZone,
            OperationSessionId = operationSessionId,
            MovementLegId = legId,
            UpperId = upperId,
            VehicleBusinessMessageId = runtime.VehicleBusinessMessageId,
            WorklistMessageId = runtime.WorklistMessageId,
            PlanMessageId = runtime.PlanMessageId,
            Status = JourneyStopStatuses.Pending,
            CreatedAt = now,
        };
        OrderIntent intent = new(
            legId,
            null,
            upperId,
            IntentPurpose,
            stationName,
            now,
            vehicle.VehicleKey,
            options.MapId,
            stationId,
            vehicle.AgvLifecycleGeneration,
            options.DispatchGeneration,
            JourneyStopRoles.OrderShapeOf(JourneyStopRoles.WaitingPoint));
        return (runtime, stop, intent);
    }
}
