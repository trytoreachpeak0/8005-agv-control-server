using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 车载报文入站去重写库遇到瞬时数据库错误不再断开车载连接（control-server#779）。
/// </summary>
/// <remarks>
/// <para>
/// <b>现场。</b>v5 上线当天（2026-10-19）服务端两次在 <c>WireToGateStore.CaptureFirstResponseAsync</c> 的去重查询上抛异常：
/// 06:48:25Z <c>SQLite Error 5: 'database is locked'</c>，06:55:22Z <c>ObjectDisposedException 'SQLitePCL.sqlite3'</c>。两次都抛在
/// <c>SqliteConnection.Open()</c> 里，业务处理一行还没跑；异常冒到 <see cref="OnboardTcpServer"/> 的兜底 catch，记一条不带车号的
/// 1003，连接被关，车 2～3 秒后重连。
/// </para>
/// <para>
/// <b>守的是什么。</b>业务处理开始之前遇到的瞬时错误（BUSY、LOCKED、底层句柄已释放）整次重来，有上限；同一报文只入一行
/// <c>ProtocolInbox</c>、业务只做一次、应答照常回。业务处理开始之后的数据库错误不重来——它可能已经改过内存里的会话状态——照旧
/// 断线，但断线前记清哪辆车、哪条消息、哪个异常（1108），1003 也带上车号与远端地址。
/// </para>
/// <para>
/// <b>怎么注入。</b><see cref="InboxFault"/> 是一个 EF 命令拦截器，只认带着目标 messageId 参数的那几条 <c>ProtocolInbox</c> 语句：
/// 去重查询（业务处理之前）与插入（业务处理之后、同一事务里）。
/// </para>
/// </remarks>
public sealed class InboundDatabaseFaultTests
{
    private const string AgvId = "AGV-001";
    private const string Credential = "test-credential-not-for-production";

    public static TheoryData<string> TransientFaults => ["busy", "locked", "disposed-handle"];

    [Theory]
    [MemberData(nameof(TransientFaults))]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ATransientFaultOnTheInboxLookupIsRetriedAndTheMessageIsTakenOnce(string fault)
    {
        await using Rig rig = await Rig.StartAsync(nameof(ATransientFaultOnTheInboxLookupIsRetriedAndTheMessageIsTakenOnce));
        string messageId = Guid.NewGuid().ToString("D");
        string requestId = Guid.NewGuid().ToString("D");
        rig.Fault.FailLookup(messageId, times: 1, () => Fault(fault));

        string response = await rig.Processor.ProcessAsync(
            ManualChargingReturn(messageId, requestId, rig.Generation), rig.State, Rig.Token);

        AssertResultFor(response, messageId, requestId);
        Assert.Equal(2, rig.Fault.Lookups);
        Assert.Equal(1, await rig.InboxRowsAsync(messageId));
        Assert.Equal(1, await rig.ManualChargingDecisionsAsync(requestId));
        Assert.Contains(rig.Log.Entries, entry => entry.EventId.Id == 1107 && entry.Message.Contains(messageId));
    }

    /// <summary>重试救回的那一条再到一次（车载端没收到应答而重发）：命中 ProtocolInbox，原样回首个应答，业务不再做。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task TheSameMessageArrivingAgainAfterARetryIsAnsweredFromItsFirstAcceptance()
    {
        await using Rig rig = await Rig.StartAsync(nameof(TheSameMessageArrivingAgainAfterARetryIsAnsweredFromItsFirstAcceptance));
        string messageId = Guid.NewGuid().ToString("D");
        string requestId = Guid.NewGuid().ToString("D");
        string line = ManualChargingReturn(messageId, requestId, rig.Generation);
        rig.Fault.FailLookup(messageId, times: 1, () => Fault("busy"));
        string first = await rig.Processor.ProcessAsync(line, rig.State, Rig.Token);

        string again = await rig.Processor.ProcessAsync(line, rig.State, Rig.Token);

        Assert.Equal(first, again);
        Assert.Equal(1, await rig.InboxRowsAsync(messageId));
        Assert.Equal(1, await rig.ManualChargingDecisionsAsync(requestId));
    }

