using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using CsCheck;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 断线重连的基于模型随机测试（control-server#342）：一串随机动作打在真实的引擎、SQLite 与假车载端上，
/// 走完之后按不变量判一次。
/// </summary>
/// <remarks>
/// <para>
/// 范围：车到取货站那一串（车辆业务状态、清单、到站计划、录入请求），与重连握手。握手经真实的
/// <see cref="OnboardMessageProcessor"/>，车按真车载端的顺序补发持久报文、每条只读一条答复，所以「握手期间每条入站只回一条答复」
/// 与「防重放护栏不拒合法消息」在这里测得到。会话就绪与否的另一半（<see cref="ReconnectStep.VehicleNotReady"/>、
/// <see cref="ReconnectStep.VehicleReady"/>）仍是照夹具的做法直接写库，代表「因为别的原因」未就绪、恢复就绪。
/// </para>
/// <para>
/// 时钟是 <see cref="FixedTimeProvider"/>，只由 <see cref="ReconnectStep.Round"/> 拨动，每一轮都断言它真的走了：
/// 钟不走时「期限作废后从此刻重填」重填出来的还是原来那个时刻，cs#331 的现场时序就测不出来。
/// </para>
/// </remarks>
internal static class ReconnectModel
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";

    /// <summary>推进失败的阻断码，写成字面量：它在 cs#331 修复前的提交上还不存在，这个文件要在那上面也编得过。</summary>
    internal const string AdvanceFailedReason = "JOURNEY_ADVANCE_FAILED";

    /// <summary>收尾时连续几轮都抛异常算「每轮都抛」。</summary>
    private const int FailingTailRounds = 3;

    private const int TailRounds = 8;

    /// <summary>
    /// 指名了在等谁的阻断码：与 <c>JourneyRuntimeEngine.CarriesACodeThatNamesAWaitOnAPerson</c> 同一组，写成字面量——看板上现场看得见
    /// 的东西，改名应当让这里对不上而不是跟着常量悄悄改。<c>Blocked</c> 阶段与 AREA 站等准入要连阶段一起判，这个模型走不到，没列。
    /// </summary>
    private static readonly HashSet<string> WaitOnPersonCodes = new(StringComparer.Ordinal)
    {
        "VEHICLE_ORDER_FAILED",
        "ORDER_HANG",
        "ORDER_STATE_UNRECOGNIZED",
        "ORDER_ENDED_WITHOUT_ARRIVAL",
        "ONBOARD_SESSION_LOST",
        "STATION_TIMEOUT_DOOR_NOT_CLOSED",
        "LOAD_CORRECTION_IN_PROGRESS",
        "PRE_DEPARTURE_SAFETY_NOT_VALID",
    };

    /// <summary>
    /// 握手凭据用一个本类独占的环境变量名：别的测试类在自己的用例里设、清它们自己的那一个，并行跑时互不影响。
    /// 设一次、不清：进程里只有这里读它。
    /// </summary>
    private const string HandshakeCredentialVariable = "CONTROL_SERVER_TEST_CS342_HANDSHAKE_CREDENTIAL";

    private const string HandshakeCredential = "cs342-model-credential-not-a-production-secret";

    private static readonly IConfiguration HandshakeConfiguration = CreateHandshakeConfiguration();

    private static IConfiguration CreateHandshakeConfiguration()
    {
        Environment.SetEnvironmentVariable(HandshakeCredentialVariable, HandshakeCredential);
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{OnboardTransportOptions.SectionName}:CredentialEnvironmentVariable"] = HandshakeCredentialVariable,
            })
            .Build();
    }

    internal static readonly Gen<ReconnectStep> Step = Gen.Frequency(
        (6, Gen.Int[1, 8].Select(seconds => (ReconnectStep)new ReconnectStep.Round(seconds))),
        (2, Gen.Const<ReconnectStep>(new ReconnectStep.Arrive())),
        (3, Gen.Const<ReconnectStep>(new ReconnectStep.Ack())),
        (2, Gen.Int[0, 4].Select(sends => (ReconnectStep)new ReconnectStep.CutAfter(sends))),
        (2, Gen.Const<ReconnectStep>(new ReconnectStep.BeginHandshake())),
        (2, Gen.Const<ReconnectStep>(new ReconnectStep.CompleteHandshake())),
        (2, Gen.Bool.Select(ownOrder => (ReconnectStep)new ReconnectStep.VehicleNotReady(ownOrder))),
        (1, Gen.Const<ReconnectStep>(new ReconnectStep.VehicleReady())),
        (3, Gen.Select(
            Gen.OneOfConst(SafetyKind.Safe, SafetyKind.NotSafe, SafetyKind.OwnOrder),
            Gen.OneOfConst(SafetyDelivery.Delivered, SafetyDelivery.LostInFlight, SafetyDelivery.AckLost),
            (kind, delivery) => (ReconnectStep)new ReconnectStep.SafetyChange(kind, delivery))),
        (1, Gen.Const<ReconnectStep>(new ReconnectStep.OrderHang())),
        (1, Gen.Const<ReconnectStep>(new ReconnectStep.OrderContinue())),
        (1, Gen.Const<ReconnectStep>(new ReconnectStep.ConflictingResend())));

    internal static readonly Gen<ReconnectStep[]> Sequence = Step.Array[1, 24];

    internal static string Print(ReconnectStep[] steps) => "[" + string.Join(", ", steps.Select(step => step.ToString())) + "]";

    /// <summary><see cref="Print"/> 的反向：把失败输出里打印的那一串原样读回来，用来在别的提交上复跑化简后的序列。</summary>
    internal static ReconnectStep[] Parse(string printed)
    {
        string body = printed.Trim().TrimStart('[').TrimEnd(']');
        List<ReconnectStep> steps = [];
        foreach (string token in SplitTopLevel(body))
        {
            string name = token.Split('(')[0];
            string argument = token.Contains('(', StringComparison.Ordinal) ? token[(name.Length + 1)..^1] : string.Empty;
            steps.Add(name switch
            {
                "Round" => new ReconnectStep.Round(int.Parse(argument.TrimEnd('s'), CultureInfo.InvariantCulture)),
                "Arrive" => new ReconnectStep.Arrive(),
                "Ack" => new ReconnectStep.Ack(),
                "CutAfter" => new ReconnectStep.CutAfter(int.Parse(argument, CultureInfo.InvariantCulture)),
                "BeginHandshake" => new ReconnectStep.BeginHandshake(),
                "CompleteHandshake" => new ReconnectStep.CompleteHandshake(),
                "NotReady" => new ReconnectStep.VehicleNotReady(argument == "ownOrder"),
                "Ready" => new ReconnectStep.VehicleReady(),
                "Safety" => new ReconnectStep.SafetyChange(
                    Enum.Parse<SafetyKind>(argument.Split(',')[0]),
                    Enum.Parse<SafetyDelivery>(argument.Split(',')[1])),
                "OrderHang" => new ReconnectStep.OrderHang(),
                "OrderContinue" => new ReconnectStep.OrderContinue(),
                "ConflictingResend" => new ReconnectStep.ConflictingResend(),
                _ => throw new FormatException($"Unknown step '{token}' in {printed}"),
            });
        }

        return [.. steps];
    }

    private static IEnumerable<string> SplitTopLevel(string body)
    {
        int depth = 0;
        int start = 0;
        for (int index = 0; index < body.Length; index++)
        {
            depth += body[index] switch { '(' => 1, ')' => -1, _ => 0 };
            if (body[index] == ',' && depth == 0)
            {
                yield return body[start..index].Trim();
                start = index + 1;
            }
        }

        if (body[start..].Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }

    /// <summary>
    /// 已经开了票、还没修的违规，按「类别 + 说明里必须出现的几段原文」认。CI 那一批遇到它们照样打印，但不判失败；每一条都有一个
    /// 以同一张票为 Skip 原因的确定性回归用例（<see cref="ReconnectModelRegressionTests"/>）。
    /// </summary>
    /// <remarks>
    /// <b>修复票合入时，这里的那一行与那条 Skip 要一起去掉</b>，否则修复之后同一种违规再出现也不会有人知道。认法故意收得窄：
    /// 同一类别里说明对不上的，照样判失败——那是新问题，交分票王开票。
    /// </remarks>
    internal static readonly KnownDefect[] KnownDefects =
    [
        // 握手里补发的 SafetyStateChanged 回了确认又附一行就绪，车把那一行当成下一条的答复。修复在 PR #343。
        new(
            "control-server#340",
            ReconnectViolation.OneInboundManyAnswers,
            ["SafetyStateChanged in the handshake of generation", "answered with DurableAck+SessionReadiness"]),
        // 同一代握手里补发 SafetyStateChanged vN 之后，同为 vN 的安全快照按整行哈希判冲突，握手被拒。
        new(
            "onboard-hmi#206",
            ReconnectViolation.LegitimateMessageRefused,
            ["SafetyStateSnapshot (generation", "handshake open", "ProtocolContentConflictException: safety revision", "has conflicting content"]),
    ];

    /// <summary>这一条违规对应的已知未修缺陷，没有时为 null。</summary>
    internal static KnownDefect? KnownDefectFor(ReconnectViolation violation, string detail) =>
        KnownDefects.FirstOrDefault(defect =>
            defect.Violation == violation &&
            defect.DetailFragments.All(fragment => detail.Contains(fragment, StringComparison.Ordinal)));

    /// <summary>
    /// 第 <paramref name="index"/> 个固定种子与它生成的序列。种子串用 <see cref="Check.SampleAsync{T}(Gen{T}, Func{T, Task}, Action{string}?, string?, long, int, int, Func{T, string}?, ILogger?)"/>
    /// 的 <c>seed</c> 参数就能复现同一个序列，再由 CsCheck 化简。
    /// </summary>
    internal static (string Seed, ReconnectStep[] Steps) Fixed(ulong masterSeed, int index)
    {
        PCG pcg = new(1, masterSeed + (ulong)index);
        string seed = pcg.ToString();
        ReconnectStep[] steps = Sequence.Generate(pcg, null, out _);
        return (seed, steps);
    }

    /// <summary>跑一串动作，收尾，判不变量。返回判定与这一串的逐步记录。</summary>
    internal static async Task<ReconnectVerdict> RunAsync(ReconnectStep[] steps)
    {
        Stopwatch watch = Stopwatch.StartNew();
        await using ReconnectModelRun run = await ReconnectModelRun.StartAsync();
        TimeSpan setup = watch.Elapsed;
        foreach (ReconnectStep step in steps)
        {
            await run.ApplyAsync(step, tail: false);
        }

        ReconnectVerdict verdict = await run.SettleAndJudgeAsync();
        return verdict with { Elapsed = watch.Elapsed, Setup = setup };
    }

    /// <summary>
    /// 在 CsCheck 的化简之后再做一遍确定性的逐步删减：一次去掉一个动作、把参数调到最小，只要仍然违反同一条
    /// <paramref name="target"/> 就留下，直到哪一步都去不掉为止。
    /// </summary>
    /// <remarks>
    /// CsCheck 的化简是随机搜索更小的样本，违规组合只占几个百分点时，几百次里往往一个更小的都碰不到。这一遍确定性、
    /// 可复现，结果就是固定成回归用例的那一串。
    /// </remarks>
    internal static async Task<(ReconnectStep[] Steps, ReconnectVerdict Verdict)> MinimizeAsync(
        ReconnectStep[] steps,
        ReconnectViolation target)
    {
        ReconnectVerdict current = await RunAsync(steps);
        if (!current.Has(target))
        {
            throw new InvalidOperationException($"The sequence to minimise does not violate {target}: {Print(steps)}");
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int index = 0; index < steps.Length; index++)
            {
                ReconnectStep[] without = [.. steps[..index], .. steps[(index + 1)..]];
                if (await RunAsync(without) is { } verdict && verdict.Has(target))
                {
                    (steps, current, changed) = (without, verdict, true);
                    break;
                }
            }

            for (int index = 0; !changed && index < steps.Length; index++)
            {
                ReconnectStep? smaller = steps[index] switch
                {
                    ReconnectStep.Round { Seconds: > 1 } round => new ReconnectStep.Round(round.Seconds - 1),
                    ReconnectStep.CutAfter { Sends: > 0 } cut => new ReconnectStep.CutAfter(cut.Sends - 1),
                    ReconnectStep.VehicleNotReady { OwnOrder: true } => new ReconnectStep.VehicleNotReady(false),
                    ReconnectStep.SafetyChange { Delivery: not SafetyDelivery.Delivered } change =>
                        change with { Delivery = SafetyDelivery.Delivered },
                    _ => null,
                };
                if (smaller is null)
                {
                    continue;
                }

                ReconnectStep[] simpler = [.. steps];
                simpler[index] = smaller;
                if (await RunAsync(simpler) is { } verdict && verdict.Has(target))
                {
                    (steps, current, changed) = (simpler, verdict, true);
                }
            }
        }

        return (steps, current);
    }

    /// <summary>
    /// 一次运行的状态：夹具、车载端的日志替身、连接与握手的开合、按代次记下的车真正收到的报文。
    /// </summary>
    private sealed class ReconnectModelRun : IAsyncDisposable
    {
        private readonly RuntimeFixture _fixture;
        private readonly AdoptingPeer _vehicle;
        private readonly List<string> _trace = [];
        private readonly List<(string MessageType, long Generation)> _delivered = [];
        private readonly List<string?> _tailOutcomes = [];

        private bool _connected = true;
        private bool _handshakeOpen;
        private int? _sendsBeforeCut;
        private long _generation = 1;
        private bool _arrived;
        private int _ackConflicts;
        private int _rounds;
        private bool _orderHung;

        /// <summary>服务端已经收下的安全变化，原样：<see cref="ConflictingResendAsync"/> 拿它们的 messageId 造语义不同的重放。</summary>
        private readonly List<(string MessageId, string Line)> _processedSafetyChanges = [];
        private int _waitOnPersonChances;

        /// <summary>服务端最后一次收到车的任何一条入站的时刻（心跳、握手里的每一条、会话中途的安全变化）。</summary>
        private DateTimeOffset? _lastInboundAt;

        /// <summary>车的持久日志里还没等到确认的报文，按发出的顺序；下一次握手原样补发（只换代次）。</summary>
        private readonly List<(string MessageId, string Line)> _unacknowledged = [];
        private readonly List<(ReconnectViolation Violation, string Detail)> _violations = [];
        private long _acceptedSafetyVersion = 7;
        private SafetyKind _safety = SafetyKind.Safe;
        private long _alarmRevision;
        private ControlServerDbContext? _connectionContext;
        private OnboardMessageProcessor? _processor;
        private OnboardConnectionState? _connection;

        private ReconnectModelRun(RuntimeFixture fixture)
        {
            _fixture = fixture;
            _vehicle = new AdoptingPeer(fixture.Context, fixture.Clock);
            fixture.Peer.OnMessageSent = Deliver;
        }

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        public static async Task<ReconnectModelRun> StartAsync()
        {
            RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
            // 期限要开着：关着时清单里的期限永远是 null，cs#331 现场那一层（期限作废后重填、与车已确认的那一版不同）就不存在。
            fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(30);
            ReconnectModelRun run = new(fixture);
            // 第 1 代是夹具直接播种的，握手已经完成；给它一条经真实处理器的连接，会话中途的安全变化走这一条。
            await run.OpenConnectionAsync();
            run._connection!.AgvId = fixture.Options.AgvId;
            run._connection.SessionGeneration = 1;
            run._connection.CapabilityRevision = 1;
            run._connection.SafetyRevision = 7;
            run._connection.Readiness = SessionReadiness.Ready;
            run._connection.HandshakeCompleted = true;
            fixture.Catalog.Set(fixture.Demand(DemandId, "SUBLOT-001", Now.AddMinutes(-10)));
            fixture.BoxCounts.Set("SUBLOT-001", 4);
            await fixture.Engine.ExecuteOnceAsync(Token);
            await fixture.RecreateEngineAsync();
            JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
            if (runtime.Stage != JourneyRuntimeStage.AwaitingPickupArrival)
            {
                throw new InvalidOperationException($"The model starts on a dispatched journey, got {runtime.Stage}.");
            }

            return run;
        }

        public async Task ApplyAsync(ReconnectStep step, bool tail)
        {
            string? outcome = step switch
            {
                ReconnectStep.Round round => await RoundAsync(round.Seconds),
                ReconnectStep.Arrive => Arrive(),
                ReconnectStep.Ack => await AckAsync(),
                ReconnectStep.CutAfter cut => CutAfter(cut.Sends),
                ReconnectStep.BeginHandshake => await BeginHandshakeAsync(),
                ReconnectStep.CompleteHandshake => await CompleteHandshakeAsync(),
                ReconnectStep.VehicleNotReady notReady => await VehicleNotReadyAsync(notReady.OwnOrder),
                ReconnectStep.VehicleReady => await VehicleReadyAsync(),
                ReconnectStep.SafetyChange change => await SafetyChangeAsync(change.Kind, change.Delivery),
                ReconnectStep.OrderHang => await OrderHangAsync(),
                ReconnectStep.OrderContinue => await OrderContinueAsync(),
                ReconnectStep.ConflictingResend => await ConflictingResendAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
            };
            _trace.Add((tail ? "  tail " : "  ") + step + (outcome is null ? string.Empty : " -> " + outcome));
            if (tail && step is ReconnectStep.Round)
            {
                _tailOutcomes.Add(outcome is not null && outcome.StartsWith("threw ", StringComparison.Ordinal) ? outcome : null);
            }
        }

        /// <summary>
        /// 收尾：连接恢复、会话就绪、车在取货站，然后每轮之后车都确认，给引擎足够多的轮次走到底，再判。
        /// </summary>
        public async Task<ReconnectVerdict> SettleAndJudgeAsync()
        {
            _sendsBeforeCut = null;
            if (!_connected && !_handshakeOpen)
            {
                await ApplyAsync(new ReconnectStep.BeginHandshake(), tail: true);
            }

            if (_handshakeOpen)
            {
                await ApplyAsync(new ReconnectStep.CompleteHandshake(), tail: true);
            }

            if (_connected && _safety != SafetyKind.Safe)
            {
                await ApplyAsync(new ReconnectStep.SafetyChange(SafetyKind.Safe, SafetyDelivery.Delivered), tail: true);
            }

            await ApplyAsync(new ReconnectStep.VehicleReady(), tail: true);
            if (_orderHung)
            {
                await ApplyAsync(new ReconnectStep.OrderContinue(), tail: true);
            }

            await ApplyAsync(new ReconnectStep.Arrive(), tail: true);
            for (int round = 0; round < TailRounds; round++)
            {
                await ApplyAsync(new ReconnectStep.Round(2), tail: true);
                await ApplyAsync(new ReconnectStep.Ack(), tail: true);
                if (!_connected)
                {
                    // 确认被拒、握手被拒或答复不对时连接断了；车会再连上来。
                    await ApplyAsync(new ReconnectStep.BeginHandshake(), tail: true);
                    await ApplyAsync(new ReconnectStep.CompleteHandshake(), tail: true);
                    if (_connected && _safety != SafetyKind.Safe)
                    {
                        await ApplyAsync(new ReconnectStep.SafetyChange(SafetyKind.Safe, SafetyDelivery.Delivered), tail: true);
                    }
                }

                // 走到了就不再多跑：这一轮没抛、录入请求已以当前这一代送到车上、阶段在等录入。
                // 卡住的组合每轮都抛，这一条永远不成立，照样跑满收尾的轮数。
                if (_tailOutcomes[^1] is null &&
                    _delivered.Any(line => line.MessageType == "SublotEntryRequested" && line.Generation == _generation) &&
                    (await _fixture.RuntimeAsync()).Stage == JourneyRuntimeStage.AwaitingSublot)
                {
                    break;
                }
            }

            await _fixture.RecreateEngineAsync();
            JourneyRuntimeRow runtime = await _fixture.RuntimeAsync();
            string[] lastFailures = [.. _tailOutcomes.TakeLast(FailingTailRounds).Where(outcome => outcome is not null).Select(outcome => outcome!)];
            bool everyRoundThrows = lastFailures.Length == FailingTailRounds;
            bool entryReachedVehicle = runtime.Stage == JourneyRuntimeStage.AwaitingSublot
                ? _delivered.Any(line => line.MessageType == "SublotEntryRequested" && line.Generation == _generation)
                : _delivered.Any(line => line.MessageType == "SublotEntryRequested");
            bool waitsVisibly = runtime.BlockReasonCode is { } code &&
                                !string.Equals(code, AdvanceFailedReason, StringComparison.Ordinal);

            ReconnectViolation? journeyViolation = everyRoundThrows
                ? runtime.Stage == JourneyRuntimeStage.AwaitingPickupArrival
                    ? ReconnectViolation.StuckAtPickup
                    : ReconnectViolation.EveryRoundThrows
                : entryReachedVehicle || waitsVisibly
                    ? null
                    : ReconnectViolation.EntryNeverReachedVehicle;

            List<(ReconnectViolation Violation, string Detail)> violations = [.. _violations];
            if (journeyViolation is { } found)
            {
                violations.Add((found, $"stage={runtime.Stage} block={runtime.BlockReasonCode ?? "null"}" +
                    (everyRoundThrows ? " last tail rounds: " + string.Join(" | ", lastFailures.Distinct(StringComparer.Ordinal)) : string.Empty)));
            }

            StringBuilder detail = new();
            detail.AppendLine(
                CultureInfo.InvariantCulture,
                $"final: stage={runtime.Stage} block={runtime.BlockReasonCode ?? "null"} generation={_generation} " +
                $"entryReachedVehicle={entryReachedVehicle} ackConflicts={_ackConflicts} regressions={_vehicle.Regressions.Count}");
            if (everyRoundThrows)
            {
                detail.AppendLine("last tail rounds: " + string.Join(" | ", lastFailures.Distinct(StringComparer.Ordinal)));
            }

            foreach ((ReconnectViolation violation, string text) in violations)
            {
                detail.AppendLine(CultureInfo.InvariantCulture, $"violation {violation}: {text}");
            }

            detail.AppendLine("trace:");
            foreach (string line in _trace)
            {
                detail.AppendLine(line);
            }

            return new ReconnectVerdict(violations, detail.ToString(), _ackConflicts, _vehicle.Regressions.Count, TimeSpan.Zero, default, _rounds)
            {
                WaitOnPersonChances = _waitOnPersonChances,
            };
        }

        public async ValueTask DisposeAsync()
        {
            if (_connectionContext is not null)
            {
                await _connectionContext.DisposeAsync();
            }

            await _fixture.DisposeAsync();
        }

        private Task Deliver(string line)
        {
            if (!_connected)
            {
                throw new IOException("No recovered Onboard peer is connected for the model vehicle.");
            }

            if (_sendsBeforeCut is 0)
            {
                _sendsBeforeCut = null;
                _connected = false;
                throw new IOException("The model connection dropped while this line was being sent.");
            }

            if (_sendsBeforeCut is int remaining)
            {
                _sendsBeforeCut = remaining - 1;
            }

            using JsonDocument document = JsonDocument.Parse(line.TrimEnd('\n'));
            JsonElement root = document.RootElement;
            _delivered.Add((
                root.GetProperty("messageType").GetString()!,
                root.TryGetProperty("sessionGeneration", out JsonElement generation) && generation.ValueKind == JsonValueKind.Number
                    ? generation.GetInt64()
                    : -1));
            _vehicle.Receive(line);
            return Task.CompletedTask;
        }

        private async Task<string?> RoundAsync(int seconds)
        {
            DateTimeOffset before = _fixture.Clock.GetUtcNow();
            _fixture.Clock.Advance(TimeSpan.FromSeconds(seconds));
            if (_fixture.Clock.GetUtcNow() <= before)
            {
                throw new InvalidOperationException("The model clock did not move; a frozen clock hides every reset-to-now.");
            }

            if (_connected && !_handshakeOpen)
            {
                _fixture.Context.ChangeTracker.Clear();
                await _fixture.HearFromPeerAsync();
                _lastInboundAt = _fixture.Clock.GetUtcNow();
            }

            _rounds++;
            int sentBefore = _delivered.Count;
            JourneyRuntimeRow beforeRound = await _fixture.RuntimeAsync();
            string? outcome;
            try
            {
                await _fixture.Engine.ExecuteOnceAsync(Token);
                outcome = null;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                outcome = "threw " + error.GetType().Name + ": " + FirstLine(error.Message);
            }

            // 宿主每一轮开一个新的作用域，失败那一轮没保存的改动不会带到下一轮。
            await _fixture.RecreateEngineAsync();
            JourneyRuntimeRow runtime = await _fixture.RuntimeAsync();
            CheckWaitOnPersonKept(beforeRound, runtime, threw: outcome is not null);
            string sent = string.Join(
                ",",
                _delivered.Skip(sentBefore).Select(line => ShortName(line.MessageType) + "@" + line.Generation));
            // 离站等待的起点记成「开始后第几秒」：期限作废、重填成此刻，都在这一栏看得见。
            string wait = runtime.StationDepartureWaitStartedAt is { } started
                ? "+" + (started - Now).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s"
                : "-";
            return $"{outcome ?? "ok"}; stage={runtime.Stage} block={runtime.BlockReasonCode ?? "-"} wait={wait}" +
                   (sent.Length == 0 ? string.Empty : "; delivered " + sent);
        }

        /// <summary>
        /// 不变量：「等人」类阻断码不被推进失败覆盖，码没变时起点不重置（control-server#331 审查必修 2）。每一轮都判，不等收尾。
        /// </summary>
        /// <remarks>
        /// 只判这两件：换成 <c>JOURNEY_ADVANCE_FAILED</c>，以及码没变、开始时间却变了。等人码被别的码换掉、或者清掉（人在 RIoT 里继续了），
        /// 这里不判——那是别的规则管的，混进来会把合法的清码也算成违规。
        /// </remarks>
        private void CheckWaitOnPersonKept(JourneyRuntimeRow before, JourneyRuntimeRow after, bool threw)
        {
            if (before.BlockReasonCode is not { } code || !WaitOnPersonCodes.Contains(code))
            {
                return;
            }

            // 失联码说的是「车不说话」：写下它之后车又被听到过（心跳、握手、任何一条入站），它就不再成立，引擎先把它清掉是对的，
            // 之后这一轮再失败、写推进失败，不是覆盖。只在写下之后车一直没说话时判它（cs#331 的 PR 剩余风险里写的正是这一点：
            // 车一被听到就先清掉这个码）。只看「这一轮开头连没连着」不够：握手完成之后没跑一轮就又断了，车说过话，却不算连着。
            if (string.Equals(code, "ONBOARD_SESSION_LOST", StringComparison.Ordinal) &&
                _lastInboundAt is { } heard && heard >= before.BlockReasonSince)
            {
                return;
            }

            // 这条不变量有过几次出事的机会：带着等人码进来、这一轮又失败了。没有这样的轮次，它不出违规也说明不了什么。
            _waitOnPersonChances += threw ? 1 : 0;

            if (string.Equals(after.BlockReasonCode, AdvanceFailedReason, StringComparison.Ordinal))
            {
                Violate(
                    ReconnectViolation.WaitOnPersonOverwritten,
                    $"{code} (since {before.BlockReasonSince:O}) was overwritten by {AdvanceFailedReason} at stage {after.Stage}");
            }
            else if (string.Equals(after.BlockReasonCode, code, StringComparison.Ordinal) &&
                     after.BlockReasonSince != before.BlockReasonSince)
            {
                Violate(
                    ReconnectViolation.WaitOnPersonOverwritten,
                    $"{code} kept its code but its start moved from {before.BlockReasonSince:O} to {after.BlockReasonSince:O}");
            }
        }

        /// <summary>
        /// 不变量「防重放护栏不放过语义不同的消息」：拿一条服务端已经收下的安全变化，messageId 不变、安全内容改掉，在当前这一代再发一次。
        /// 服务端必须拒绝（抛异常、连接随之结束，或者不回 <c>DurableAck</c>）；回了 <c>DurableAck</c> 就是把两件不同的事当成了同一件。
        /// </summary>
        private async Task<string?> ConflictingResendAsync()
        {
            if (!_connected || _handshakeOpen || _processedSafetyChanges.Count == 0)
            {
                return "no-op";
            }

            (string messageId, string original) = _processedSafetyChanges[^1];
            JsonNode forged = JsonNode.Parse(Rebind(original, _generation))!;
            JsonNode safety = forged["payload"]!["safety"]!;
            bool departureSafe = safety["departureSafe"]!.GetValue<bool>();
            safety["departureSafe"] = !departureSafe;
            safety["reasonCodes"] = departureSafe ? new JsonArray("SLOT_LOCK_UNKNOWN") : new JsonArray();
            string[]? answers = await ExchangeAsync(forged.ToJsonString(), "SafetyStateChanged", refusalExpected: true);
            if (answers is null)
            {
                return $"resend of {messageId[..8]} with different content refused";
            }

            string[] types = [.. answers.Select(MessageTypeOf)];
            if (types.FirstOrDefault() == "DurableAck")
            {
                Violate(
                    ReconnectViolation.DifferentMessageAccepted,
                    $"SafetyStateChanged {messageId} resent with different safety content was answered with {string.Join("+", types)}");
            }

            return $"resend of {messageId[..8]} with different content answered {string.Join("+", types)}";
        }

        /// <summary>RIoT 报告去取货站的那张单 HANG（9）：车停住了，只有人能在 RIoT 里继续或取消它。到站之后没有在途单，什么也不做。</summary>
        private async Task<string?> OrderHangAsync()
        {
            if (_arrived || _orderHung)
            {
                return "no-op";
            }

            _fixture.Riot.SetOrderState(await PickupUpperIdAsync(), RiotOrderState.Hang, terminal: false);
            _orderHung = true;
            return null;
        }

        /// <summary>有人在 RIoT 里让那张单继续：回到执行中（3）。</summary>
        private async Task<string?> OrderContinueAsync()
        {
            if (!_orderHung)
            {
                return "no-op";
            }

            _fixture.Riot.SetOrderState(await PickupUpperIdAsync(), OrderRunning, terminal: false);
            _orderHung = false;
            return null;
        }

        /// <summary>执行中：夹具建单时报的那个状态（<c>RecordingRiot.CreateAsync</c>）。</summary>
        private const int OrderRunning = 3;

        private async Task<string> PickupUpperIdAsync() =>
            (await _fixture.Context.OrderIntents.AsNoTracking().SingleAsync(row => row.Purpose == "TO_PICKUP", Token)).UpperId;

        private string? Arrive()
        {
            if (_arrived)
            {
                return "no-op";
            }

            // 到站就是那张单走完了：挂着的也算有人继续过了。
            _orderHung = false;
            JourneyRuntimeRow runtime = _fixture.Context.JourneyRuntimes.AsNoTracking().Single();
            _fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
            _fixture.Riot.Vehicle = _fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
            _arrived = true;
            return null;
        }

        private async Task<string?> AckAsync()
        {
            try
            {
                await _vehicle.DeliverBufferedAcksAsync();
                return null;
            }
            catch (ProtocolContentConflictException error)
            {
                // 真实处理器在这里抛出，连接随之结束；剩下的确认跟着连接一起丢了。
                _ackConflicts++;
                _vehicle.LoseBufferedAcks();
                _fixture.Context.ChangeTracker.Clear();
                _connected = false;
                _sendsBeforeCut = null;
                return "ack refused, connection closed: " + FirstLine(error.Message);
            }
        }

        private string? CutAfter(int sends)
        {
            if (!_connected)
            {
                return "no-op";
            }

            if (sends == 0)
            {
                _connected = false;
                _sendsBeforeCut = null;
                return null;
            }

            _sendsBeforeCut = sends;
            return null;
        }

        /// <summary>
        /// 车以新的一代连上来，经真实的 <see cref="OnboardMessageProcessor"/>：<c>SessionHello</c>，然后按真车载端的做法
        /// 把上一个会话没等到确认的持久报文逐条补发（<c>WireToGateSessionClient.cs:720-734</c>），每条只读一条答复、
        /// 要求它是 <c>DurableAck</c>（<c>:1847-1849</c>）。旧连接上没送到的快照确认随旧连接丢掉。
        /// </summary>
        /// <remarks>
        /// 握手期间连接还没挂到 <c>OnboardPeer</c> 上（<c>OnboardTcpServer</c> 在恢复报告答复之后才挂），服务端这一侧往车上发的
        /// 任何东西都按「没连上」失败，与真实的一样。答复不对时车断开，这一次握手到此为止，车下次再连。
        /// </remarks>
        private async Task<string?> BeginHandshakeAsync()
        {
            _connected = false;
            _sendsBeforeCut = null;
            _vehicle.LoseBufferedAcks();
            await OpenConnectionAsync();
            string hello = VehicleLine(
                "SessionHello",
                new { protocolReleaseIdentity = ReleaseIdentity(), credentialProof = HandshakeCredential },
                generation: null);
            string[]? accepted = await HandshakeExchangeAsync(hello, "SessionHello", ["SessionAccepted"]);
            if (accepted is null)
            {
                return "handshake refused at SessionHello";
            }

            _generation = _connection!.SessionGeneration!.Value;
            _handshakeOpen = true;
            foreach ((string messageId, string line) in _unacknowledged.ToArray())
            {
                string resent = Rebind(line, _generation);
                string[]? answers = await HandshakeExchangeAsync(resent, MessageTypeOf(line), ["DurableAck"]);
                if (answers is null)
                {
                    return $"handshake broke on the resent {MessageTypeOf(line)}";
                }

                // 车读到的第一行就是这一条的答复：它认了这一条（真车载端写日志、不再补发）。
                _unacknowledged.RemoveAll(entry => entry.MessageId == messageId);
                if (!_handshakeOpen)
                {
                    return $"handshake broke after the resent {MessageTypeOf(line)}";
                }
            }

            return _unacknowledged.Count == 0 ? null : "resends left";
        }

        /// <summary>握手的其余部分：能力快照、安全快照（版本号取车已接受的那一版）、告警快照、恢复报告。</summary>
        private async Task<string?> CompleteHandshakeAsync()
        {
            if (!_handshakeOpen)
            {
                return "no-op";
            }

            (string Type, string Line, string[] Expected)[] rest =
            [
                ("CapabilitySnapshot", VehicleLine("CapabilitySnapshot", CapabilityPayload(), _generation), ["SnapshotAppliedAck"]),
                ("SafetyStateSnapshot", VehicleLine("SafetyStateSnapshot", SafetySnapshotPayload(), _generation), ["SnapshotAppliedAck"]),
                ("OnboardAlarmSnapshot", VehicleLine(
                    "OnboardAlarmSnapshot",
                    new { alarmSnapshotRevision = ++_alarmRevision, observedAt = _fixture.Clock.GetUtcNow(), alarms = Array.Empty<object>() },
                    _generation), ["SnapshotAppliedAck"]),
                ("RecoveryStateReport", VehicleLine("RecoveryStateReport", RecoveryReportPayload(), _generation), ["DurableAck", "SessionReadiness"]),
            ];
            foreach ((string type, string line, string[] expected) in rest)
            {
                if (await HandshakeExchangeAsync(line, type, expected) is null || (!_handshakeOpen && type != "RecoveryStateReport"))
                {
                    return $"handshake broke at {type}";
                }
            }

            await _fixture.HearFromPeerAsync();
            return null;
        }

        /// <summary>
        /// 车的安全状态变了：发一条 <c>SafetyStateChanged</c>，版本号是已接受的下一版，发出即推进已接受的版本
        /// （<c>WireToGateSessionClient.cs:630</c>），并记进持久日志，等到确认才划掉。
        /// </summary>
        /// <remarks>
        /// <see cref="SafetyDelivery.Delivered"/>：服务端收到、车收到确认。<see cref="SafetyDelivery.LostInFlight"/>：连接在这一条
        /// 送到之前断了。<see cref="SafetyDelivery.AckLost"/>：服务端收下了，确认没回到车上，连接随即断了——下一次握手补发它，
        /// 服务端按重复到达回答。连接不在或握手没完成时，这一条只进日志，等下一次握手补发。
        /// </remarks>
        private async Task<string?> SafetyChangeAsync(SafetyKind kind, SafetyDelivery delivery)
        {
            _safety = kind;
            long version = ++_acceptedSafetyVersion;
            string messageId = Guid.NewGuid().ToString("D");
            string line = VehicleLine(
                "SafetyStateChanged",
                new
                {
                    safetyStateVersion = version,
                    observedAt = _fixture.Clock.GetUtcNow(),
                    safety = SafetySummary(kind),
                    affectedSlots = Array.Empty<int>(),
                },
                _generation,
                messageId);
            _unacknowledged.Add((messageId, line));
            if (!_connected || _handshakeOpen)
            {
                return $"v{version} journalled, not sent";
            }

            if (delivery == SafetyDelivery.LostInFlight)
            {
                DropConnection();
                return $"v{version} lost in flight, connection dropped";
            }

            string[]? answers = await ExchangeAsync(line, "SafetyStateChanged");
            if (answers is null)
            {
                return $"v{version} refused";
            }

            _processedSafetyChanges.Add((messageId, line));

            if (delivery == SafetyDelivery.AckLost)
            {
                DropConnection();
                return $"v{version} taken, its ack lost, connection dropped";
            }

            if (answers.FirstOrDefault() is { } first && MessageTypeOf(first) == "DurableAck")
            {
                _unacknowledged.RemoveAll(entry => entry.MessageId == messageId);
            }

            return $"v{version} answered {string.Join("+", answers.Select(MessageTypeOf))}";
        }

        private async Task OpenConnectionAsync()
        {
            if (_connectionContext is not null)
            {
                await _connectionContext.DisposeAsync();
            }

            // 一条 TCP 连接一个作用域、一个上下文，与 OnboardTcpServer 一样；与引擎的上下文分开。
            _connectionContext = _fixture.OpenConnectionContext();
            _processor = TestOnboardProcessorFactory.Create(
                _connectionContext,
                new WireToGateStore(_connectionContext),
                _fixture.Clock,
                HandshakeConfiguration,
                _fixture.Peer,
                _fixture.Options);
            _connection = new OnboardConnectionState { DeferOutboundUntilResponseWritten = true };
        }

        private void DropConnection()
        {
            _connected = false;
            _handshakeOpen = false;
            _sendsBeforeCut = null;
        }

        /// <summary>
        /// 握手里的一条：一条入站，读答复。答复的行数与第一行的类型都要对；不对就记违规、车断开。
        /// 服务端抛异常（真实连接随之结束）同样记违规。
        /// </summary>
        private async Task<string[]?> HandshakeExchangeAsync(string line, string messageType, string[] expected)
        {
            string[]? answers = await ExchangeAsync(line, messageType, attachOnHandshakeDone: true);
            if (answers is null)
            {
                return null;
            }

            string[] types = [.. answers.Select(MessageTypeOf)];
            _trace.Add($"    handshake {messageType} -> {string.Join("+", types)}");
            if (types.Length > expected.Length)
            {
                Violate(
                    ReconnectViolation.OneInboundManyAnswers,
                    $"{messageType} in the handshake of generation {_generation} was answered with {string.Join("+", types)}, expected {string.Join("+", expected)}");
                DropConnection();
                return answers;
            }

            if (!types.SequenceEqual(expected))
            {
                Violate(
                    ReconnectViolation.UnexpectedAnswer,
                    $"{messageType} in the handshake of generation {_generation} was answered with {string.Join("+", types)}, expected {string.Join("+", expected)}");
                DropConnection();
                return answers;
            }

            return answers;
        }

        /// <summary>
        /// 一条入站经处理器，答复按 <c>OnboardTcpServer</c> 的顺序：先写答复，再冲掉延后的发送。恢复报告答复之后连接挂上
        /// （<paramref name="attachOnHandshakeDone"/>），延后的发送因此送得出去。
        /// </summary>
        private async Task<string[]?> ExchangeAsync(
            string line,
            string messageType,
            bool attachOnHandshakeDone = false,
            bool refusalExpected = false)
        {
            // 服务端读到了这一行，不论它随后怎么答：车被听到了。
            _lastInboundAt = _fixture.Clock.GetUtcNow();
            try
            {
                string response = await _processor!.ProcessAsync(line, _connection!, Token);
                string[] answers = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (attachOnHandshakeDone && _connection!.HandshakeCompleted && _handshakeOpen)
                {
                    _handshakeOpen = false;
                    _connected = true;
                }

                await _processor.FlushDeferredOutboundAsync(_connection!, Token);
                return answers;
            }
            catch (Exception error) when (error is not OperationCanceledException && refusalExpected)
            {
                _trace.Add($"    refused as expected: {error.GetType().Name}: {FirstLine(error.Message)}");
                _connectionContext!.ChangeTracker.Clear();
                DropConnection();
                return null;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Violate(
                    ReconnectViolation.LegitimateMessageRefused,
                    $"{messageType} (generation {_generation}, handshake {(_handshakeOpen ? "open" : "done")}) refused: {error.GetType().Name}: {FirstLine(error.Message)}");
                _connectionContext!.ChangeTracker.Clear();
                DropConnection();
                return null;
            }
        }

        private void Violate(ReconnectViolation violation, string detail)
        {
            _violations.Add((violation, detail));
            _trace.Add("  !! " + violation + ": " + detail);
        }

        private string VehicleLine(string messageType, object payload, long? generation, string? messageId = null) =>
            JsonSerializer.Serialize(new
            {
                protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                profileId = ProtocolCandidateIdentity.ProfileId,
                protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                messageType,
                messageId = messageId ?? Guid.NewGuid().ToString("D"),
                correlationId = (string?)null,
                agvId = _fixture.Options.AgvId,
                sessionGeneration = generation,
                sentAt = _fixture.Clock.GetUtcNow(),
                payload,
            }, SerializerOptions);

        /// <summary>补发：同一行，只把代次换成新的一代（ADR-cross-0030，messageId 不变）。</summary>
        private static string Rebind(string line, long generation)
        {
            JsonNode node = JsonNode.Parse(line)!;
            node["sessionGeneration"] = generation;
            return node.ToJsonString();
        }

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
            vectorsSha256 = ProtocolCandidateIdentity.VectorsSha256,
        };

        /// <summary>与夹具播种的那一份相同：八个仓位、空、锁着。</summary>
        private object CapabilityPayload() => new
        {
            capabilityVersion = 1,
            observedAt = _fixture.Clock.GetUtcNow(),
            slotModelVersion = "SLOT-MODEL-1",
            activeSlotConfigurationVersion = "SLOT-CONFIG-1",
            activeSlotConfigurationFingerprint = new string('0', 64),
            slotStates = SlotStates(),
            supportsBatchUnlock = true,
            onboardJournalFormatVersion = 1,
        };

        /// <summary>握手里的安全快照：内容取车此刻的安全状态，版本号取已接受的那一版（<c>WireToGateSessionClient.cs:756-757</c>）。</summary>
        private object SafetySnapshotPayload() => new
        {
            safetyStateVersion = _acceptedSafetyVersion,
            observedAt = _fixture.Clock.GetUtcNow(),
            safety = SafetySummary(_safety),
            slotStates = SlotStates(),
        };

        private static object RecoveryReportPayload() => new
        {
            reportId = Guid.NewGuid().ToString("D"),
            unsettledSlotOperationAttemptId = (string?)null,
            provenRecoveryCheckpoint = "NONE",
            activeUnlockSlots = Array.Empty<int>(),
            forcedRecoveryGeneration = 0,
            pendingResults = Array.Empty<object>(),
        };

        private static object[] SlotStates() =>
        [
            .. Enumerable.Range(1, 8).Select(slot => (object)new
            {
                slotNo = slot,
                operability = "OPERABLE",
                administrativeAvailability = "ENABLED",
                physicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>(),
            }),
        ];

        /// <summary>
        /// 安全摘要。<see cref="SafetyKind.OwnOrder"/> 是真车载端挂着本服务端在途单时报的样子（失败现场 <c>SessionRecoveries</c>，
        /// 见 <see cref="PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync"/>）：不安全、有未知、原因只有 <c>VEHICLE_NOT_READY</c>。
        /// </summary>
        private static SafetySummaryWire SafetySummary(SafetyKind kind) => kind switch
        {
            SafetyKind.Safe => new(true, true, true, true, false, []),
            SafetyKind.OwnOrder => new(false, false, true, true, true, ["VEHICLE_NOT_READY"]),
            _ => new(false, true, true, true, false, ["SLOT_LOCK_UNKNOWN"]),
        };

        /// <summary>协议的 <c>safety</c> 摘要；按夹具的 Web 序列化选项写成 camelCase。</summary>
        private sealed record SafetySummaryWire(
            bool DepartureSafe,
            bool VehicleStopped,
            bool AllTargetSlotsLocked,
            bool AllUnlockOutputsReset,
            bool UnknownPresent,
            string[] ReasonCodes);

        /// <summary>
        /// 车在当前这一代上报未就绪。<paramref name="ownOrder"/> 为真时是真车载端挂着本服务端在途单时的形状
        /// （<see cref="PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync"/>），否则是一般的离站安全未就绪。
        /// </summary>
        private async Task<string?> VehicleNotReadyAsync(bool ownOrder)
        {
            if (_handshakeOpen)
            {
                return "no-op";
            }

            _fixture.Context.ChangeTracker.Clear();
            if (ownOrder)
            {
                await PickupDispatchPlanPastOwnOrderTests.DropSessionOnOwnOrderAsync(_fixture);
            }
            else
            {
                await _fixture.DropOnboardSessionAsync();
            }

            return null;
        }

        /// <summary>车在当前这一代上回到就绪。车不在线（断了、握手没完成）时报不了，什么也不做。</summary>
        private async Task<string?> VehicleReadyAsync()
        {
            if (_handshakeOpen || !_connected)
            {
                return "no-op";
            }

            _fixture.Context.ChangeTracker.Clear();
            SessionRecoveryRow session = await _fixture.Context.SessionRecoveries.SingleAsync(Token);
            session.Readiness = SessionReadiness.Ready;
            session.ReasonCode = "READY";
            ResetSafety(session);
            session.UpdatedAt = _fixture.Clock.GetUtcNow();
            await _fixture.Context.SaveChangesAsync(Token);
            return null;
        }

        /// <summary>回到夹具播种时的安全摘要：离站安全、没有原因码、没有未知。</summary>
        private static void ResetSafety(SessionRecoveryRow session)
        {
            session.DepartureSafe = true;
            session.SafetyReasonCodesJson = null;
            session.SafetyUnknownPresent = null;
        }

        private static string MessageTypeOf(string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("messageType").GetString()!;
        }

        private static string FirstLine(string message)
        {
            int newline = message.IndexOf('\n', StringComparison.Ordinal);
            return newline < 0 ? message : message[..newline].TrimEnd('\r');
        }

        private static string ShortName(string messageType) => messageType switch
        {
            "VehicleBusinessStateSnapshot" => "VBS",
            "CurrentStopWorklistSnapshot" => "Worklist",
            "UpcomingStopPlanSnapshot" => "Plan",
            "SublotEntryRequested" => "Entry",
            _ => messageType,
        };
    }
}

