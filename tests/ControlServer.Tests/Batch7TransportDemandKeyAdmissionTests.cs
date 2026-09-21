using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 批次7-05（control-server#210）判据侧：经公开入口 <see cref="DispatchAdmissionCriteria.Default"/> 加
/// <see cref="DispatchAdmissionChain"/>，被抑制的键、成功过的键、原 <c>DemandId</c>、同 SUBLOT 另一任务类型、新键五例。
/// </summary>
/// <remarks>
/// 「放行」判的是链走过了第 11、12 两道：这里的车没有车载端事实，放行的候选一路走到车辆动态事实那一道，判
/// <c>ONBOARD_FACTS_NOT_READY</c>。所以这五例里放行的答案都是它，而被挡的答案是两个新码或 <c>DEMAND_ALREADY_ACCEPTED</c>。
/// </remarks>
public sealed class Batch7TransportDemandKeyAdmissionTests : IAsyncDisposable
{
    private const string Sublot = "SUBLOT-S1";
    private const string OriginalDemand = "10000000-0000-4000-8000-000000000501";
    private const string NewDemand = "10000000-0000-4000-8000-000000000502";
    private const string PassedTheKeyCriteria = "ONBOARD_FACTS_NOT_READY";

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 6, 0, 0, TimeSpan.Zero);

    private static readonly RiotMapStation Gate = new(210, "关卡");

    private static readonly RiotMapStation Staging = new(305, "派工待送取货");

    private static readonly RiotMapStationCatalogSnapshot Map = new(
        25, Now, new string('c', 64), [new RiotMapStation(12, "N1-1"), Gate, Staging]);

    private static readonly JourneyRuntimeOptions Configured = new()
    {
        MapId = 25,
        AllowedWorkTypes = [.. TransportTaskTypes.All],
        AllowedDispatchZones = ["MAP-25-WIRE_TO_GATE"],
        MinimumBatteryPercent = 30,
        MaximumEvidenceAge = TimeSpan.FromMinutes(2),
    };

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private ControlServerDbContext? _context;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ASuppressedKeyUnderANewDemandIdIsRefusedAsSuppressed()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(OriginalDemand, TransportTaskTypes.WireToGate, DemandExecutionStatus.Cancelled);
        await SuppressAsync(OriginalDemand, TransportTaskTypes.WireToGate, "CANCELLED_BY_OPERATOR");

        string verdict = await chain.EvaluateAsync(
            Evaluation(NewDemand, TransportTaskTypes.WireToGate, acceptedThisServer: [OriginalDemand]), Token);

        Assert.Equal(TransportDemandKeySuppressedCriterion.Reason, verdict);
    }

    /// <summary>
    /// 成功完成、从未取消过的键：没有抑制，挡它的是第 12 道——否则它会走完全部判据，在受理存储层撞唯一索引。
    /// </summary>
    [Fact]
    public async Task AKeyCompletedUnderAnotherDemandIdIsRefusedAsAlreadyAccepted()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(OriginalDemand, TransportTaskTypes.WireToGate, DemandExecutionStatus.Succeeded);

        string verdict = await chain.EvaluateAsync(
            Evaluation(NewDemand, TransportTaskTypes.WireToGate, acceptedThisServer: [OriginalDemand]), Token);

        Assert.Equal(TransportDemandKeyAlreadyAcceptedCriterion.Reason, verdict);
    }

    /// <summary>
    /// 被取消的那一条自己仍在目录里：第 10 道先挡，码不变——既有 L2 场景对它的期待就是这个。它的键有抑制、也有受理行，
    /// 两条新判据都会命中，所以这一例证明的是它们排在第 10 道之后。
    /// </summary>
    [Fact]
    public async Task TheCancelledDemandItselfIsStillRefusedAsAlreadyAccepted()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(OriginalDemand, TransportTaskTypes.WireToGate, DemandExecutionStatus.Cancelled);
        await SuppressAsync(OriginalDemand, TransportTaskTypes.WireToGate, "CANCELLED_BY_OPERATOR");

        string verdict = await chain.EvaluateAsync(
            Evaluation(OriginalDemand, TransportTaskTypes.WireToGate, acceptedThisServer: [OriginalDemand]), Token);

        Assert.Equal(AlreadyAcceptedCriterion.DemandAlreadyAccepted, verdict);
    }

    /// <summary>
    /// 同一 SUBLOT 的另一任务类型是另一个键（REQ-0155 末句）：原键被抑制、也被受理过，这一条照样放行。
    /// </summary>
    [Fact]
    public async Task AnotherTaskTypeOnTheSameSublotIsAnotherKeyAndPasses()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(OriginalDemand, TransportTaskTypes.WireToGate, DemandExecutionStatus.Cancelled);
        await SuppressAsync(OriginalDemand, TransportTaskTypes.WireToGate, "CANCELLED_BY_OPERATOR");

        string verdict = await chain.EvaluateAsync(
            Evaluation(NewDemand, TransportTaskTypes.StagingToWire, acceptedThisServer: [OriginalDemand]), Token);

        Assert.Equal(PassedTheKeyCriteria, verdict);
    }

    [Fact]
    public async Task AKeyNeverAcceptedPasses()
    {
        DispatchAdmissionChain chain = await ChainAsync();

        string verdict = await chain.EvaluateAsync(
            Evaluation(NewDemand, TransportTaskTypes.WireToGate, acceptedThisServer: []), Token);

        Assert.Equal(PassedTheKeyCriteria, verdict);
    }

    /// <summary>
    /// 释放待改派的需求（批次7-10，REQ-0328）以同一个 <c>DemandId</c> 再派：它的受理行就是键上那一行，不算「别的 DemandId」。
    /// 轮次把它从已受理集合里拿掉，第 10 道放它过去；这里证明第 11、12 道也放它过去，否则改派会被本票悄悄关掉。
    /// </summary>
    [Fact]
    public async Task ADemandReleasedForRedispatchUnderItsOwnDemandIdPasses()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(OriginalDemand, TransportTaskTypes.WireToGate, DemandExecutionStatus.Accepted);

        string verdict = await chain.EvaluateAsync(
            Evaluation(OriginalDemand, TransportTaskTypes.WireToGate, acceptedThisServer: []), Token);

        Assert.Equal(PassedTheKeyCriteria, verdict);
    }

    /// <summary>取消过之外还被别的需求在办（进行中）的键，一样挡——「任何状态」。</summary>
    [Fact]
    public async Task AKeyInProgressUnderAnotherDemandIdIsRefusedAsAlreadyAccepted()
    {
        DispatchAdmissionChain chain = await ChainAsync();
        await AcceptAsync(OriginalDemand, TransportTaskTypes.WireToGate, DemandExecutionStatus.Accepted);

        string verdict = await chain.EvaluateAsync(
            Evaluation(NewDemand, TransportTaskTypes.WireToGate, acceptedThisServer: [OriginalDemand]), Token);

        Assert.Equal(TransportDemandKeyAlreadyAcceptedCriterion.Reason, verdict);
    }

    /// <summary>
    /// 两个码进积压列表并带中文说明，且不进结构性阻断——它们是普通积压（有意不执行，不是故障）。
    /// </summary>
    [Theory]
    [InlineData(DispatchReasonCodes.TransportDemandKeySuppressed, "取消")]
    [InlineData(DispatchReasonCodes.TransportDemandKeyAlreadyAccepted, "受理过")]
    public async Task TheDashboardListsTheKeyReasonsAsOrdinaryBacklogWithAChineseDescription(
        string reasonCode, string descriptionMentions)
    {
        await ChainAsync();
        _context!.JourneyBacklog.Add(new JourneyBacklogRow
        {
            DemandId = NewDemand,
            TransportDemandKey = Key(TransportTaskTypes.WireToGate),
            FirstSeenAt = Now.AddMinutes(-5),
            DemandCreatedAt = Now.AddMinutes(-6),
            DecisionFingerprint = "fingerprint",
            ReasonCode = reasonCode,
            LastSeenAt = Now,
        });
        await _context.SaveChangesAsync(Token);
        _context.ChangeTracker.Clear();

        using System.Text.Json.JsonDocument fact = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(
                await new ControlServer.Host.Dashboard.DispatchBacklogQueryEndpoint().ReadAsync(_context, Token)));

        string description = ControlServer.Host.Dashboard.DispatchBacklogQueryEndpoint.Descriptions[reasonCode];
        Assert.Matches(@"\p{IsCJKUnifiedIdeographs}", description);
        Assert.Contains(descriptionMentions, description, StringComparison.Ordinal);
        System.Text.Json.JsonElement row = Assert.Single(fact.RootElement.GetProperty("backlog").EnumerateArray());
        Assert.Equal(reasonCode, row.GetProperty("reasonCode").GetString());
        Assert.Equal(description, row.GetProperty("reasonDescription").GetString());
        Assert.Equal(DispatchReasonClass.Backlog, StructuralDispatchClassification.ClassOf(reasonCode));
        Assert.False(DispatchReasonCodes.IsSilent(reasonCode));
    }

    /// <summary>
    /// 「不经 <c>blockingFacts</c> 下发」：下发给车的阻断事实只由车载端投影的两个发布者构造，它们一个都不认这两个码。
    /// 这是按构造成立的——阻断事实来自恢复会话，不来自积压——这里钉的是有人把积压原因接过去的那一天。
    /// </summary>
    [Fact]
    public void NeitherKeyReasonIsWiredIntoWhatIsSentToTheVehicle()
    {
        string transport = Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "ControlServer.Host", "Transport");
        foreach (string file in Directory.GetFiles(transport, "*.cs"))
        {
            string source = File.ReadAllText(file);
            foreach (string name in new[]
                     {
                         nameof(DispatchReasonCodes.TransportDemandKeySuppressed),
                         nameof(DispatchReasonCodes.TransportDemandKeyAlreadyAccepted),
                         DispatchReasonCodes.TransportDemandKeySuppressed,
                         DispatchReasonCodes.TransportDemandKeyAlreadyAccepted,
                     })
            {
                Assert.DoesNotContain(name, source, StringComparison.Ordinal);
            }
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Key(string workType) => $"{Sublot}|{workType}";

    private async Task<DispatchAdmissionChain> ChainAsync()
    {
        await _connection.OpenAsync(Token);
        _context = new ControlServerDbContext(
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync(Token);
        return new DispatchAdmissionChain(DispatchAdmissionCriteria.Default(
            Options.Create(Configured),
            new MapStationResolver(),
            new PackageCapacityStore(_context),
            new WireToGateStore(_context),
            new VehicleFaultStore(_context),
            new JourneyRuntimeWorkerTestKit.RecordingBoxCounts(),
            NullLogger<SlotCapacityCriterion>.Instance,
            new TransportDemandSuppressionStore(_context),
            _context));
    }

    /// <summary>A row the acceptance would have written, in the given state; only the key and the id matter here.</summary>
    private async Task AcceptAsync(string demandId, string workType, DemandExecutionStatus status)
    {
        await using ControlServerDbContext writing = NewContext();
        writing.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = demandId,
            SeriesId = $"SERIES-{demandId}",
            TransportDemandKey = Key(workType),
            WorkType = workType,
            Sublot = Sublot,
            Generation = 1,
            DemandRevision = 7,
            HistoryEpoch = "11111111-1111-4111-8111-111111111111",
            CatalogRevision = 21,
            CreatedAt = Now.AddMinutes(-30),
            ValueObservedAt = Now.AddMinutes(-29),
            ValuePollTraceId = $"TRACE-{demandId}",
            ValueProjectionCommitId = $"COMMIT-{demandId}",
            LiveMesFieldsJson = "{}",
            AcceptedAt = Now.AddMinutes(-20),
            Status = status,
        });
        await writing.SaveChangesAsync(Token);
    }

    private async Task SuppressAsync(string demandId, string workType, string reasonCode)
    {
        await using ControlServerDbContext writing = NewContext();
        await new TransportDemandSuppressionStore(writing).SuppressIfAbsentAsync(
            new TransportDemandSuppression(Key(workType), demandId, reasonCode, Now.AddMinutes(-10)), Token);
    }

    private ControlServerDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(_connection).Options);

    private static DispatchCandidateEvaluation Evaluation(
        string demandId, string workType, IReadOnlyCollection<string> acceptedThisServer)
    {
        AcceptedDemandSnapshot candidate = new(
            demandId,
            Key(workType),
            7,
            "11111111-1111-4111-8111-111111111111",
            21,
            Now,
            $"SERIES-{demandId}",
            workType,
            Sublot,
            1,
            Now.AddMinutes(-10),
            Now.AddMinutes(-9),
            $"TRACE-{demandId}",
            $"COMMIT-{demandId}",
            new LiveMesFieldSet("N1-1", "EQP-01", "STEP-01", Now.AddMinutes(-10), "PDFN5×6-8L(12R)"));
        DispatchRoundFacts round = new(
            new DemandCatalogSnapshot("11111111-1111-4111-8111-111111111111", 21, [candidate]),
            Map,
            new ScriptedView(taskType => taskType == TransportTaskTypes.StagingToWire
                ? FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Origin, Staging, 1, 1)
                : FixedTaskStationResolution.Resolved(taskType, FixedStationEnd.Destination, Gate, 1, 1)),
            new HashSet<string>(acceptedThisServer, StringComparer.Ordinal),
            Now,
            new VehicleDispatchPolicy(
                [new VehicleDispatchProfile(
                    "AGV-1",
                    new HashSet<string>(TransportTaskTypes.All, StringComparer.Ordinal),
                    30_000)],
                new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
                {
                    ["MAP-25-WIRE_TO_GATE"] = new HashSet<string>(StringComparer.Ordinal) { "AGV-1" },
                },
                "TEST-POLICY"),
            new AreaAssignmentTableVersion(
                1,
                new string('a', 64),
                "snapshot-1",
                Now,
                new Dictionary<string, AreaAssignment>(StringComparer.Ordinal)
                {
                    ["N1-1"] = new("N1-1", "MAP-25-WIRE_TO_GATE", "FRONT"),
                }));
        return new DispatchCandidateEvaluation(
            candidate,
            round,
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now));
    }

    private sealed class ScriptedView(Func<string, FixedTaskStationResolution> resolve) : IFixedTaskStationView
    {
        public FixedTaskStationResolution Resolve(string taskType) => resolve(taskType);
    }
}
