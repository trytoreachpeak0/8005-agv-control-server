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

        ObservingRiot riot = new(fleet.Riot);
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
        MovedOff(fleet);

        ObservingRiot riot = new(fleet.Riot);
        await using ControlServerDbContext deciding = fleet.NewContext();
        OnboardMessageProcessor processor = Processor(fleet, deciding, stationClearance: StationClearance(fleet, deciding, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);

        string response = await WhileRiotIsHeldAsync(
            fleet, riot, () => processor.ProcessAsync(ManualStationClearanceLine(state), state, Token));

        JsonElement result = Payload(response, "ManualStationClearanceConfirmationResult");
        Assert.Equal("CONFIRMED", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("stationReleased").GetBoolean());
    }

    // ---- 锁外观察、锁内比对（经真实的消息处理）------------------------------------------------------------------------------

    /// <summary>
    /// 观察与拿锁之间库变了，经入站处理器：确认已记下、旧单还 <c>HANG</c>；第二个确认号观察时读到旧单 <c>HANG</c>，读完、拿锁之前，有人在 RIoT 里让旧单继续，
    /// 引擎那一轮看到、作废了已记下的确认（审查 N1）。锁内重读发现「确认已记下」变了，什么也不写，收件箱事务整个回滚，重新观察：这一次读到旧单在执行，
    /// 按审查 W3 拒收。这条消息照常答一次（不抛、连接不断）；不会按观察时那份 <c>HANG</c> 把确认记下。
    /// </summary>
    [Fact]
    public async Task AClearanceObservedBeforeTheEngineVoidedItIsJudgedAgainOnWhatRiotSaysNow()
    {
        await using FleetFixture fleet = await FleetAsync();
        JourneyRuntimeRow journey = await ConfirmedAsync(fleet);
        MovedOff(fleet);
        Assert.False((await StationClearance(fleet, fleet.Context, fleet.Riot)
            .DecideAsync(ClearanceRequest("00000000-0000-4000-8000-000000045210"), Token)).StationReleased);
        Assert.Equal("fleet-r11", (await ClearanceRowAsync(fleet)).ConfirmedBy);

        ObservingRiot riot = new(fleet.Riot);
        riot.AfterOrderRead = async () =>
        {
            riot.AfterOrderRead = null;
            fleet.Riot.PutOrder(fleet.Riot.OrderOf(journey.PickupUpperId)! with { OrderState = RiotOrderState.Executing });
            await RoundAsync(fleet);
            Assert.Null((await ClearanceRowAsync(fleet)).ConfirmedBy);
        };
        await using ControlServerDbContext deciding = fleet.NewContext();
        OnboardMessageProcessor processor = Processor(fleet, deciding, stationClearance: StationClearance(fleet, deciding, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);

        JsonElement result = Payload(
            await processor.ProcessAsync(ManualStationClearanceLine(state, "00000000-0000-4000-8000-000000045211"), state, Token),
            "ManualStationClearanceConfirmationResult");

        Assert.Equal(2, riot.OrderReads);
        Assert.Equal(
            ("REJECTED", ManualStationClearance.NotAllowedInState),
            (result.GetProperty("outcome").GetString(), result.GetProperty("problem").GetProperty("reasonCode").GetString()));
        StationClearanceRow row = await ClearanceRowAsync(fleet);
        Assert.Equal(((string?)null, (string?)null), (row.ConfirmedBy, row.ConfirmationRequestId));
        Assert.Equal((FleetFixture.VehicleKeys[0], StationExclusivityStates.Reserved), await HolderAsync(fleet, Near.StationId));
    }

    /// <summary>
    /// 现场确认充不上，经入站处理器：观察读完旧单、拿锁之前，引擎那一轮按严格事实形成了系统确认（周期版本变了）。锁内比对不上，收件箱事务整个回滚，重新观察，
    /// 读到系统那一条，答 <c>CONFIRMED</c>；只一条暂停事件（系统的）、一行判定。
    /// </summary>
    [Fact]
    public async Task AnUnableToChargeObservedBeforeTheSystemConfirmedIsJudgedAgainThroughTheInbox()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet);
        await RoundAsync(fleet);
        Assert.Empty(await HoldsAsync(fleet));

        ObservingRiot riot = new(fleet.Riot);
        riot.AfterOrderRead = async () =>
        {
            riot.AfterOrderRead = null;
            await RoundAsync(fleet);
            Assert.Single(await HoldsAsync(fleet));
        };
        await using ControlServerDbContext deciding = fleet.NewContext();
        OnboardMessageProcessor processor = Processor(fleet, deciding, fieldConfirmations: FieldConfirmations(fleet, deciding, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);

        JsonElement result = Payload(
            await processor.ProcessAsync(UnableToChargeLine(state, "00000000-0000-4000-8000-000000045220"), state, Token),
            "UnableToChargeFieldConfirmationResult");

        Assert.Equal(2, riot.OrderReads);
        Assert.Equal("CONFIRMED", result.GetProperty("outcome").GetString());
        ChargingStationAllocationHoldRow only = Assert.Single(await HoldsAsync(fleet));
        Assert.Null(only.ConfirmedByPersonId);
        Assert.Single(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
    }

    /// <summary>
    /// 比对管的不只周期版本：周期版本变了，写的那一刻本来就有令牌挡着（形成确认按读到的版本改周期，拒绝前再核一次版本）；比对另外管着那些不随周期版本变、
    /// 却会被写进暂停事件的事实。这里在观察读完旧单、拿锁之前，桩的这次独占换了一条经过记录（只改 <c>RecordId</c>，周期不动）：锁内比对不上，重新观察，
    /// 暂停事件记的是此刻那一条，不是观察时那一条。
    /// </summary>
    [Fact]
    public async Task AnUnableToChargeIsNotWrittenWithAReservationThatChangedSinceTheObservation()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet, resultCode: 1);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        ObservingRiot riot = new(fleet.Riot);
        riot.AfterOrderRead = async () =>
        {
            riot.AfterOrderRead = null;
            await using ControlServerDbContext other = fleet.NewContext();
            await other.Set<StationExclusivityRow>()
                .Where(row => row.MapId == Near.MapId && row.StationId == Near.StationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RecordId, "cs452-record-now"), Token);
        };
        await using ControlServerDbContext deciding = fleet.NewContext();
        OnboardMessageProcessor processor = Processor(fleet, deciding, fieldConfirmations: FieldConfirmations(fleet, deciding, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);

        JsonElement result = Payload(
            await processor.ProcessAsync(UnableToChargeLine(state, "00000000-0000-4000-8000-000000045225"), state, Token),
            "UnableToChargeFieldConfirmationResult");

        Assert.Equal("CONFIRMED", result.GetProperty("outcome").GetString());
        Assert.Equal(2, riot.OrderReads);
        Assert.Equal("cs452-record-now", Assert.Single(await HoldsAsync(fleet)).ReservationRecordId);
    }

    /// <summary>
    /// 每一次观察都过期（每次读完旧单，周期版本都被推进一次），经入站处理器：三次之后抛出与判定自己用尽重试时逐字相同的
    /// <see cref="InvalidOperationException"/>（连接因此结束，与今天一样）。这条消息没有记为处理过（收件箱里没有这一行、没有答复可重放），判定、暂停事件都没写。
    /// 同一行再来、库不再变：照常处理，答 <c>CONFIRMED</c>。
    /// </summary>
    [Fact]
    public async Task ObservationsThatGoStaleEveryTimeLeaveTheMessageUnprocessedAndFailAsBefore()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet, resultCode: 1);
        await RoundAsync(fleet);
        await RoundAsync(fleet);
        Assert.Empty(await HoldsAsync(fleet));

        ObservingRiot riot = new(fleet.Riot);
        riot.AfterOrderRead = async () =>
        {
            await using ControlServerDbContext engine = fleet.NewContext();
            await engine.Set<ChargingCycleRow>()
                .Where(row => row.VehicleKey == FleetFixture.VehicleKeys[0] && row.Phase != ChargingCyclePhases.Ended)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Version, row => row.Version + 1), Token);
        };
        await using ControlServerDbContext deciding = fleet.NewContext();
        OnboardMessageProcessor processor = Processor(fleet, deciding, fieldConfirmations: FieldConfirmations(fleet, deciding, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);
        const string id = "00000000-0000-4000-8000-000000045230";
        string line = UnableToChargeLine(state, id);
        string messageId = MessageIdOf(line);

        InvalidOperationException failed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(line, state, Token));

        Assert.Equal(UnableToChargeFieldConfirmations.LostEveryRace(id), failed.Message);
        Assert.Equal(
            $"Unable-to-charge field confirmation {id} lost every race it entered; nothing was written.", failed.Message);
        Assert.Equal(3, riot.OrderReads);
        Assert.False(await fleet.Context.ProtocolInbox.AsNoTracking().AnyAsync(row => row.MessageId == messageId, Token));
        Assert.Empty(await fleet.Context.Set<UnableToChargeFieldConfirmationRow>().AsNoTracking().ToArrayAsync(Token));
        Assert.Empty(await HoldsAsync(fleet));

        riot.AfterOrderRead = null;
        JsonElement result = Payload(await processor.ProcessAsync(line, state, Token), "UnableToChargeFieldConfirmationResult");
        Assert.Equal("CONFIRMED", result.GetProperty("outcome").GetString());
        Assert.True(await fleet.Context.ProtocolInbox.AsNoTracking().AnyAsync(row => row.MessageId == messageId, Token));
        Assert.Single(await HoldsAsync(fleet));
    }

    /// <summary>
    /// 重放不观察：同一行（同一个 <c>messageId</c>）再来，收件箱去重命中，一次 RIoT 都不读，答逐字相同的那一行；换了 <c>messageId</c> 的重提（同一个确认号），
    /// 确认号已判过，同样一次 RIoT 都不读，答相同的载荷。两种确认都一样。
    /// </summary>
    [Fact]
    public async Task AReplayOrAResubmissionReadsNothingFromRiot()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet, resultCode: 1);
        await RoundAsync(fleet);
        await RoundAsync(fleet);

        ObservingRiot riot = new(fleet.Riot);
        OnboardMessageProcessor processor = Processor(
            fleet, fleet.Context, StationClearance(fleet, fleet.Context, riot), FieldConfirmations(fleet, fleet.Context, riot));
        OnboardConnectionState state = await SessionAsync(fleet, AgvA);
        foreach (Func<string, string> line in new Func<string, string>[]
                 {
                     id => UnableToChargeLine(state, id),
                     id => ManualStationClearanceLine(state, id),
                 })
        {
            string first = line("00000000-0000-4000-8000-000000045240");
            string answered = await processor.ProcessAsync(first, state, Token);
            (int vehicleReads, int orderReads) = (riot.VehicleReads, riot.OrderReads);
            Assert.True(vehicleReads > 0);

            Assert.Equal(answered, await processor.ProcessAsync(first, state, Token));
            string resubmitted = await processor.ProcessAsync(line("00000000-0000-4000-8000-000000045240"), state, Token);
            Assert.Equal(PayloadText(answered), PayloadText(resubmitted));

            Assert.Equal((vehicleReads, orderReads), (riot.VehicleReads, riot.OrderReads));
            riot.Reset();
        }
    }

    // ---- 守卫 ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 在事务里观察就抛：两种确认的观察、等待点到点人工收尾的 RIoT 读取共用一个守卫（<see cref="RiotReadOutsideWriteLock"/>）。一次 RIoT 都不读。
    /// </summary>
    [Fact]
    public async Task ObservingInsideATransactionThrowsBeforeReadingRiot()
    {
        await using FleetFixture fleet = await FleetAsync();
        await FailingAtChargerAsync(fleet, resultCode: 1);
        ObservingRiot riot = new(fleet.Riot);

        await using (await fleet.Context.Database.BeginTransactionAsync(Token))
        {
            InvalidOperationException unable = await Assert.ThrowsAsync<InvalidOperationException>(
                () => FieldConfirmations(fleet, fleet.Context, riot).ObserveAsync(
                    new UnableToChargeFieldConfirmationRequest(
                        AgvA, FleetFixture.VehicleKeys[0], "00000000-0000-4000-8000-000000045250", 1, Guid.NewGuid().ToString("D"),
                        "hash", Near.StationName, "CONNECTION_FAILED", "fleet-r11", "SESSION", fleet.Clock.GetUtcNow(),
                        fleet.Clock.GetUtcNow()),
                    Token));
            InvalidOperationException clearance = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StationClearance(fleet, fleet.Context, riot).ObserveAsync(
                    ClearanceRequest("00000000-0000-4000-8000-000000045251"), Token));
            InvalidOperationException direct = Assert.Throws<InvalidOperationException>(
                () => RiotReadOutsideWriteLock.Ensure(fleet.Context));
            Assert.All(
                new[] { unable, clearance, direct },
                error => Assert.Equal("RIoT is read outside the write lock (control-server#452).", error.Message));
        }

        Assert.Equal((0, 0), (riot.VehicleReads, riot.OrderReads));
        RiotReadOutsideWriteLock.Ensure(fleet.Context);
    }

    /// <summary>
    /// 判定开始、停在 RIoT 那一刻，另一辆车发一条心跳（入站处理器在自己的连接上开收件箱事务、写一行），引擎跑一轮；两者都不能撞上写锁。然后放行 RIoT，
    /// 答判定的那一行。
    /// </summary>
    private static async Task<string> WhileRiotIsHeldAsync(FleetFixture fleet, ObservingRiot riot, Func<Task<string>> decide)
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

    private static string UnableToChargeLine(
        OnboardConnectionState state, string confirmationRequestId = "00000000-0000-4000-8000-000000045201") =>
        Envelope(state, "UnableToChargeFieldConfirmationRequested", new
        {
            confirmationRequestId,
            chargerStationId = Near.StationName,
            observedCondition = "CONNECTION_FAILED",
            @operator = new { operatorId = "fleet-r11", verificationMethod = "SESSION", verifiedAt = "2026-09-08T05:59:00Z" },
            observedAt = "2026-09-08T06:00:00Z",
        });

    private static string ManualStationClearanceLine(
        OnboardConnectionState state, string confirmationRequestId = "00000000-0000-4000-8000-000000045202") =>
        Envelope(state, "ManualStationClearanceConfirmationRequested", new
        {
            confirmationRequestId,
            stationId = Near.StationName,
            publicStationFunction = (string?)null,
            clearedCondition = "STATION_EMPTY",
            @operator = new { operatorId = "fleet-r11", verificationMethod = "SESSION", verifiedAt = "2026-09-08T05:59:00Z" },
            observedAt = "2026-09-08T06:00:00Z",
        });

    private static string MessageIdOf(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageId").GetString()!;
    }

    private static string PayloadText(string response)
    {
        using JsonDocument document = JsonDocument.Parse(response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        return document.RootElement.GetProperty("payload").GetRawText();
    }

    private static ManualStationClearanceRequest ClearanceRequest(string confirmationRequestId) => new(
        ManualStationClearanceSources.Host, AgvA, FleetFixture.VehicleKeys[0], confirmationRequestId, 1, Guid.NewGuid().ToString("D"),
        "clearance|" + confirmationRequestId, Near.StationName, null, "STATION_EMPTY", "fleet-r11", "BADGE",
        DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-09-08T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    private static Task<StationClearanceRow> ClearanceRowAsync(FleetFixture fleet) =>
        fleet.Context.Set<StationClearanceRow>().AsNoTracking().SingleAsync(Token);

    private static void MovedOff(FleetFixture fleet) =>
        fleet.Riot.VehicleOverrides[FleetFixture.VehicleKeys[0]] = seen => seen with { CurrentStationId = 300, BatteryState = "NO_CHARGE" };

    private static JsonElement Payload(string response, string messageType)
    {
        using JsonDocument document = JsonDocument.Parse(response.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal(messageType, document.RootElement.GetProperty("messageType").GetString());
        return document.RootElement.GetProperty("payload").Clone();
    }

    /// <summary>
    /// 包一层 RIoT：数读了几次车、几次旧单；每次读完旧单之后先让 <see cref="AfterOrderRead"/> 做完（判定此时在锁外观察，还没开事务）；
    /// armed 之后第一次读旧单停在那里，直到 <see cref="Release"/>——一个慢的 RIoT，慢多久由用例定。
    /// </summary>
    private sealed class ObservingRiot(IRiotVehicleFacts inner) : IRiotVehicleFacts
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Armed { get; set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<Task>? AfterOrderRead { get; set; }

        public int VehicleReads { get; private set; }

        public int OrderReads { get; private set; }

        public void Release() => _released.TrySetResult();

        public void Reset() => (VehicleReads, OrderReads) = (0, 0);

        public Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            VehicleReads++;
            return inner.ReadVehicleAsync(vehicleKey, cancellationToken);
        }

        public async Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            OrderReads++;
            if (Armed && Entered.TrySetResult())
            {
                await _released.Task.WaitAsync(cancellationToken);
            }
            RiotOrderObservation order = await inner.ReconcileByUpperIdAsync(upperId, cancellationToken);
            if (AfterOrderRead is { } meanwhile)
            {
                await meanwhile();
            }
            return order;
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            inner.CreateAsync(intent, cancellationToken);
    }
}