/// <summary>模型里的一个动作。<see cref="object.ToString"/> 就是化简后打印出来的样子。</summary>
internal abstract record ReconnectStep
{
    /// <summary>时钟走 <paramref name="Seconds"/> 秒，车若连着就先发一次心跳，然后引擎跑一轮。</summary>
    internal sealed record Round(int Seconds) : ReconnectStep
    {
        public override string ToString() => $"Round({Seconds}s)";
    }

    /// <summary>RIoT 报告车到了取货站。</summary>
    internal sealed record Arrive : ReconnectStep
    {
        public override string ToString() => "Arrive";
    }

    /// <summary>车对它已收到的快照的确认送到服务端（可以晚于好几轮）。</summary>
    internal sealed record Ack : ReconnectStep
    {
        public override string ToString() => "Ack";
    }

    /// <summary>连接在接下来第 <paramref name="Sends"/> 条发送之后断开；0 表示现在就断。</summary>
    internal sealed record CutAfter(int Sends) : ReconnectStep
    {
        public override string ToString() => $"CutAfter({Sends})";
    }

    /// <summary>车以新的一代重连，握手开始（会话未就绪，发送仍不可达）。</summary>
    internal sealed record BeginHandshake : ReconnectStep
    {
        public override string ToString() => "BeginHandshake";
    }

