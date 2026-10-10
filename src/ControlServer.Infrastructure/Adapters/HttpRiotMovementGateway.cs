using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Kiota.Abstractions;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// ControlServer's fail-closed domain adapter over the versioned RIoT SDK. The SDK owns
/// transport, authentication, endpoint serialization, and response parsing; this adapter
/// owns only ControlServer observation semantics.
/// </summary>
public sealed class HttpRiotMovementGateway : IRiotMovementGateway, IRiotVehicleFacts, IRiotMapStationCatalog,
    IRiotMapNameCatalog, IRiotVehicleSafetyFacts, IVehicleMotionFacts, IRiotVehicleOrderFacts, IRiotOrderListingFacts,
    IRiotOrderMissionFacts, IOnboardVehicleSafetyProjection
{
    /// <summary>
    /// The order states that are not an ending: QUEUEING 1, EXECUTING 3, PAUSED 7, SUSPENDED 8, HANG 9 and QUEUE_PRIORITY 10 (the
    /// SDK's <c>OrderRecordObject.orderState</c>: 10 is "队列优先执行", a queued order moved to the front). 8 and 10 were missing until
    /// control-server#404's second review (L-2): 10 is a queue state like 1, and 8 -- labelled "已移除" but never observed, and
    /// counted as occupying the vehicle by REQ-0164 -- is not one of the four explicit endings
    /// (<c>ForeignRunningOrders.IsExplicitlyEnded</c>). Left out, a vehicle with such an order read as having none.
    /// </summary>
    private static readonly int[] NonFinalOrderStates = [1, 3, 7, 8, 9, 10];

    /// <summary>
    /// The <c>movementState</c> values this server is prepared to read as "not moving".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closed list, and short on purpose. Seven values have been observed in the behaviour lab —
    /// <c>MT_RUNNING</c>, <c>MT_FINISHED</c>, <c>MT_NA</c>, <c>MT_PAUSED</c>,
    /// <c>MT_WAIT_FOR_CHECKPOINT</c>, <c>MT_WAIT_FOR_START</c> and <c>MT_IN_CANCEL</c> — and only
    /// the two here are a positive statement that motion has stopped. <c>MT_NA</c> is an absence.
    /// The other three describe a vehicle in the middle of something: waiting at a checkpoint,
    /// waiting to start, cancelling. None of them rules out a vehicle that is still rolling, and
    /// REQ-0247 needs a fact that does.
    /// </para>
    /// <para>
    /// <b>Adding a value here weakens a safety proof.</b> Anything not on this list reads as
    /// <see cref="VehicleMotionReading.Unknown"/>, which blocks the stop proof rather than granting
    /// it, so an incomplete list costs an unnecessary escalation — the direction REQ-0246 asks to
    /// err in.
    /// </para>
    /// </remarks>
    private static readonly string[] NotMovingStates = ["MT_FINISHED", "MT_PAUSED"];

    /// <summary>Page size for the non-final order listing; 100 is what every read used before paging (control-server#525).</summary>
    private const int NonFinalOrderPageSize = 100;

    /// <summary>
    /// The most pages one non-final order read follows (control-server#525). The real RIoT held 252 non-final orders on
    /// 2026-10-09 (3 pages), nearly all of them other plant lines' state-8 orders accumulated since 2026-09-09; 20 pages is
    /// 2000 orders, about eight times that, before the read gives up and answers incomplete. Past it the answer stays the
    /// fail-closed one every caller already handles, rather than an unbounded burst of requests on every poll.
    /// </summary>
    private const int NonFinalOrderPageCap = 20;

    /// <summary>Placeholder RIoT reports in executeVehicleKey before a vehicle is bound.</summary>
    private const string UnassignedVehicleKeyPlaceholder = "--";
    private readonly RiotSession riotSession;
    private readonly TimeProvider timeProvider;

    public HttpRiotMovementGateway(RiotSession riotSession)
        : this(riotSession, TimeProvider.System)
    {
    }

    [ActivatorUtilitiesConstructor]
    public HttpRiotMovementGateway(RiotSession riotSession, TimeProvider timeProvider)
    {
        this.riotSession = riotSession;
        this.timeProvider = timeProvider;
    }

    /// <summary>The act a charging order carries after its move to the charger (allowlist 1.2, shape two).</summary>
    private static readonly OrderMissionAction StartChargingAction = new(
        RiotChargingOrderAction.ActionId,
        RiotChargingOrderAction.StartChargingParam1,
        RiotChargingOrderAction.Param2);

    /// <summary>RIoT business code for "订单已存在" on byDefaultMissions (BC-ORDER-004).</summary>
    private const string OrderAlreadyExistsBusinessCode = "0610008";

    public async Task<RiotOrderObservation> ReconcileByUpperIdAsync(
        string upperId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upperId);
        try
        {
            OrderLookupResult lookup = await riotSession.Order.FindOrderByUpperIdAsync(
                upperId, cancellationToken).ConfigureAwait(false);
            return lookup.Status switch
            {
                OrderLookupStatus.NotFound =>
                    new RiotOrderObservation(
                        upperId,
                        RiotOrderObservationKind.NotFound,
                        null,
                        Receipt: Receipt("RECONCILE", "NotFound", httpStatusCode: 404, resultPresent: false)),
                OrderLookupStatus.Found when lookup.Order is not null =>
                    ToObservation(
                        upperId,
                        lookup.Order,
                        Receipt("RECONCILE", "Found", resultPresent: true)),
                OrderLookupStatus.AbsentAtObservation => Unknown(
                    upperId,
                    Receipt("RECONCILE", "AbsentAtObservation", resultPresent: false)),
                OrderLookupStatus.Indeterminate => Unknown(
                    upperId,
                    Receipt("RECONCILE", "Indeterminate", resultPresent: true)),
                _ => Unknown(upperId, Receipt("RECONCILE", "Unknown"))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return Unknown(upperId, FailureReceipt("RECONCILE", error));
        }
    }

    public async Task<RiotOrderObservation> CreateAsync(
        OrderIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateFrozenIntent(intent);
        bool charge = string.Equals(intent.OrderShape, OrderShapes.Charge, StringComparison.Ordinal);
        if (!charge && !string.Equals(intent.OrderShape, OrderShapes.SingleMove, StringComparison.Ordinal))
        {
            // The shape column carries no CHECK (#399), so this is where an unknown value stops. Nothing is
            // sent: guessing a shape would create an order the intent never asked for.
            return Unknown(
                intent.UpperId,
                Receipt("CREATE", "UnsupportedOrderShape", resultPresent: false, failureCategory: "IDENTITY_INVALID"));
        }

        try
        {
            // The shape is read from the frozen intent on every attempt, so a retry creates exactly what the
            // first attempt did, whatever RIoT or the vehicle reported in between.
            OrderRef created = charge
                ? await riotSession.Order.CreateMoveOrderAsync(
                    intent.UpperId,
                    intent.VehicleKey,
                    intent.MapId,
                    intent.DestinationStationId,
                    StartChargingAction,
                    intent.UpperId,
                    cancellationToken).ConfigureAwait(false)
                : await riotSession.Order.CreateMoveOrderAsync(
                    intent.UpperId,
                    intent.VehicleKey,
                    intent.MapId,
                    intent.DestinationStationId,
                    intent.UpperId,
                    cancellationToken).ConfigureAwait(false);

            RiotOrderObservationKind kind = ToObservationKind(created.OrderState);
            if (kind == RiotOrderObservationKind.Unknown ||
                string.IsNullOrWhiteSpace(created.OrderId) ||
                !string.Equals(created.UpperId, intent.UpperId, StringComparison.Ordinal))
            {
                return Unknown(
                    intent.UpperId,
                    Receipt("CREATE", "SdkIndeterminate", resultPresent: true, failureCategory: "IDENTITY_INVALID"));
            }

            return new RiotOrderObservation(
                intent.UpperId,
                kind,
                created.OrderId,
                created.OrderState,
                intent.VehicleKey,
                intent.MapId,
                intent.DestinationStationId,
                Receipt("CREATE", "SdkAccepted", resultPresent: true));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RiotApiException error) when (error.BusinessCode == OrderAlreadyExistsBusinessCode)
        {
            // BC-ORDER-004: RIoT enforces upperId idempotency server-side. "订单已存在" is a
            // definitive statement that no second order was created; the existing order object
            // is deliberately not returned, so the caller reconciles by upperId for its state.
            return new RiotOrderObservation(
                intent.UpperId,
                RiotOrderObservationKind.AlreadyExists,
                null,
                Receipt: Receipt(
                    "CREATE",
                    "OrderAlreadyExists",
                    httpStatusCode: 200,
                    businessCode: OrderAlreadyExistsBusinessCode,
                    resultPresent: false));
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return Unknown(intent.UpperId, FailureReceipt("CREATE", error));
        }
    }

    public async Task<RiotVehicleObservation> ReadVehicleAsync(
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        try
        {
            VehicleCard vehicle = await riotSession.Tasks.GetVehicleCardAsync(
                vehicleKey, cancellationToken).ConfigureAwait(false);
            DateTimeOffset observedAt = timeProvider.GetUtcNow();
            return new RiotVehicleObservation(
                vehicleKey,
                Connected: vehicle.Status == 1,
                Enabled: vehicle.Enable == true,
                ProcState: vehicle.ProcState ?? "UNKNOWN",
                CurrentMap: vehicle.CurrentMap ?? string.Empty,
                CurrentStationId: vehicle.CurrentPosition,
                BatteryPercent: vehicle.BatteryPercent,
                BatteryState: vehicle.BatteryState,
                Speed: vehicle.Speed,
                ObservedAt: observedAt,
                LockStatus: vehicle.LockStatus,
                OrderTaskId: vehicle.OrderTaskId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return UnknownVehicle(vehicleKey);
        }
    }

    public async Task<RiotMapStationCatalogSnapshot> ReadMapStationsAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);

        IReadOnlyList<Station> sdkStations;
        try
        {
            sdkStations = await riotSession.Maps.ListStationsStrictAsync(
                mapId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            throw new InvalidDataException("RIoT Map station catalog response was not valid.", error);
        }

        RiotMapStation[] stations = sdkStations.Select(station =>
            new RiotMapStation(
                station.MapId == mapId && station.StationId > 0
                    ? station.StationId
                    : throw new InvalidDataException("RIoT Map station identity is invalid."),
                string.IsNullOrWhiteSpace(station.Name)
                    ? throw new InvalidDataException("RIoT Map station name is required.")
                    : station.Name)).OrderBy(station => station.StationId).ToArray();
        if (stations.Length == 0 || stations.Select(station => station.StationId).Distinct().Count() != stations.Length)
        {
            throw new InvalidDataException("RIoT Map station catalog must be non-empty with unique station ids.");
        }

        string canonical = string.Join('\n', stations.Select(station =>
            $"{mapId}\t{station.StationId}\t{station.StationName}"));
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        DateTimeOffset observedAt = timeProvider.GetUtcNow();
        return new RiotMapStationCatalogSnapshot(mapId, observedAt, fingerprint, stations);
    }

    /// <summary>
    /// RIoT's Map list without any <c>mapJson</c> (control-server#186; verified on the real RIoT 2026-09-28: 2 KB for eight
    /// Maps, where <c>mapInfo/{mapId}</c> is some 260 KB for one). Fails closed: an answer that is not a non-empty list
    /// throws rather than coming back empty, because an empty list would read as every Map being absent.
    /// </summary>
    public async Task<RiotMapNameListing> ReadMapNamesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Map> maps;
        try
        {
            maps = await riotSession.Maps.ListMapsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            throw new InvalidDataException("RIoT Map list response was not valid.", error);
        }

        // The SDK drops entries without an id or a name; what is left must still be something.
        if (maps.Count == 0)
        {
            throw new InvalidDataException("RIoT Map list must be non-empty.");
        }
        return new RiotMapNameListing(
            timeProvider.GetUtcNow(),
            [.. maps.Select(map => new RiotMapName(map.MapId, map.Name))]);
    }

    public async Task<RiotVehicleSafetyObservation> ReadVehicleSafetyAsync(
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        try
        {
            VehicleExecutionFacts vehicle = await riotSession.Tasks.GetVehicleExecutionFactsAsync(
                vehicleKey, cancellationToken).ConfigureAwait(false);
            NonFinalOrderRead orders = await ReadAllNonFinalOrdersAsync(cancellationToken).ConfigureAwait(false);
            if (!orders.IsComplete)
            {
                return UnknownSafety(vehicleKey, "RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN");
            }

            return Safety(vehicleKey, vehicle, orders.Records);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsTimeout(error))
        {
            return UnknownSafety(vehicleKey, "RIOT_READ_TIMEOUT");
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return UnknownSafety(vehicleKey, "RIOT_READ_FAILED");
        }
    }

    public async Task<RiotVehicleSafetyObservation> ReadForOnboardAsync(
        string vehicleKey,
        int listingRereads,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        ArgumentOutOfRangeException.ThrowIfNegative(listingRereads);
        try
        {
            VehicleExecutionFacts vehicle = await riotSession.Tasks.GetVehicleExecutionFactsAsync(
                vehicleKey, cancellationToken).ConfigureAwait(false);
            // Each read is a whole one, judged alone by control-server#525's rule; pages of two reads are never put together.
            for (int read = 0; read <= listingRereads; read++)
            {
                NonFinalOrderRead orders = await ReadAllNonFinalOrdersAsync(cancellationToken).ConfigureAwait(false);
                if (orders.IsComplete)
                {
                    return Safety(vehicleKey, vehicle, orders.Records);
                }
            }
            return UnknownSafety(vehicleKey, "RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsTimeout(error))
        {
            return UnknownSafety(vehicleKey, "RIOT_READ_TIMEOUT");
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return UnknownSafety(vehicleKey, "RIOT_READ_FAILED");
        }
    }

    /// <summary>The safety predicate over one vehicle read and one complete non-final order listing.</summary>
    private RiotVehicleSafetyObservation Safety(
        string vehicleKey,
        VehicleExecutionFacts vehicle,
        IReadOnlyList<OrderStateRecord> nonFinalOrders)
    {
        bool hasNonFinalOrder = nonFinalOrders.Any(order =>
            string.Equals(order.AppointVehicleKey, vehicleKey, StringComparison.Ordinal) ||
            string.Equals(order.ExecuteVehicleKey, vehicleKey, StringComparison.Ordinal));
        if ((vehicle.Speed is not null && vehicle.Speed != 0) ||
            string.Equals(vehicle.MovementState, "MT_RUNNING", StringComparison.Ordinal))
        {
            return new RiotVehicleSafetyObservation(
                vehicleKey,
                RiotVehicleMotionState.Moving,
                timeProvider.GetUtcNow(),
                "RIOT_BEHAVIOR_LAB_R41",
                ["RIOT_MOTION_ACTIVE"]);
        }

        List<string> reasons = [];
        if (!string.Equals(vehicle.ProcState, "IDLE", StringComparison.Ordinal)) reasons.Add("RIOT_PROC_NOT_IDLE");
        if (vehicle.ProcessingOrder != false) reasons.Add("RIOT_PROCESSING_ORDER_UNKNOWN_OR_ACTIVE");
        if (vehicle.Enable != true) reasons.Add("RIOT_VEHICLE_NOT_ENABLED");
        if (!string.Equals(vehicle.IntegrationLevel, "ON_LINE", StringComparison.Ordinal)) reasons.Add("RIOT_VEHICLE_NOT_ONLINE");
        if (!string.Equals(vehicle.EmergencyState, "OK", StringComparison.Ordinal)) reasons.Add("RIOT_EMERGENCY_NOT_OK");
        if (!string.Equals(vehicle.BreakSwitchState, "MOVABLE", StringComparison.Ordinal)) reasons.Add("RIOT_BRAKE_NOT_MOVABLE");
        if (!string.Equals(vehicle.ControlState, "CONTROL_STATE_OK", StringComparison.Ordinal)) reasons.Add("RIOT_CONTROL_NOT_OK");
        if (!string.Equals(vehicle.LocationState, "LOCATION_STATE_RUNNING", StringComparison.Ordinal)) reasons.Add("RIOT_LOCATION_NOT_RUNNING");
        if (vehicle.Speed is null || vehicle.Speed != 0) reasons.Add("RIOT_SPEED_NOT_ZERO");
        if (!string.Equals(vehicle.MovementState, "MT_FINISHED", StringComparison.Ordinal)) reasons.Add("RIOT_MOVEMENT_NOT_FINISHED");
        if (hasNonFinalOrder) reasons.Add("RIOT_NONFINAL_ORDER_PRESENT");

        return new RiotVehicleSafetyObservation(
            vehicleKey,
            reasons.Count == 0 ? RiotVehicleMotionState.Stopped : RiotVehicleMotionState.Unknown,
            timeProvider.GetUtcNow(),
            "RIOT_BEHAVIOR_LAB_R41",
            reasons);
    }

    /// <summary>
    /// The unfinished orders RIoT holds for one vehicle, for REQ-0356's "no release while the
    /// vehicle still has an unfinished order".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same read, the same state list and the same vehicle match as the safety read's
    /// <c>RIOT_NONFINAL_ORDER_PRESENT</c>, so the two cannot come to disagree about whether a
    /// vehicle has an order. An order counts for the vehicle when either the appointed or the
    /// executing key is its own: a QUEUEING order appointed to it has not been bound yet and will
    /// still drive it.
    /// </para>
    /// <para>
    /// A page that does not cover every record, and every failure, is an unknown answer rather than
    /// an exception or an empty list. "No unfinished order" has to be something RIoT said.
    /// </para>
    /// </remarks>
    public async Task<RiotVehicleOrderObservation> ReadUnfinishedOrdersAsync(
        string deviceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        try
        {
            NonFinalOrderRead orders = await ReadAllNonFinalOrdersAsync(cancellationToken).ConfigureAwait(false);
            if (!orders.IsComplete)
            {
                return new RiotVehicleOrderObservation(deviceKey, null, [], timeProvider.GetUtcNow());
            }

            var unfinished = orders.Records
                .Where(order =>
                    string.Equals(order.AppointVehicleKey, deviceKey, StringComparison.Ordinal) ||
                    string.Equals(order.ExecuteVehicleKey, deviceKey, StringComparison.Ordinal))
                .ToArray();
            string[] ids = [.. unfinished.Select(order => order.OrderId)];
            // A record listed twice keeps the state of neither: StateOf then answers null, which reads as "not shown PAUSED".
            Dictionary<string, int?> states = unfinished
                .Where(order => order.OrderId is not null)
                .GroupBy(order => order.OrderId!, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single().OrderState, StringComparer.Ordinal);
            return new RiotVehicleOrderObservation(
                deviceKey, ids.Length > 0, ids, timeProvider.GetUtcNow(), states);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return new RiotVehicleOrderObservation(deviceKey, null, [], timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Every order RIoT holds in a non-final state (<see cref="NonFinalOrderStates"/>), whichever vehicle it is for
    /// (control-server#330).
    /// </summary>
    /// <remarks>
    /// The same read and the same state list as <see cref="ReadUnfinishedOrdersAsync"/> and the safety read's
    /// <c>RIOT_NONFINAL_ORDER_PRESENT</c>, but unfiltered and with each order's own fields: which of them is running on a
    /// vehicle of this server's, and whose it is, is the caller's question. A page that does not cover every record, and
    /// every failure, is an incomplete listing rather than an exception or an empty one.
    /// </remarks>
    public async Task<RiotUnfinishedOrderListing> ListUnfinishedOrdersAsync(CancellationToken cancellationToken)
    {
        try
        {
            NonFinalOrderRead orders = await ReadAllNonFinalOrdersAsync(cancellationToken).ConfigureAwait(false);
            if (!orders.IsComplete)
            {
                return new RiotUnfinishedOrderListing(false, [], timeProvider.GetUtcNow());
            }

            RiotListedOrder[] listed = [.. orders.Records
                .Where(order => !string.IsNullOrWhiteSpace(order.OrderId))
                .Select(order => new RiotListedOrder(
                    order.OrderId!,
                    order.UpperId,
                    order.OrderState,
                    order.AppointVehicleKey,
                    order.ExecuteVehicleKey))];
            // A record without an orderId cannot be addressed, re-read or cancelled; one that is there all the same makes the
            // listing something this server cannot fully account for.
            return new RiotUnfinishedOrderListing(
                listed.Length == orders.Records.Count, listed, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return new RiotUnfinishedOrderListing(false, [], timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Every order RIoT holds in a non-final state, read page by page until the listing is covered (control-server#525).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SDK's <see cref="OrderStatePage.CoversAllRecords"/> holds only for a first page that carries the whole listing,
    /// so coverage across pages is proven here: every page must answer the page asked for, report the same total as the
    /// first, and carry exactly the records that page should hold (a full page, then the remainder), and no record id may
    /// appear twice. Together the pages then add up to the first page's total.
    /// </para>
    /// <para>
    /// A total that changes between pages, a page that is short, long or out of place, a repeated record, or a listing
    /// past <see cref="NonFinalOrderPageCap"/> pages is incomplete -- never stitched into a snapshot RIoT never held. The
    /// callers poll, so the next read starts over. What paging cannot prove is a listing that changed without its total
    /// changing (one order leaving while another arrives between two page reads); RIoT offers no snapshot to page over, so
    /// that residue is accepted rather than closed.
    /// </para>
    /// </remarks>
    private async Task<NonFinalOrderRead> ReadAllNonFinalOrdersAsync(CancellationToken cancellationToken)
    {
        OrderStatePage first = await riotSession.Order.ListOrdersByStatesAsync(
            NonFinalOrderStates,
            pageNum: 1,
            pageSize: NonFinalOrderPageSize,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (first.CoversAllRecords)
        {
            return new NonFinalOrderRead(true, first.Records);
        }
        if (first.Current != 1 || first.Total is not long total || total < 0 ||
            (first.Size.HasValue && first.Size.Value != NonFinalOrderPageSize))
        {
            return NonFinalOrderRead.Incomplete;
        }

        long pageCount = (total + NonFinalOrderPageSize - 1) / NonFinalOrderPageSize;
        if (pageCount > NonFinalOrderPageCap)
        {
            return NonFinalOrderRead.Incomplete;
        }

        List<OrderStateRecord> records = [];
        HashSet<long> seenIds = [];
        for (int pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            OrderStatePage page = pageNum == 1
                ? first
                : await riotSession.Order.ListOrdersByStatesAsync(
                    NonFinalOrderStates,
                    pageNum: pageNum,
                    pageSize: NonFinalOrderPageSize,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            long expectedCount = Math.Min(NonFinalOrderPageSize, total - ((long)(pageNum - 1) * NonFinalOrderPageSize));
            if (page.Current != pageNum || page.Total != total || page.Records.Count != expectedCount ||
                (page.Size.HasValue && page.Size.Value != NonFinalOrderPageSize))
            {
                return NonFinalOrderRead.Incomplete;
            }
            foreach (OrderStateRecord record in page.Records)
            {
                if (record.Id is long id && !seenIds.Add(id))
                {
                    return NonFinalOrderRead.Incomplete;
                }
                records.Add(record);
            }
        }
        return new NonFinalOrderRead(true, records);
    }

    private sealed record NonFinalOrderRead(bool IsComplete, IReadOnlyList<OrderStateRecord> Records)
    {
        public static NonFinalOrderRead Incomplete { get; } = new(false, []);
    }

    /// <summary>
    /// One order's state by its RIoT <c>orderId</c>, through <c>detailByOrderId</c> (control-server#330). Null state on every
    /// failure: a read that did not answer says nothing about whether the order ended.
    /// </summary>
    public async Task<RiotOrderStateReading> ReadOrderStateAsync(string orderId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        try
        {
            OrderRef order = await riotSession.Order.GetOrderByOrderIdAsync(orderId, cancellationToken)
                .ConfigureAwait(false);
            return new RiotOrderStateReading(
                orderId,
                string.Equals(order.OrderId, orderId, StringComparison.Ordinal) ? order.OrderState : null,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return new RiotOrderStateReading(orderId, null, timeProvider.GetUtcNow());
        }
    }

    public async Task<RiotOrderMissionFacts> ReadOrderMissionFactsAsync(
        string upperId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upperId);
        try
        {
            OrderLookupResult lookup = await riotSession.Order.FindOrderByUpperIdAsync(
                upperId, cancellationToken).ConfigureAwait(false);
            DateTimeOffset observedAt = timeProvider.GetUtcNow();
            return lookup.Status switch
            {
                OrderLookupStatus.Found when lookup.Order is { } order &&
                    string.Equals(order.UpperId, upperId, StringComparison.Ordinal) =>
                    new RiotOrderMissionFacts(
                        upperId,
                        RiotOrderMissionFactsStatus.Found,
                        order.OrderId,
                        order.OrderState,
                        order.Missions
                            .Select(mission => new RiotOrderMissionFact(
                                mission.Type,
                                mission.MapId,
                                mission.Destination,
                                mission.ActionId,
                                mission.ActionParam1,
                                mission.ActionParam2,
                                mission.ResultCode))
                            .ToArray(),
                        observedAt),
                OrderLookupStatus.NotFound => new RiotOrderMissionFacts(
                    upperId, RiotOrderMissionFactsStatus.NotFound, null, null, [], observedAt),
                _ => UnknownMissionFacts(upperId, observedAt)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return UnknownMissionFacts(upperId, timeProvider.GetUtcNow());
        }
    }

    private static RiotOrderMissionFacts UnknownMissionFacts(string upperId, DateTimeOffset observedAt) =>
        new(upperId, RiotOrderMissionFactsStatus.Unknown, null, null, [], observedAt);

    /// <summary>
    /// One motion-and-position sample for REQ-0247's combined stop proof.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two reads because RIoT publishes the two halves separately: <c>getVehicleInfo</c> carries
    /// <c>movementState</c> and the speed, the vehicle card carries the Map and the station. Both
    /// are already on the allowlist and already called from this adapter.
    /// </para>
    /// <para>
    /// <b>The sample is stamped after both reads, not between them.</b> Two calls take time, and a
    /// vehicle can move across them; timestamping at the end means the freshness check treats the
    /// sample as no newer than its oldest half. Every failure produces an
    /// <see cref="VehicleMotionReading.Unknown"/> sample rather than an exception, because
    /// "RIoT could not be asked" is a fact the stop proof has to weigh, not an error the caller
    /// should have to catch.
    /// </para>
    /// </remarks>
    public async Task<VehicleMotionSample> SampleMotionAsync(
        string deviceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceKey);
        try
        {
            VehicleExecutionFacts execution = await riotSession.Tasks.GetVehicleExecutionFactsAsync(
                deviceKey, cancellationToken).ConfigureAwait(false);
            VehicleCard card = await riotSession.Tasks.GetVehicleCardAsync(
                deviceKey, cancellationToken).ConfigureAwait(false);

            return new VehicleMotionSample(
                deviceKey,
                ReadMotion(execution.MovementState, execution.Speed),
                execution.MovementState,
                execution.Speed,
                card.CurrentMap,
                card.CurrentPosition,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (RiotCallFailureClassification.IsSdkFailure(error))
        {
            return new VehicleMotionSample(
                deviceKey,
                VehicleMotionReading.Unknown,
                MovementState: null,
                Speed: null,
                CurrentMap: null,
                CurrentStationId: null,
                timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// What one <c>movementState</c> and speed say about motion.
    /// </summary>
    /// <remarks>
    /// Moving wins over the state list: a non-zero speed is motion whatever the state field says,
    /// and a speed RIoT did not report cannot be part of a proof that the vehicle is still.
    /// </remarks>
    public static VehicleMotionReading ReadMotion(string? movementState, double? speed)
    {
        if (speed is not null && speed != 0)
        {
            return VehicleMotionReading.Moving;
        }

        if (string.Equals(movementState, "MT_RUNNING", StringComparison.Ordinal))
        {
            return VehicleMotionReading.Moving;
        }

        if (speed is null || movementState is null)
        {
            return VehicleMotionReading.Unknown;
        }

        return NotMovingStates.Contains(movementState, StringComparer.Ordinal)
            ? VehicleMotionReading.NotMoving
            : VehicleMotionReading.Unknown;
    }

    private static RiotOrderObservation ToObservation(
        string expectedUpperId,
        OrderSnapshot order,
        RiotOrderCallReceipt receipt)
    {
        RiotOrderObservationKind kind = ToObservationKind(order.OrderState);
        string? vehicleKey = IsAssignedVehicleKey(order.ExecuteVehicleKey)
            ? order.ExecuteVehicleKey
            : order.AppointVehicleKey;
        OrderMissionSnapshot[] movements = order.Missions
            .Where(mission => string.Equals(mission.Type, "move", StringComparison.Ordinal))
            .ToArray();
        if (kind == RiotOrderObservationKind.Unknown ||
            string.IsNullOrWhiteSpace(order.OrderId) ||
            !string.Equals(order.UpperId, expectedUpperId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(vehicleKey) ||
            movements.Length == 0)
        {
            return Unknown(expectedUpperId, receipt with
            {
                Classification = "Indeterminate",
                FailureCategory = "IDENTITY_INVALID"
            });
        }

        // RIoT expands an order whose target station has an enter_exit point into several moves: a
        // charging order to 211 on maps 25 and 26 reads back as move(212) -> move(211) -> act(78,1,0),
        // and the order after it may start with RIoT's own act(78,2,0). Act missions never decide the
        // destination. With several moves the last one is the destination, and only when the order's
        // endStationNo says the same: an order whose moves end somewhere other than where RIoT says it
        // ends is not evidence of anything (the MVP rule of 3c9ced4). This holds for every order shape,
        // not just charging: a transport order after leaving the charger may be expanded the same way.
        OrderMissionSnapshot movement = movements[^1];
        if (movement.MapId is not > 0 ||
            (movements.Length > 1 &&
             (order.EndStationNo is not > 0 || movement.Destination != order.EndStationNo)))
        {
            return Unknown(expectedUpperId, receipt with
            {
                Classification = "Indeterminate",
                FailureCategory = "IDENTITY_INVALID"
            });
        }

        int? destination = movement.Destination ?? order.EndStationNo;
        if (destination is not > 0)
        {
            return Unknown(expectedUpperId, receipt with
            {
                Classification = "Indeterminate",
                FailureCategory = "DESTINATION_MISSING"
            });
        }

        return new RiotOrderObservation(
            expectedUpperId,
            kind,
            order.OrderId,
            order.OrderState,
            vehicleKey,
            movement.MapId,
            destination,
            receipt);
    }

    /// <summary>
    /// Whether RIoT has actually bound a vehicle to the order. BC-ORDER-012 and BC-ORDER-013
    /// record that a QUEUEING order carries the literal "--" placeholder in executeVehicleKey
    /// until dispatch binds one, so until then the appointed key is what identifies the order --
    /// exactly the "appointVehicleKey == ours || executeVehicleKey == ours" rule BC-ORDER-013
    /// prescribes. Reading the placeholder as a vehicle key made every freshly created order
    /// fail its frozen-intent match, which marked the intent RESULT_UNKNOWN seconds after a
    /// successful create and left the journey unable to ever confirm its own movement.
    /// </summary>
    private static bool IsAssignedVehicleKey(string? executeVehicleKey) =>
        !string.IsNullOrWhiteSpace(executeVehicleKey) &&
        !string.Equals(executeVehicleKey.Trim(), UnassignedVehicleKeyPlaceholder, StringComparison.Ordinal);

    private static RiotOrderObservationKind ToObservationKind(int orderState) => orderState switch
    {
        1 or 3 or 7 or 9 or 10 => RiotOrderObservationKind.Active,
        2 or 4 or 5 or 6 or 8 => RiotOrderObservationKind.Terminal,
        _ => RiotOrderObservationKind.Unknown
    };

    private static void ValidateFrozenIntent(OrderIntent intent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.UpperId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.VehicleKey);
        if (intent.MapId <= 0 || intent.DestinationStationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intent), "RIoT map and destination identifiers must be positive.");
        }
    }

    private RiotOrderCallReceipt Receipt(
        string operation,
        string classification,
        int? httpStatusCode = null,
        string? businessCode = null,
        bool? resultPresent = null,
        string? failureCategory = null) =>
        new(
            operation,
            classification,
            timeProvider.GetUtcNow(),
            httpStatusCode,
            businessCode,
            resultPresent,
            failureCategory);

    private RiotOrderCallReceipt FailureReceipt(string operation, Exception error)
    {
        int? httpStatusCode = RiotCallFailureClassification.HttpStatusCode(error);
        string? rawBusinessCode = RiotCallFailureClassification.RawBusinessCode(error);
        string? businessCode = RiotAuditSanitizer.BusinessCode(rawBusinessCode);
        // order-ref-missing is this gateway's own case: the SDK reached RIoT and RIoT answered
        // without the order reference, which is a protocol failure rather than a business one.
        // Everything else is the shared classification.
        string failureCategory = error is RiotApiException { BusinessCode: "order-ref-missing" }
            ? "PROTOCOL_FAILURE"
            : RiotCallFailureClassification.FailureCategory(error);
        bool? resultPresent = rawBusinessCode == "order-ref-missing" ? false : null;
        return Receipt(
            operation,
            "SdkFailure",
            httpStatusCode,
            businessCode,
            resultPresent,
            failureCategory);
    }

    private static RiotOrderObservation Unknown(string upperId, RiotOrderCallReceipt? receipt = null) =>
        new(upperId, RiotOrderObservationKind.Unknown, null, Receipt: receipt);

    private RiotVehicleObservation UnknownVehicle(string vehicleKey) => new(
        vehicleKey,
        Connected: false,
        Enabled: false,
        ProcState: "UNKNOWN",
        CurrentMap: string.Empty,
        CurrentStationId: null,
        BatteryPercent: null,
        BatteryState: null,
        Speed: null,
        ObservedAt: timeProvider.GetUtcNow(),
        LockStatus: null,
        OrderTaskId: null);

    private RiotVehicleSafetyObservation UnknownSafety(string vehicleKey, string reason) => new(
        vehicleKey,
        RiotVehicleMotionState.Unknown,
        timeProvider.GetUtcNow(),
        "RIOT_BEHAVIOR_LAB_R41",
        [reason]);
}