    /// <summary>
    /// 重连后重放：上一代会话收下的持久报文换到新一代重发，去重查询这时遇到一次 BUSY。重来之后照常按首次受理回
    /// DurableAck，ProtocolInbox 仍是那一行、内容不变。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task AResendIntoTheNextSessionMeetingABusyLookupIsAcknowledgedFromItsFirstAcceptance()
    {
        await using Rig rig = await Rig.StartAsync(nameof(AResendIntoTheNextSessionMeetingABusyLookupIsAcknowledgedFromItsFirstAcceptance));
        string changeId = Guid.NewGuid().ToString("D");
        string original = Envelope("SafetyStateChanged", changeId, rig.Generation, new
        {
            safetyStateVersion = 2,
            safety = Safety(departureSafe: true),
            observedAt = "2026-08-25T09:00:00Z"
        });
        await rig.Processor.ProcessAsync(original, rig.State, Rig.Token);
        string firstRequest = await rig.InboxRequestAsync(changeId);

        OnboardConnectionState next = new();
        await ReachReadyAsync(rig.Processor, next, Rig.Token);
        JsonNode rebound = JsonNode.Parse(original)!;
        rebound["sessionGeneration"] = next.SessionGeneration!.Value;
        rig.Fault.FailLookup(changeId, times: 1, () => Fault("busy"));

        string replay = await rig.Processor.ProcessAsync(rebound.ToJsonString(), next, Rig.Token);

        using JsonDocument ack = JsonDocument.Parse(replay.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("DurableAck", ack.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(changeId, ack.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(next.SessionGeneration, ack.RootElement.GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(2, rig.Fault.Lookups);
        Assert.Equal(1, await rig.InboxRowsAsync(changeId));
        Assert.Equal(firstRequest, await rig.InboxRequestAsync(changeId));
    }

    /// <summary>
    /// 业务处理已经开始之后的 BUSY（这里在同一事务里插 ProtocolInbox 那一步）不重来：处理可能已经改过内存里的会话状态，再跑一遍就是
    /// 处理两次。也不能吞掉异常回一个空应答——那样车载端等不到回答，消息却也没入库。所以照旧抛出、断线，事务回滚，什么都没留下；
    /// 重连后车载端重发，那一次照常受理、只受理一次。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ABusyFaultAfterProcessingBeganIsNotRetriedAndTheResendIsTakenOnce()
    {
        await using Rig rig = await Rig.StartAsync(nameof(ABusyFaultAfterProcessingBeganIsNotRetriedAndTheResendIsTakenOnce));
        string messageId = Guid.NewGuid().ToString("D");
        string requestId = Guid.NewGuid().ToString("D");
        string line = ManualChargingReturn(messageId, requestId, rig.Generation);
        rig.Fault.FailInsert(messageId, times: 1, () => Fault("busy"));

        Exception error = await Assert.ThrowsAnyAsync<Exception>(
            () => rig.Processor.ProcessAsync(line, rig.State, Rig.Token));

        Assert.True(Rig.IsBusy(error), $"抛出的不是注入的 BUSY：{error}");
        Assert.Equal(1, rig.Fault.Inserts);
        Assert.Equal(1, rig.Fault.Lookups);
        Assert.Equal(0, await rig.InboxRowsAsync(messageId));
        Assert.Equal(0, await rig.ManualChargingDecisionsAsync(requestId));
        AssertFailureLogged(rig, messageId);

        // The connection ended; the vehicle reconnects on a new scope and resends the same line, rebound.
        await using ControlServerDbContext context = rig.NewContext();
        OnboardMessageProcessor processor = rig.NewProcessor(context);
        OnboardConnectionState next = new();
        await ReachReadyAsync(processor, next, Rig.Token);
        JsonNode rebound = JsonNode.Parse(line)!;
        rebound["sessionGeneration"] = next.SessionGeneration!.Value;
        string response = await processor.ProcessAsync(rebound.ToJsonString(), next, Rig.Token);

        AssertResultFor(response, messageId, requestId);
        Assert.Equal(1, await rig.InboxRowsAsync(messageId));
        Assert.Equal(1, await rig.ManualChargingDecisionsAsync(requestId));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ALookupThatKeepsFailingEndsTheConnectionAfterThreeAttempts()
    {
        await using Rig rig = await Rig.StartAsync(nameof(ALookupThatKeepsFailingEndsTheConnectionAfterThreeAttempts));
        string messageId = Guid.NewGuid().ToString("D");
        rig.Fault.FailLookup(messageId, times: int.MaxValue, () => Fault("busy"));

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => rig.Processor.ProcessAsync(
            ManualChargingReturn(messageId, Guid.NewGuid().ToString("D"), rig.Generation), rig.State, Rig.Token));

        Assert.True(Rig.IsBusy(error), $"抛出的不是注入的 BUSY：{error}");
        Assert.Equal(3, rig.Fault.Lookups);
        Assert.Equal(0, await rig.InboxRowsAsync(messageId));
        AssertFailureLogged(rig, messageId);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ANonTransientDatabaseFaultIsNotRetriedAndIsLoggedWithTheVehicleAndTheMessage()
    {
        await using Rig rig = await Rig.StartAsync(nameof(ANonTransientDatabaseFaultIsNotRetriedAndIsLoggedWithTheVehicleAndTheMessage));
        string messageId = Guid.NewGuid().ToString("D");
        rig.Fault.FailLookup(messageId, times: int.MaxValue, () => new SqliteException("database disk image is malformed", 11));

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => rig.Processor.ProcessAsync(
            ManualChargingReturn(messageId, Guid.NewGuid().ToString("D"), rig.Generation), rig.State, Rig.Token));

        Assert.Equal(11, error.SqliteErrorCode);
        Assert.Equal(1, rig.Fault.Lookups);
        AssertFailureLogged(rig, messageId);
    }

    /// <summary>
    /// 1003 带上车号与远端地址：握手之后断的，记会话的车号；握手之前断的，车号还不知道，至少有远端地址。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task TheConnectionEndedLogNamesTheVehicleAfterTheHandshakeAndTheRemoteEndpointBefore()
    {
        const string variable = "CONTROL_SERVER_TEST_INBOUND_DB_FAULT_TCP_CREDENTIAL";
        Environment.SetEnvironmentVariable(variable, Credential);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(token);
            OnboardPeer peer = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), TimeProvider.System, Configuration(variable), peer);
            ServiceCollection services = new();
            services.AddScoped(_ => processor);
            await using ServiceProvider provider = services.BuildServiceProvider();
            int port = ReserveFreePort();
            EventRecordingLogger<OnboardTcpServer> log = new();
            using OnboardTcpServer server = new(
                Options.Create(new OnboardTransportOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = port }),
                provider.GetRequiredService<IServiceScopeFactory>(),
                peer,
                log);
            await server.StartAsync(token);
            try
            {
                // Before any handshake: the vehicle is not known yet.
                await SendAndAwaitEndAsync(port, ["not json"], log, endedBefore: 0, token);
                EventRecordingLogger<OnboardTcpServer>.Entry beforeHello = Ended(log).Single();
                Assert.Contains("(no session)", beforeHello.Message);
                Assert.Contains("127.0.0.1:", beforeHello.Message);

                // After the handshake: the session's vehicle.
                await SendAndAwaitEndAsync(port, [.. HandshakeLines(generation: 1), "not json"], log, endedBefore: 1, token);
                EventRecordingLogger<OnboardTcpServer>.Entry afterHandshake = Ended(log).Last();
                Assert.Contains(AgvId, afterHandshake.Message);
                Assert.Contains("127.0.0.1:", afterHandshake.Message);
            }
            finally
            {
                await server.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static EventRecordingLogger<OnboardTcpServer>.Entry[] Ended(EventRecordingLogger<OnboardTcpServer> log)
    {
        lock (log.Entries)
        {
            return [.. log.Entries.Where(entry => entry.EventId.Id == 1003)];
        }
    }

    /// <summary>
    /// Writes the lines on a fresh connection without reading the answers -- the server processes them in order -- and waits
    /// for the server to end it with a 1003.
    /// </summary>
    private static async Task SendAndAwaitEndAsync(
        int port, string[] lines, EventRecordingLogger<OnboardTcpServer> log, int endedBefore, CancellationToken token)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        await using StreamWriter writer = new(client.GetStream(), new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        foreach (string line in lines)
        {
            await writer.WriteLineAsync(line.AsMemory(), token);
        }
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Ended(log).Length == endedBefore && DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
        }
        Assert.Equal(endedBefore + 1, Ended(log).Length);
    }

    private static void AssertFailureLogged(Rig rig, string messageId)
    {
        EventRecordingLogger<OnboardMessageProcessor>.Entry[] failures;
        lock (rig.Log.Entries)
        {
            failures = [.. rig.Log.Entries.Where(entry => entry.EventId.Id == 1108)];
        }
        EventRecordingLogger<OnboardMessageProcessor>.Entry failure = Assert.Single(failures);
        Assert.Contains(AgvId, failure.Message);
        Assert.Contains(messageId, failure.Message);
        Assert.Contains("ManualChargingReturnToServiceRequested", failure.Message);
        Assert.NotNull(failure.Error);
    }

    private static void AssertResultFor(string response, string messageId, string requestId)
    {
        using JsonDocument result = JsonDocument.Parse(response);
        Assert.Equal("ManualChargingReturnToServiceResult", result.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(messageId, result.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(requestId, result.RootElement.GetProperty("payload").GetProperty("requestId").GetString());
    }

    private static Exception Fault(string kind) => kind switch
    {
        // What SqliteException.ThrowExceptionForRC builds for SQLITE_BUSY / SQLITE_LOCKED.
        "busy" => new SqliteException("SQLite Error 5: 'database is locked'.", 5),
        "locked" => new SqliteException("SQLite Error 6: 'database table is locked'.", 6),
        // What SafeHandle.DangerousAddRef throws for a sqlite3 handle the pool had already disposed (06:55:22Z).
        "disposed-handle" => new ObjectDisposedException("SQLitePCL.sqlite3"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static IConfiguration Configuration(string credentialVariable) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OnboardTransport:CredentialEnvironmentVariable"] = credentialVariable
        })
        .Build();

    private static string[] HandshakeLines(long generation) =>
    [
        Envelope("SessionHello", Guid.NewGuid().ToString("D"), null, new
        {
            protocolReleaseIdentity = ReleaseIdentity(),
            credentialProof = Credential
        }),
        Envelope("CapabilitySnapshot", Guid.NewGuid().ToString("D"), generation,
            new { capabilityVersion = 1, activeSlotConfigurationFingerprint = new string('0', 64) }),
        Envelope("SafetyStateSnapshot", Guid.NewGuid().ToString("D"), generation,
            new { safetyStateVersion = 1, safety = Safety(departureSafe: true) }),
        Envelope("RecoveryStateReport", Guid.NewGuid().ToString("D"), generation, new
        {
            reportId = Guid.NewGuid().ToString("D"),
            unsettledSlotOperationAttemptId = (string?)null,
            provenRecoveryCheckpoint = (string?)null,
            activeUnlockSlots = Array.Empty<int>(),
            forcedRecoveryGeneration = 0,
            pendingResults = Array.Empty<object>()
        })
    ];

    private static async Task ReachReadyAsync(
        OnboardMessageProcessor processor, OnboardConnectionState state, CancellationToken token)
    {
        string[] lines = HandshakeLines(generation: 0);
        await processor.ProcessAsync(lines[0], state, token);
        long generation = state.SessionGeneration!.Value;
        foreach (string line in HandshakeLines(generation).Skip(1))
        {
            await processor.ProcessAsync(line, state, token);
        }
        Assert.True(state.HandshakeCompleted, "握手没走完。");
    }

    private static string ManualChargingReturn(string messageId, string requestId, long generation) =>
        Envelope("ManualChargingReturnToServiceRequested", messageId, generation, new
        {
            requestId,
            administrator = new
            {
                operatorId = "OP-4779",
                verificationMethod = "BADGE",
                verifiedAt = "2026-10-19T06:48:00Z"
            },
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            reason = "inbound database fault test",
            observedBatteryPercent = 84.5
        });

    private static object Safety(bool departureSafe) => new
    {
        departureSafe,
        vehicleStopped = departureSafe,
        allTargetSlotsLocked = true,
        allUnlockOutputsReset = true,
        unknownPresent = !departureSafe,
        reasonCodes = departureSafe ? Array.Empty<string>() : new[] { "VEHICLE_MOTION_UNKNOWN" }
    };

    private static readonly JsonSerializerOptions PeerSerializerOptions = new(JsonSerializerDefaults.Web);

    private static string Envelope(string messageType, string messageId, long? generation, object payload) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId = (string?)null,
            agvId = AgvId,
            sessionGeneration = generation,
            sentAt = "2026-10-19T06:48:00Z",
            payload = JsonSerializer.SerializeToElement(payload, PeerSerializerOptions)
        });

    private static object ReleaseIdentity() => new
    {
        repository = "8005-agv-protocol",
        releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        tag = ProtocolCandidateIdentity.Tag,
        commit = ProtocolCandidateIdentity.RepositoryCommit,
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256,
        vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256
    };

    private static int ReserveFreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>One processor on one in-memory database, handshaken to READY, with the fault interceptor on its context.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _credentialVariable;
        private readonly IConfiguration _configuration;

        private Rig(
            SqliteConnection connection,
            string credentialVariable,
            IConfiguration configuration,
            InboxFault fault,
            ControlServerDbContext context,
            OnboardMessageProcessor processor,
            OnboardConnectionState state)
        {
            _connection = connection;
            _credentialVariable = credentialVariable;
            _configuration = configuration;
            Fault = fault;
            Context = context;
            Processor = processor;
            State = state;
        }

        public InboxFault Fault { get; }

        public EventRecordingLogger<OnboardMessageProcessor> Log { get; } = new();

        public ControlServerDbContext Context { get; }

        public OnboardMessageProcessor Processor { get; private set; }

        public OnboardConnectionState State { get; }

        public long Generation => State.SessionGeneration!.Value;

        public static CancellationToken Token => TestContext.Current.CancellationToken;

        public static async Task<Rig> StartAsync(string test)
        {
            string variable = "CONTROL_SERVER_TEST_INBOUND_DB_FAULT_" + test.ToUpperInvariant();
            Environment.SetEnvironmentVariable(variable, Credential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            IConfiguration configuration = Configuration(variable);
            InboxFault fault = new();
            ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(new FaultInterceptor(fault))
                .Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            OnboardConnectionState state = new();
            Rig rig = new(connection, variable, configuration, fault, context, null!, state);
            rig.Processor = rig.NewProcessor(context);
            await ReachReadyAsync(rig.Processor, state, TestContext.Current.CancellationToken);
            return rig;
        }

        /// <summary>A context on the same database without the interceptor: a new connection scope.</summary>
        public ControlServerDbContext NewContext() =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);

        public OnboardMessageProcessor NewProcessor(ControlServerDbContext context) => TestOnboardProcessorFactory.Create(
            context, new WireToGateStore(context), TimeProvider.System, _configuration, logger: Log);

        public static bool IsBusy(Exception error)
        {
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current is SqliteException { SqliteErrorCode: 5 })
                {
                    return true;
                }
            }
            return false;
        }