    /// <summary>握手完成，会话回到就绪，连接可达。</summary>
    internal sealed record CompleteHandshake : ReconnectStep
    {
        public override string ToString() => "CompleteHandshake";
    }

    /// <summary>车在当前这一代上报未就绪；<paramref name="OwnOrder"/> 为真时是「本服务端自己的在途单导致」的形状。</summary>
    internal sealed record VehicleNotReady(bool OwnOrder) : ReconnectStep
    {
        public override string ToString() => OwnOrder ? "NotReady(ownOrder)" : "NotReady";
    }

    /// <summary>车在当前这一代上回到就绪。</summary>
    internal sealed record VehicleReady : ReconnectStep
    {
        public override string ToString() => "Ready";
    }

    /// <summary>RIoT 报告去取货站的那张单挂起（HANG）。</summary>
    internal sealed record OrderHang : ReconnectStep
    {
        public override string ToString() => "OrderHang";
    }

    /// <summary>车（或别的什么）拿一条服务端已收下的安全变化的 messageId，改掉内容再发一次。</summary>
    internal sealed record ConflictingResend : ReconnectStep
    {
        public override string ToString() => "ConflictingResend";
    }

    /// <summary>有人在 RIoT 里让挂起的那张单继续。</summary>
    internal sealed record OrderContinue : ReconnectStep
    {
        public override string ToString() => "OrderContinue";
    }

