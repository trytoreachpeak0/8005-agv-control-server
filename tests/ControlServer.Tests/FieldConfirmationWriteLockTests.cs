using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Charging;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.ChargingAllocationTests;
using static ControlServer.Tests.ChargingUnableToChargeTests;
using FleetFixture = ControlServer.Tests.MultiVehicleExecutionTests.FleetFixture;

namespace ControlServer.Tests;

/// <summary>
/// control-server#452：人工清桩确认（control-server#406）与现场确认充不上（control-server#410）经入站处理器进来时，判定期间向 RIoT 的读取不能落在收件箱的写事务里。
/// </summary>
/// <remarks>
/// <para>
/// 这个库的写事务是 <c>BEGIN IMMEDIATE</c>：开事务那一刻就拿到整库写锁，直到提交。入站处理器在开着这个事务时调判定，判定要是在里面读 RIoT，RIoT 慢多久，
/// 引擎那一轮与别的车的入站就被挡多久，等满 busy timeout（生产 30 秒）就以 <c>SQLITE_BUSY</c> 失败。
/// </para>
/// <para>
/// 复现是确定的，不靠计时：假 RIoT 在判定读旧单的那一刻停住，直到用例放行；停住期间，另一辆车的一条入站、引擎的一轮各在自己的连接上写库。库是文件库
/// （每个上下文一个连接，写锁才是生产那一把），busy timeout 压到一秒，挡住时一秒就失败，而不是挂住用例。
/// </para>
/// </remarks>
[Trait("IntegrationSlice", "FP-IS-13")]
public sealed class FieldConfirmationWriteLockTests : IAsyncDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"cs452-{Guid.NewGuid():N}.db");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AgvA => FleetFixture.AgvIds[0];
    private static string AgvB => FleetFixture.AgvIds[1];

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (string file in new[] { _database, _database + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A pooled handle still open on Windows: the temp directory keeps it.
            }
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 现场确认充不上：RIoT 读旧单停住期间，另一辆车的入站与引擎的一轮都照常写成；放行之后判定照常答 <c>CONFIRMED</c>。
    /// </summary>
    [Fact]
    public async Task AnUnableToChargeConfirmationHoldsNoWriteLockWhileRiotAnswers()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2, databaseFile: _database);
        await FailingAtChargerAsync(fleet, resultCode: 1);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Empty(await HoldsAsync(fleet));

        HeldRiot riot = new(fleet.Riot);
        await using ControlServerDbContext deciding = fleet.NewContext();
        OnboardMessageProcessor processor = Processor(fleet, deciding, fieldConfirmations: FieldConfirmations(fleet, deciding, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);

        string response = await WhileRiotIsHeldAsync(
            fleet, riot, () => processor.ProcessAsync(UnableToChargeLine(state), state, Token));

        Assert.Equal("CONFIRMED", Payload(response, "UnableToChargeFieldConfirmationResult").GetProperty("outcome").GetString());
        Assert.Single(await HoldsAsync(fleet));
    }

    /// <summary>
    /// 人工清桩确认：同上。车已被挪开、旧单已取消，判定照常答 <c>CONFIRMED</c> 并放桩。
    /// </summary>
    [Fact]
    public async Task AManualStationClearanceHoldsNoWriteLockWhileRiotAnswers()
    {
        await using FleetFixture fleet = await FleetAsync(vehicles: 2, databaseFile: _database);
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        fleet.Riot.CancelOrder(journey.PickupUpperId);
        fleet.Riot.VehicleOverrides[FleetFixture.VehicleKeys[0]] =
            seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };

        HeldRiot riot = new(fleet.Riot);
        await using ControlServerDbContext deciding = fleet.NewContext();
        OnboardMessageProcessor processor = Processor(fleet, deciding, stationClearance: StationClearance(fleet, deciding, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);

        string response = await WhileRiotIsHeldAsync(
            fleet, riot, () => processor.ProcessAsync(ManualStationClearanceLine(state), state, Token));

        JsonElement result = Payload(response, "ManualStationClearanceConfirmationResult");
        Assert.Equal("CONFIRMED", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("stationReleased").GetBoolean());
    }

    /// <summary>
    /// 判定开始、停在 RIoT 那一刻，另一辆车发一条心跳（入站处理器在自己的连接上开收件箱事务、写一行），引擎跑一轮；两者都不能撞上写锁。然后放行 RIoT，
    /// 答判定的那一行。
    /// </summary>
    private static async Task<string> WhileRiotIsHeldAsync(FleetFixture fleet, HeldRiot riot, Func<Task<string>> decide)
    {
        riot.Armed = true;
        Task<string> deciding = decide();
        await riot.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Exception? otherInbound;
        Exception? engineRound;
        try
        {
            await using ControlServerDbContext other = fleet.NewContext();
            OnboardMessageProcessor otherProcessor = Processor(fleet, other);
            OnboardConnectionState otherState = await SessionAsync(fleet, AgvB);
            otherInbound = await Record.ExceptionAsync(() => otherProcessor.ProcessAsync(HeartbeatLine(otherState), otherState, Token));
            engineRound = await Record.ExceptionAsync(() => fleet.RunRoundAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            riot.Release();
        }

        string response = await deciding.WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.Equal(
            ("other vehicle's inbound: written", "engine round: written"),
            ($"other vehicle's inbound: {otherInbound?.Message ?? "written"}", $"engine round: {engineRound?.Message ?? "written"}"));
        return response;
    }

    private static OnboardMessageProcessor Processor(
        FleetFixture fleet,
        ControlServerDbContext db,
        ManualStationClearance? stationClearance = null,
        UnableToChargeFieldConfirmations? fieldConfirmations = null) =>
        TestOnboardProcessorFactory.Create(
            db, new WireToGateStore(db), fleet.Clock, new ConfigurationBuilder().Build(), runtimeOptions: fleet.Options,
            stationClearance: stationClearance, fieldConfirmations: fieldConfirmations);

    private static async Task<OnboardConnectionState> SessionAsync(FleetFixture fleet, string agvId) => new()
    {
        AgvId = agvId,
        SessionGeneration = await fleet.Context.SessionRecoveries.AsNoTracking()
            .Where(row => row.AgvId == agvId).Select(row => row.SessionGeneration).SingleAsync(Token),
        HandshakeCompleted = true,
        Readiness = SessionReadiness.Ready,
    };

    private static UnableToChargeFieldConfirmations FieldConfirmations(
        FleetFixture fleet, ControlServerDbContext db, IRiotVehicleFacts riot)
    {
        FieldOperatorRoleRoster roster = new(Options.Create(fleet.ClearanceRoles));
        return new UnableToChargeFieldConfirmations(
            db,
            new FieldConfirmationRequestStore(db),
            riot,
            roster,
            new ChargerRosterStore(db, new GovernedConfigurationPublisher(Audit(db), Audit(db))),
            new ChargingHoldStore(db),
            new ManualChargingHoldStore(db),
            fleet.ChargingPolicy,
            Audit(db),
            Options.Create(fleet.Options),
            fleet.Clock,
            NullLogger<UnableToChargeFieldConfirmations>.Instance,
            new StationClearanceExit(
                roster,
                Options.Create(fleet.ClearanceRoles),
                new ConfigurationBuilder().Build(),
                Options.Create(new VehicleFaultRecoveryOptions { CredentialEnvironmentVariable = "W2G_TEST_CS452_UNSET" })));
    }

    private static ManualStationClearance StationClearance(FleetFixture fleet, ControlServerDbContext db, IRiotVehicleFacts riot) => new(
        db,
        new FieldConfirmationRequestStore(db),
        new StationClearanceStore(db),
        riot,
        new FieldOperatorRoleRoster(Options.Create(fleet.ClearanceRoles)),
        Audit(db),
        Options.Create(fleet.Options),
        fleet.Clock,
        NullLogger<ManualStationClearance>.Instance);

    private static GovernanceStore Audit(ControlServerDbContext context) =>
        new(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    private static string Envelope(OnboardConnectionState state, string messageType, object payload) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId = (string?)null,
            agvId = state.AgvId,
            sessionGeneration = state.SessionGeneration,
            sentAt = "2026-09-08T06:00:00Z",
            payload,
        });

    private static string HeartbeatLine(OnboardConnectionState state) =>
        Envelope(state, "Heartbeat", new { observedAt = "2026-09-08T06:00:00Z" });

    private static string UnableToChargeLine(OnboardConnectionState state) =>
        Envelope(state, "UnableToChargeFieldConfirmationRequested", new
        {
            confirmationRequestId = "00000000-0000-4000-8000-000000045201",
            chargerStationId = Near.StationName,
            observedCondition = "CONNECTION_FAILED",
            @operator = new { operatorId = "fleet-r11", verificationMethod = "SESSION", verifiedAt = "2026-09-08T05:59:00Z" },
            observedAt = "2026-09-08T06:00:00Z",
        });

    private static string ManualStationClearanceLine(OnboardConnectionState state) =>
        Envelope(state, "ManualStationClearanceConfirmationRequested", new
        {
            confirmationRequestId = "00000000-0000-4000-8000-000000045202",
            stationId = Near.StationName,
            publicStationFunction = (string?)null,
            clearedCondition = "STATION_EMPTY",
            @operator = new { operatorId = "fleet-r11", verificationMethod = "SESSION", verifiedAt = "2026-09-08T05:59:00Z" },
            observedAt = "2026-09-08T06:00:00Z",
        });

    private static JsonElement Payload(string response, string messageType)
    {
        using JsonDocument document = JsonDocument.Parse(response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal(messageType, document.RootElement.GetProperty("messageType").GetString());
        return document.RootElement.GetProperty("payload").Clone();
    }

    /// <summary>
    /// armed 之后，第一次读旧单停在那里，直到 <see cref="Release"/>：一个慢的 RIoT，慢多久由用例定。读车照常。
    /// </summary>
    private sealed class HeldRiot(IRiotVehicleFacts inner) : IRiotVehicleFacts
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Armed { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken) =>
            inner.ReadVehicleAsync(vehicleKey, cancellationToken);

        public async Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            if (Armed && Entered.TrySetResult())
            {
                await _released.Task.WaitAsync(cancellationToken);
            }
            return await inner.ReconcileByUpperIdAsync(upperId, cancellationToken);
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }
}
