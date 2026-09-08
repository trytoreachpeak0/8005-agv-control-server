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
    IRiotVehicleSafetyFacts, IVehicleMotionFacts
{
    private static readonly int[] NonFinalOrderStates = [1, 3, 7, 9];

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
        try
        {
            OrderRef created = await riotSession.Order.CreateMoveOrderAsync(
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

    public async Task<RiotVehicleSafetyObservation> ReadVehicleSafetyAsync(
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        try
        {
            VehicleExecutionFacts vehicle = await riotSession.Tasks.GetVehicleExecutionFactsAsync(
                vehicleKey, cancellationToken).ConfigureAwait(false);
            OrderStatePage orders = await riotSession.Order.ListOrdersByStatesAsync(
                NonFinalOrderStates,
                pageNum: 1,
                pageSize: 100,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!orders.CoversAllRecords)
            {
                return UnknownSafety(vehicleKey, "RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN");
            }

            bool hasNonFinalOrder = orders.Records.Any(order =>
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
            movements.Length != 1 ||
            movements[0].MapId is not > 0)
        {
            return Unknown(expectedUpperId, receipt with
            {
                Classification = "Indeterminate",
                FailureCategory = "IDENTITY_INVALID"
            });
        }

        int? destination = movements[0].Destination ?? order.EndStationNo;
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
            movements[0].MapId,
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