    /// <summary>车的安全状态变了，经真实处理器发一条 <c>SafetyStateChanged</c>；<paramref name="Delivery"/> 决定它与它的确认到没到。</summary>
    internal sealed record SafetyChange(SafetyKind Kind, SafetyDelivery Delivery) : ReconnectStep
    {
        public override string ToString() => $"Safety({Kind},{Delivery})";
    }
}

internal enum SafetyKind
{
    Safe,
    NotSafe,

    /// <summary>真车载端挂着本服务端在途单时的样子：不安全、有未知、原因只有 <c>VEHICLE_NOT_READY</c>。</summary>
    OwnOrder,
}

internal enum SafetyDelivery
{
    Delivered,
    LostInFlight,
    AckLost,
}

/// <summary>收尾之后的判定：<see cref="Violations"/> 为空表示不变量成立。</summary>
internal sealed record ReconnectVerdict(
    IReadOnlyList<(ReconnectViolation Violation, string Detail)> Violations,
    string Detail,
    int AckConflicts,
    int Regressions,
    TimeSpan Elapsed,
    TimeSpan Setup = default,
    int Rounds = 0)
{
    /// <summary>带着「等人」码进来、这一轮又失败了的轮数：<see cref="ReconnectViolation.WaitOnPersonOverwritten"/> 出事的机会。</summary>
    public int WaitOnPersonChances { get; init; }

    /// <summary>第一条违规，没有时为 null。</summary>
    public ReconnectViolation? Violation => Violations.Count == 0 ? null : Violations[0].Violation;

    public bool Has(ReconnectViolation violation) => Violations.Any(item => item.Violation == violation);
}