        public async Task<int> InboxRowsAsync(string messageId)
        {
            await using ControlServerDbContext context = NewContext();
            return await context.ProtocolInbox.CountAsync(row => row.MessageId == messageId, Token);
        }

        public async Task<string> InboxRequestAsync(string messageId)
        {
            await using ControlServerDbContext context = NewContext();
            return (await context.ProtocolInbox.SingleAsync(row => row.MessageId == messageId, Token)).RequestJson;
        }

        public async Task<int> ManualChargingDecisionsAsync(string requestId)
        {
            await using ControlServerDbContext context = NewContext();
            return await context.ManualChargingReturnToServiceRequests.CountAsync(row => row.RequestId == requestId, Token);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
            Environment.SetEnvironmentVariable(_credentialVariable, null);
        }
    }

    /// <summary>Hands every command the context runs to the fault.</summary>
    private sealed class FaultInterceptor(InboxFault fault) : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            fault.Inspect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            fault.Inspect(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            fault.Inspect(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            fault.Inspect(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Throws on the ProtocolInbox statements that carry one messageId: the dedup lookup (before processing) and the insert
    /// (after processing, inside its transaction). Counts every one it sees for that messageId.
    /// </summary>
    private sealed class InboxFault
    {
        private string? _messageId;
        private int _lookupFailures;
        private int _insertFailures;
        private Func<Exception> _lookupFault = () => new InvalidOperationException();
        private Func<Exception> _insertFault = () => new InvalidOperationException();

        public int Lookups { get; private set; }

        public int Inserts { get; private set; }

        public void FailLookup(string messageId, int times, Func<Exception> fault)
        {
            _messageId = messageId;
            _lookupFailures = times;
            _lookupFault = fault;
        }

        public void FailInsert(string messageId, int times, Func<Exception> fault)
        {
            _messageId = messageId;
            _insertFailures = times;
            _insertFault = fault;
        }

        public void Inspect(DbCommand command)
        {
            if (_messageId is null ||
                !command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, _messageId)))
            {
                return;
            }
            string sql = command.CommandText;
            if (sql.Contains("INSERT INTO \"ProtocolInbox\"", StringComparison.Ordinal))
            {
                Inserts++;
                if (_insertFailures > 0)
                {
                    _insertFailures--;
                    throw _insertFault();
                }
            }
            else if (sql.StartsWith("SELECT", StringComparison.Ordinal) &&
                     sql.Contains("FROM \"ProtocolInbox\"", StringComparison.Ordinal))
            {
                Lookups++;
                if (_lookupFailures > 0)
                {
                    _lookupFailures--;
                    throw _lookupFault();
                }
            }
        }
    }
}
