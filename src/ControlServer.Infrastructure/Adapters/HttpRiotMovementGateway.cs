using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// Minimal allowlisted RIoT order boundary for the WIRE_TO_GATE MVP. It deliberately
/// has no automatic retry: a timed-out mutation remains unknown until reconciled by upperId.
/// </summary>
public sealed class HttpRiotMovementGateway : IRiotMovementGateway, IRiotVehicleFacts
{
    public const string CreatePath = "/api/order/v1/add/byDefaultMissions";
    public const string ReconcilePathPrefix = "/api/order/v1/orderRecord/detailByUpperId/";
    public const string VehiclePath = "/api/task/vehicles/getVehicleInfoByDeviceKey";

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

    private sealed record RiotEnvelope(JsonElement Code, string? Message, RiotOrderDto? Result);

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
}