internal enum ReconnectViolation
{
    /// <summary>车在取货站，收尾那几轮每轮都抛异常，录入请求没到车上——cs#331 的形状。</summary>
    StuckAtPickup,

    /// <summary>收尾那几轮每轮都抛异常，但不在取货站。</summary>
    EveryRoundThrows,

    /// <summary>引擎不再抛异常，录入请求却从没到车上，看板上也没有说明在等什么的码。</summary>
    EntryNeverReachedVehicle,

    /// <summary>握手期间一条入站回了不止一条答复：车每发一条只读一条，多出来的会被当成下一条的答复——cs#340 的形状。</summary>
    OneInboundManyAnswers,

    /// <summary>握手期间一条入站的答复类型不对。</summary>
    UnexpectedAnswer,

    /// <summary>一条合法的入站被服务端拒绝（抛异常，真实连接随之结束）——防重放护栏拒了不该拒的。</summary>
    LegitimateMessageRefused,

    /// <summary>同一个 messageId、语义不同的一条被当成已收下的那一条回了确认——防重放护栏放过了不该放的。</summary>
    DifferentMessageAccepted,

    /// <summary>「等人」类阻断码被推进失败覆盖，或码没变而开始时间重置了（control-server#331 审查必修 2）。</summary>
    WaitOnPersonOverwritten,
}

/// <summary>一张已经开了、还没修的票，以及认出它的违规的办法。</summary>
internal sealed record KnownDefect(string Ticket, ReconnectViolation Violation, string[] DetailFragments);
