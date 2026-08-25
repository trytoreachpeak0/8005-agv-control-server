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
public sealed class HttpRiotMovementGateway(HttpClient httpClient) : IRiotMovementGateway
{
    public const string CreatePath = "/api/order/v1/add/byDefaultMissions";
    public const string ReconcilePathPrefix = "/api/order/v1/orderRecord/detailByUpperId/";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

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
        return new RiotOrderObservation(expectedUpperId, kind, envelope.Result.OrderId);
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

    private sealed record RiotEnvelope(JsonElement Code, string? Message, RiotOrderDto? Result);

    private sealed record RiotOrderDto(long? Id, string? OrderId, string? UpperId, int? OrderState);
}
