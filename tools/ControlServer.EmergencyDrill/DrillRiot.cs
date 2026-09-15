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

/// <summary>
/// The trailing still streak. <paramref name="LatchedRunningSamples"/> counts the samples in it that
/// were still only by the latched-running rule (<see cref="StillRule.LatchedRunning"/>).
/// </summary>
internal sealed record StopVerdict(
    bool Stopped,
    DateTimeOffset? Since,
    int Streak,
    bool ProductReadingNotMoving,
    int LatchedRunningSamples)
{
    /// <summary>Stopped, and the stop rests at least partly on speed 0 + MT_RUNNING under an engaged latch.</summary>
    public bool StillWhileLatchedRunning => Stopped && LatchedRunningSamples > 0;

    internal string Describe() => FormattableString.Invariant(
        $"stopped={Stopped} streak={Streak} stillWhileLatchedRunning={StillWhileLatchedRunning} (latched MT_RUNNING samples in streak: {LatchedRunningSamples})");
}

/// <summary>Whether one motion sample counts as still, and by which rule.</summary>
internal enum StillRule
{
    /// <summary>Not still: moving, speed or movementState unreported, a read failed, or MT_RUNNING without an engaged latch.</summary>
    NotStill,

    /// <summary>A reported speed of exactly 0 with a reported movementState other than MT_RUNNING.</summary>
    NotRunning,

    /// <summary>
    /// A reported speed of exactly 0 with movementState MT_RUNNING, while the emergency latch was known
    /// to be engaged (CAN_RECOVER or CAN_NOT_RECOVER). 2026-09-15 field finding: an emergency-latched
    /// vehicle that still holds its order keeps reporting MT_RUNNING with speed 0.
    /// </summary>
    LatchedRunning
}

/// <summary>
/// A motion sample (null when the read failed) with whether the latest latch reading taken at or
/// before it showed the latch engaged. An unread latch is not an engaged one.
/// </summary>
internal readonly record struct StopSample(VehicleMotionSample? Motion, bool LatchEngaged);

/// <summary>What the drill reads out of a motion sample.</summary>
internal static class Motion
{
    /// <summary>
    /// The lowest reported speed that counts as actually moving, compared strictly (speed must be
    /// greater). In RIoT's own speed unit, the <c>speed</c> field of getVehicleInfo / the vehicle card,
    /// which RIoT does not document; 0.349 was read while agv02 was driven by hand on 2026-09-15, which
    /// fits m/s. 0.05 sits well above a reported 0 and well below any real driving speed.
    /// </summary>
    internal const double MinimumMovingSpeed = 0.05;

    private const string MovementRunning = "MT_RUNNING";

    /// <summary>
    /// Actually driving between stations: the product's own <see cref="HttpRiotMovementGateway.ReadMotion"/>
    /// reads Moving (non-zero speed or MT_RUNNING), the reported speed is above
    /// <see cref="MinimumMovingSpeed"/>, and no station is reported.
    /// </summary>
    /// <remarks>
    /// The speed is the discriminator; neither of the other two conditions is enough. 2026-09-15 on
    /// site: when agv02 took the drill order it rotated in place at station 210 without leaving it, and
    /// throughout RIoT reported movementState=MT_RUNNING, speed=0 and currentStationId=0. So MT_RUNNING
    /// does not mean the vehicle is driving, and currentStationId 0 does not mean it has left the
    /// station. watch-moving therefore waits through the start-of-order rotation.
    /// </remarks>
    internal static bool IsMovingBetweenStations(VehicleMotionSample sample) =>
        sample.Reading == VehicleMotionReading.Moving &&
        sample.Speed is double speed && speed > MinimumMovingSpeed &&
        sample.CurrentStationId is null or 0;

    /// <summary>
    /// Which rule, if any, makes a sample still. Speed must be reported as exactly 0 and movementState
    /// must be reported. A state other than MT_RUNNING is still (<see cref="StillRule.NotRunning"/>):
    /// deliberately wider than the product's NotMoving (MT_FINISHED / MT_PAUSED only), and whether the
    /// product would also have read the streak as NotMoving is recorded alongside. MT_RUNNING is still
    /// only while the latch is known to be engaged (<see cref="StillRule.LatchedRunning"/>); without an
    /// engaged latch it stays not still.
    /// </summary>
    internal static StillRule ClassifyStill(VehicleMotionSample sample, bool latchEngaged)
    {
        if (sample.Speed is not double speed || speed != 0 || sample.MovementState is null)
        {
            return StillRule.NotStill;
        }
        if (!string.Equals(sample.MovementState, MovementRunning, StringComparison.Ordinal))
        {
            return StillRule.NotRunning;
        }
        return latchEngaged ? StillRule.LatchedRunning : StillRule.NotStill;
    }

    /// <summary>For samples all taken under one latch reading (cancel-order, release).</summary>
    internal static StopVerdict EvaluateStop(IReadOnlyList<VehicleMotionSample?> samples, bool latchEngaged) =>
        EvaluateStop([.. samples.Select(sample => new StopSample(sample, latchEngaged))]);

    /// <summary>
    /// The trailing run of still samples at one unchanged place. Stopped means at least three of
    /// them spanning at least one second; a failed read breaks the run, because a gap is not evidence
    /// of stillness. The vehicle card carries no coordinates, so "no position change" can only be
    /// judged on currentMap and currentStationId.
    /// </summary>
    internal static StopVerdict EvaluateStop(IReadOnlyList<StopSample> samples)
    {
        VehicleMotionSample? latest = null;
        VehicleMotionSample? earliest = null;
        int streak = 0;
        int latchedRunning = 0;
        bool productNotMoving = true;
        for (int index = samples.Count - 1; index >= 0; index--)
        {
            StopSample entry = samples[index];
            if (entry.Motion is not VehicleMotionSample sample)
            {
                break;
            }
            StillRule rule = ClassifyStill(sample, entry.LatchEngaged);
            if (rule == StillRule.NotStill)
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
            latchedRunning += rule == StillRule.LatchedRunning ? 1 : 0;
            productNotMoving &= sample.Reading == VehicleMotionReading.NotMoving;
        }

        bool stopped = streak >= 3 && latest is not null && earliest is not null &&
            latest.ObservedAt - earliest.ObservedAt >= TimeSpan.FromSeconds(1);
        return new StopVerdict(stopped, stopped ? earliest?.ObservedAt : null, streak, stopped && productNotMoving, latchedRunning);
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
