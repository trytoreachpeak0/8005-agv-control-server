using System.Diagnostics;
using System.Globalization;
using ControlServer.Application;
using ControlServer.Infrastructure.Adapters;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.EmergencyDrill;

/// <summary>
/// The product's RIoT adapters over one SDK session, wired the way
/// <c>ControlServer.Host/Runtime/RiotSdkRegistration.cs</c> wires them, with two differences that
/// matter on site: the transport never uses a proxy, and every request is logged to wire.jsonl
/// (method, path, status, duration -- never headers).
/// </summary>
internal sealed class DrillRiot : IDisposable
{
    /// <summary>For the write calls. A timeout here is an Unknown disposition, never a reason to resend.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Per read, so a slow answer cannot stretch a polling loop past its window.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient client;

    private DrillRiot(HttpClient client, RiotSession session)
    {
        this.client = client;
        Session = session;
        Movement = new HttpRiotMovementGateway(session, TimeProvider.System);
        Commands = new HttpRiotOrderCommandGateway(session, TimeProvider.System);
    }

    public RiotSession Session { get; }

    public HttpRiotMovementGateway Movement { get; }

    public HttpRiotOrderCommandGateway Commands { get; }

    internal static DrillRiot Create(Uri baseUrl, string apiKey, DrillEvidence evidence)
    {
        string origin = DrillGuards.Normalize(baseUrl);

        // UseProxy = false. A default .NET handler honours HTTP_PROXY/HTTPS_PROXY/ALL_PROXY and the
        // WinINET system proxy, which on the control host is Clash; a drill command that silently went
        // through it would time its stop against the proxy, not against RIoT. This does NOT get past a
        // TUN adapter -- that takes the packets at the network layer -- which is what preflight's route
        // check is for.
        SocketsHttpHandler transport = new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        WireLogHandler wire = new(evidence) { InnerHandler = transport };
        HttpClient client = new(wire, disposeHandler: true)
        {
            BaseAddress = new Uri(origin + "/", UriKind.Absolute),
            Timeout = CallTimeout
        };
        RiotSession session = new(
            new RiotOptions { BaseUrl = origin, CallApiKey = apiKey, Timeout = CallTimeout },
            client);
        return new DrillRiot(client, session);
    }

