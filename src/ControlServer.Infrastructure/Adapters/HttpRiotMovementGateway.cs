using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// Minimal allowlisted RIoT order boundary for the WIRE_TO_GATE MVP. It deliberately
/// has no automatic retry: a timed-out mutation remains unknown until reconciled by upperId.
/// </summary>
public sealed class HttpRiotMovementGateway : IRiotMovementGateway, IRiotVehicleFacts, IRiotMapStationCatalog,
    IRiotVehicleSafetyFacts
{
    public const string CreatePath = "/api/order/v1/add/byDefaultMissions";
    public const string ReconcilePathPrefix = "/api/order/v1/orderRecord/detailByUpperId/";
    public const string VehiclePath = "/api/task/vehicles/getVehicleInfoByDeviceKey";
    public const string MapStationsPathPrefix = "/api/imap/v1/mapInfo/stations/";
    public const string VehicleSafetyPathPrefix = "/api/task/v1/task/getVehicleInfo/";
    public const string NonFinalOrdersPath =
        "/api/order/v1/orderRecord?pageNum=1&pageSize=100&filterByState=1&filterByState=3&filterByState=7&filterByState=9";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;

    public HttpRiotMovementGateway(HttpClient httpClient)
        : this(httpClient, TimeProvider.System)
    {
    }

    public HttpRiotMovementGateway(HttpClient httpClient, TimeProvider timeProvider)
    {
        this.httpClient = httpClient;
        this.timeProvider = timeProvider;
    }

    public async Task<RiotOrderObservation> ReconcileByUpperIdAsync(
        string upperId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upperId);
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                ReconcilePathPrefix + Uri.EscapeDataString(upperId));
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null);
            }
            if (!response.IsSuccessStatusCode)
            {
                return Unknown(upperId);
            }

            RiotEnvelope? envelope = await response.Content.ReadFromJsonAsync<RiotEnvelope>(
                SerializerOptions, cancellationToken).ConfigureAwait(false);
            return ToObservation(upperId, envelope);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown(upperId);
        }
        catch (HttpRequestException)
        {
            return Unknown(upperId);
        }
        catch (JsonException)
        {
            return Unknown(upperId);
        }
    }

    public async Task<RiotOrderObservation> CreateAsync(
        OrderIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateFrozenIntent(intent);
        var body = new
        {
            appointVehicleKey = intent.VehicleKey,
            isAppointEnable = 1,
            lockStatus = 0,
            orderName = intent.UpperId,
            upperId = intent.UpperId,
            mission = new[]
            {
                new
                {
                    type = "move",
                    mapId = intent.MapId,
                    destination = intent.DestinationStationId
                }
            }
        };

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, CreatePath)
            {
                Content = JsonContent.Create(body, options: SerializerOptions)
            };
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unknown(intent.UpperId);
            }
            RiotEnvelope? envelope = await response.Content.ReadFromJsonAsync<RiotEnvelope>(
                SerializerOptions, cancellationToken).ConfigureAwait(false);
            return ToObservation(intent.UpperId, envelope);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unknown(intent.UpperId);
        }
        catch (HttpRequestException)
        {
            return Unknown(intent.UpperId);
        }
        catch (JsonException)
        {
            return Unknown(intent.UpperId);
        }
    }

    public async Task<RiotVehicleObservation> ReadVehicleAsync(
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"{VehiclePath}?key={Uri.EscapeDataString(vehicleKey)}");
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UnknownVehicle(vehicleKey);
            }
            VehicleEnvelope? envelope = await response.Content.ReadFromJsonAsync<VehicleEnvelope>(
                SerializerOptions, cancellationToken).ConfigureAwait(false);
            VehicleDto? vehicle = envelope?.Result;
            if (envelope is null || !IsSuccessCode(envelope.Code) || vehicle is null ||
                !string.Equals(vehicle.DeviceKey, vehicleKey, StringComparison.Ordinal))
            {
                return UnknownVehicle(vehicleKey);
            }
            return new RiotVehicleObservation(
                vehicleKey,
                Connected: vehicle.Status == 1,
                Enabled: vehicle.Enable == true,
                ProcState: vehicle.ProcState ?? "UNKNOWN",
                CurrentMap: vehicle.CurrentMap ?? string.Empty,
                CurrentStationId: vehicle.CurrentPosition,
                BatteryPercent: vehicle.Battery,
                BatteryState: vehicle.BatteryState,
                Speed: vehicle.Speed,
                ObservedAt: timeProvider.GetUtcNow(),
                LockStatus: vehicle.LockStatus,
                OrderTaskId: vehicle.OrderTaskId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UnknownVehicle(vehicleKey);
        }
        catch (HttpRequestException)
        {
            return UnknownVehicle(vehicleKey);
        }
        catch (JsonException)
        {
            return UnknownVehicle(vehicleKey);
        }
    }

    public async Task<RiotMapStationCatalogSnapshot> ReadMapStationsAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);

        using HttpRequestMessage request = new(HttpMethod.Get, MapStationsPathPrefix + mapId);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        StationEnvelope? envelope = await response.Content.ReadFromJsonAsync<StationEnvelope>(
            SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (envelope is null || !IsSuccessCode(envelope.Code) || envelope.Result is null)
        {
            throw new InvalidDataException("RIoT Map station catalog response was not successful.");
        }

        RiotMapStation[] stations = envelope.Result.Select(station =>
            new RiotMapStation(
                station.Id > 0
                    ? station.Id
                    : throw new InvalidDataException("RIoT Map station id must be positive."),
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
        return new RiotMapStationCatalogSnapshot(mapId, timeProvider.GetUtcNow(), fingerprint, stations);
    }

    public async Task<RiotVehicleSafetyObservation> ReadVehicleSafetyAsync(
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        try
        {
            using HttpRequestMessage vehicleRequest = new(
                HttpMethod.Get, VehicleSafetyPathPrefix + Uri.EscapeDataString(vehicleKey));
            using HttpResponseMessage vehicleResponse = await httpClient.SendAsync(
                vehicleRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            vehicleResponse.EnsureSuccessStatusCode();
            SafetyVehicleEnvelope? vehicleEnvelope = await vehicleResponse.Content
                .ReadFromJsonAsync<SafetyVehicleEnvelope>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            SafetyVehicleDto? vehicle = vehicleEnvelope?.Vehicle;
            SafetyVehicleTaskDto? task = vehicleEnvelope?.VehicleTaskInfo;
            if (vehicle is null || task is null ||
                !string.Equals(task.Key, vehicleKey, StringComparison.Ordinal))
            {
                return UnknownSafety(vehicleKey, "RIOT_VEHICLE_IDENTITY_UNAVAILABLE");
            }

            using HttpRequestMessage ordersRequest = new(HttpMethod.Get, NonFinalOrdersPath);
            using HttpResponseMessage ordersResponse = await httpClient.SendAsync(
                ordersRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            ordersResponse.EnsureSuccessStatusCode();
            NonFinalOrdersEnvelope? ordersEnvelope = await ordersResponse.Content
                .ReadFromJsonAsync<NonFinalOrdersEnvelope>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<NonFinalOrderDto>? orders = ordersEnvelope?.Result?.Records;
            if (ordersEnvelope is null || !IsSuccessCode(ordersEnvelope.Code) || orders is null || orders.Count >= 100)
            {
                return UnknownSafety(vehicleKey, "RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN");
            }

            bool hasNonFinalOrder = orders.Any(order =>
                string.Equals(order.AppointVehicleKey, vehicleKey, StringComparison.Ordinal) ||
                string.Equals(order.ExecuteVehicleKey, vehicleKey, StringComparison.Ordinal));
            if (vehicle.Speed is not null && vehicle.Speed != 0 ||
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
            if (!string.Equals(task.ProcState, "IDLE", StringComparison.Ordinal)) reasons.Add("RIOT_PROC_NOT_IDLE");
            if (task.ProcessingOrder != false) reasons.Add("RIOT_PROCESSING_ORDER_UNKNOWN_OR_ACTIVE");
            if (task.Enable != true) reasons.Add("RIOT_VEHICLE_NOT_ENABLED");
            if (!string.Equals(task.IntegrationLevel, "ON_LINE", StringComparison.Ordinal)) reasons.Add("RIOT_VEHICLE_NOT_ONLINE");
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UnknownSafety(vehicleKey, "RIOT_READ_TIMEOUT");
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException)
        {
            return UnknownSafety(vehicleKey, "RIOT_READ_FAILED");
        }
    }

    private static RiotOrderObservation ToObservation(string expectedUpperId, RiotEnvelope? envelope)
    {
        if (envelope is null || !IsSuccessCode(envelope.Code) || envelope.Result is null ||
            string.IsNullOrWhiteSpace(envelope.Result.OrderId) ||
            !string.Equals(envelope.Result.UpperId, expectedUpperId, StringComparison.Ordinal) ||
            envelope.Result.OrderState is null)
        {
            return Unknown(expectedUpperId);
        }

        RiotOrderObservationKind kind = envelope.Result.OrderState.Value switch
        {
            1 or 3 or 7 or 9 or 10 => RiotOrderObservationKind.Active,
            2 or 4 or 5 or 6 or 8 => RiotOrderObservationKind.Terminal,
            _ => RiotOrderObservationKind.Unknown
        };
        OrderMissionDto? movement = envelope.Result.Missions?.SingleOrDefault(mission => mission.Type == "move");
        string? vehicleKey = string.IsNullOrWhiteSpace(envelope.Result.ExecuteVehicleKey)
            ? envelope.Result.AppointVehicleKey
            : envelope.Result.ExecuteVehicleKey;
        return new RiotOrderObservation(
            expectedUpperId,
            kind,
            envelope.Result.OrderId,
            envelope.Result.OrderState,
            vehicleKey,
            movement?.MapId,
            movement?.Destination ?? envelope.Result.EndStationNo);
    }

    private static bool IsSuccessCode(JsonElement code)
    {
        string value = code.ValueKind == JsonValueKind.String
            ? code.GetString() ?? string.Empty
            : code.GetRawText();
        return value.Trim() is "0" or "200" or "OK" or "ok" or "success" or "SUCCESS";
    }

    private static void ValidateFrozenIntent(OrderIntent intent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.UpperId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.VehicleKey);
        if (intent.MapId <= 0 || intent.DestinationStationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intent), "RIoT map and destination identifiers must be positive.");
        }
    }

    private static RiotOrderObservation Unknown(string upperId) =>
        new(upperId, RiotOrderObservationKind.Unknown, null);

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

    private sealed record RiotEnvelope(JsonElement Code, string? Message, RiotOrderDto? Result);

    private sealed record StationEnvelope(JsonElement Code, string? Message, IReadOnlyList<StationDto>? Result);

    private sealed record StationDto(int Id, string? Name);

    private sealed record RiotOrderDto(
        long? Id,
        string? OrderId,
        string? UpperId,
        int? OrderState,
        string? AppointVehicleKey,
        string? ExecuteVehicleKey,
        int? EndStationNo,
        IReadOnlyList<OrderMissionDto>? Missions);

    private sealed record OrderMissionDto(string? Type, int? MapId, int? Destination);

    private sealed record VehicleEnvelope(JsonElement Code, string? Message, VehicleDto? Result);

    private sealed record VehicleDto(
        string? DeviceKey,
        bool? Enable,
        int? Status,
        string? ProcState,
        string? CurrentMap,
        int? CurrentPosition,
        int? Battery,
        string? BatteryState,
        double? Speed,
        int? LockStatus,
        string? OrderTaskId);

    private sealed record SafetyVehicleEnvelope(
        SafetyVehicleDto? Vehicle,
        SafetyVehicleTaskDto? VehicleTaskInfo);

    private sealed record SafetyVehicleDto(
        string? MovementState,
        string? ControlState,
        string? EmergencyState,
        string? BreakSwitchState,
        string? LocationState,
        double? Speed);

    private sealed record SafetyVehicleTaskDto(
        string? Key,
        string? ProcState,
        bool? ProcessingOrder,
        bool? Enable,
        string? IntegrationLevel);

    private sealed record NonFinalOrdersEnvelope(
        JsonElement Code,
        NonFinalOrdersResultDto? Result);

    private sealed record NonFinalOrdersResultDto(IReadOnlyList<NonFinalOrderDto>? Records);

    private sealed record NonFinalOrderDto(
        string? AppointVehicleKey,
        string? ExecuteVehicleKey,
        int? OrderState);
}
