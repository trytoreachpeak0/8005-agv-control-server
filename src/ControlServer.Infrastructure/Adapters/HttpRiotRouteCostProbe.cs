using System.Text.Json;
using ControlServer.Application;
using Microsoft.Kiota.Abstractions;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// Reads RIoT's RouteCost for one vehicle through the named <c>getRouteCostsBy</c> Facade.
/// </summary>
/// <remarks>
/// <para>
/// One endpoint, one Facade, no <c>.Raw</c> — REQ-0309 requires product code to reach the Map and
/// Station surface only through named Facades, and this adapter does not introduce the first
/// exception.
/// </para>
/// <para>
/// <b>The two failure shapes are kept apart here, and that is the whole job of this class.</b>
/// The SDK returns <c>CostsMm = -1</c> when RIoT says unreachable, and throws when the call itself
/// failed or the response carried nothing for this vehicle. The first is an answer and is passed
/// through; the second becomes <c>null</c>. Letting the exception escape, or mapping it to -1,
/// would tell the gate that a Map it never got to see has no route.
/// </para>
/// </remarks>
public sealed class HttpRiotRouteCostProbe(RiotSession riotSession) : IRiotRouteCostProbe
{
    public async Task<RiotRouteCost?> ReadRouteCostAsync(
        int mapId,
        int stationId,
        string vehicleKey,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);

        try
        {
            RouteCost cost = await riotSession.Tasks
                .GetRouteCostAsync(mapId, stationId, vehicleKey, cancellationToken)
                .ConfigureAwait(false);
            return new RiotRouteCost(cost.CostsMm);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (
            error is RiotApiException or ApiException or HttpRequestException or IOException or
                JsonException or InvalidOperationException)
        {
            // No answer is not a route verdict. The gate blocks on this under its own reason.
            return null;
        }
    }
}