    /// <summary>getVehicleInfo + vehicle card. Null only when the reads ran out of time.</summary>
    internal async Task<VehicleMotionSample?> SampleAsync(string deviceKey)
    {
        using CancellationTokenSource timeout = new(ReadTimeout * 2);
        try
        {
            return await Movement.SampleMotionAsync(deviceKey, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The vehicle card. The adapter already turns a failed read into a disconnected, UNKNOWN card.</summary>
    internal async Task<RiotVehicleObservation?> ReadCardAsync(string deviceKey)
    {
        using CancellationTokenSource timeout = new(ReadTimeout);
        try
        {
            return await Movement.ReadVehicleAsync(deviceKey, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The latch. An observation with a null state means RIoT could not be asked.</summary>
    internal async Task<RiotVehicleEmergencyObservation?> ReadEmergencyAsync(string deviceKey)
    {
        using CancellationTokenSource timeout = new(ReadTimeout);
        try
        {
            return await Commands.ReadEmergencyStateAsync(deviceKey, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>getVehicleInfo + the non-final order list, reduced to the product's reason codes.</summary>
    internal async Task<RiotVehicleSafetyObservation?> ReadSafetyAsync(string deviceKey)
    {
        using CancellationTokenSource timeout = new(ReadTimeout * 2);
        try
        {
            return await Movement.ReadVehicleSafetyAsync(deviceKey, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    internal async Task<RiotOrderObservation?> ReconcileAsync(string upperId)
    {
        using CancellationTokenSource timeout = new(ReadTimeout);
        try
        {
            return await Movement.ReconcileByUpperIdAsync(upperId, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    internal async Task<StationsRead> ReadStationsAsync(int mapId)
    {
        using CancellationTokenSource timeout = new(ReadTimeout);
        try
        {
            return new StationsRead(await Movement.ReadMapStationsAsync(mapId, timeout.Token), null);
        }
        catch (OperationCanceledException)
        {
            return new StationsRead(null, "READ_TIMEOUT");
        }
        catch (InvalidDataException error)
        {
            return new StationsRead(null, error.Message);
        }
    }

    public void Dispose()
    {
        Session.Dispose();
        client.Dispose();
    }
}

internal sealed record StationsRead(RiotMapStationCatalogSnapshot? Snapshot, string? Error);

/// <summary>Records every request the SDK makes. Method, host, path, status and duration only.</summary>
internal sealed class WireLogHandler : DelegatingHandler
{
    private readonly DrillEvidence evidence;

    internal WireLogHandler(DrillEvidence evidence) => this.evidence = evidence;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset at = DateTimeOffset.Now;
        long started = Stopwatch.GetTimestamp();
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            Record(request, at, started, (int)response.StatusCode, null);
            return response;
        }
        catch (Exception error)
        {
            Record(request, at, started, null, error.GetType().Name);
            throw;
        }
    }

    private void Record(HttpRequestMessage request, DateTimeOffset at, long started, int? status, string? error) =>
        evidence.AppendWire(new
        {
            at,
            command = evidence.Command,
            method = request.Method.Method,
            host = request.RequestUri?.Authority,
            path = request.RequestUri?.PathAndQuery,
            status,
            elapsedMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1),
            error
        });
}

internal sealed record StopVerdict(bool Stopped, DateTimeOffset? Since, int Streak, bool ProductReadingNotMoving);

/// <summary>What the drill reads out of a motion sample.</summary>
internal static class Motion
{
    /// <summary>
    /// Moving (the product's own <see cref="HttpRiotMovementGateway.ReadMotion"/>: non-zero speed or
    /// MT_RUNNING) and not standing at a station.
    /// </summary>
    internal static bool IsMovingBetweenStations(VehicleMotionSample sample) =>
        sample.Reading == VehicleMotionReading.Moving && sample.CurrentStationId is null or 0;

    /// <summary>
    /// A reported zero speed with a reported movementState that is not MT_RUNNING. Deliberately
    /// wider than the product's NotMoving (MT_FINISHED / MT_PAUSED only): nobody has observed which
    /// state RIoT reports for an emergency-stopped vehicle, and the drill exists partly to find out.
    /// Whether the product would also have read the streak as NotMoving is recorded alongside.
    /// </summary>
    internal static bool IsStill(VehicleMotionSample sample) =>
        sample.Speed is double speed && speed == 0 &&
        sample.MovementState is not null &&
        !string.Equals(sample.MovementState, "MT_RUNNING", StringComparison.Ordinal);

    /// <summary>
    /// The trailing run of still samples at one unchanged place. Stopped means at least three of
    /// them spanning at least one second; a failed read breaks the run, because a gap is not evidence
    /// of stillness. The vehicle card carries no coordinates, so "no position change" can only be
    /// judged on currentMap and currentStationId.
    /// </summary>
    internal static StopVerdict EvaluateStop(IReadOnlyList<VehicleMotionSample?> samples)
    {
        VehicleMotionSample? latest = null;
        VehicleMotionSample? earliest = null;
        int streak = 0;
        bool productNotMoving = true;
        for (int index = samples.Count - 1; index >= 0; index--)
        {
            VehicleMotionSample? sample = samples[index];
            if (sample is null || !IsStill(sample))
            {
                break;
            }
            if (latest is null)
            {
                latest = sample;
            }
            else if (sample.CurrentStationId != latest.CurrentStationId ||
                     !string.Equals(sample.CurrentMap, latest.CurrentMap, StringComparison.Ordinal))
            {
                break;
            }
            earliest = sample;
            streak++;
            productNotMoving &= sample.Reading == VehicleMotionReading.NotMoving;
        }

        bool stopped = streak >= 3 && latest is not null && earliest is not null &&
            latest.ObservedAt - earliest.ObservedAt >= TimeSpan.FromSeconds(1);
        return new StopVerdict(stopped, stopped ? earliest?.ObservedAt : null, streak, stopped && productNotMoving);
    }

    internal static MotionRecord ToRecord(VehicleMotionSample sample) => new()
    {
        At = sample.ObservedAt,
        Reading = sample.Reading.ToString(),
        MovementState = sample.MovementState,
        Speed = sample.Speed,
        CurrentMap = sample.CurrentMap,
        CurrentStationId = sample.CurrentStationId,
        MovingBetweenStations = IsMovingBetweenStations(sample)
    };

    internal static CallReceiptRecord ToReceipt(string operation, RiotCommandCallResult result, TimeSpan elapsed) => new()
    {
        Operation = operation,
        Disposition = result.Disposition.ToString(),
        Classification = result.Receipt.Classification,
        HttpStatusCode = result.Receipt.HttpStatusCode,
        BusinessCode = result.Receipt.BusinessCode,
        FailureCategory = result.Receipt.FailureCategory,
        ElapsedMs = Math.Round(elapsed.TotalMilliseconds, 1)
    };

    internal static string Describe(VehicleMotionSample? sample) => sample is null
        ? "unread (timeout)"
        : FormattableString.Invariant(
            $"reading={sample.Reading} movementState={sample.MovementState ?? "-"} speed={Number(sample.Speed)} map={sample.CurrentMap ?? "-"} station={Number(sample.CurrentStationId)} betweenStationsMoving={IsMovingBetweenStations(sample)}");

    internal static string Number(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    internal static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
