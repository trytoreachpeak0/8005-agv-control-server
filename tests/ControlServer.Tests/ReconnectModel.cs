using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using CsCheck;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// 断线重连的基于模型随机测试（control-server#342）：一串随机动作打在真实的引擎、SQLite 与假车载端上，
/// 走完之后按不变量判一次。
/// </summary>
/// <remarks>
/// <para>
/// 范围只有「车到取货站那一串」：车辆业务状态、清单、到站计划、录入请求。握手在这里是直接写库的
/// （<see cref="RuntimeFixture.ReconnectAsync"/> 不经过 <c>OnboardMessageProcessor</c>），所以
/// 「握手期间每条入站只回一条答复」按构造测不到，那一部分要另走真实处理器。
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

    internal static readonly Gen<ReconnectStep> Step = Gen.Frequency(
        (6, Gen.Int[1, 8].Select(seconds => (ReconnectStep)new ReconnectStep.Round(seconds))),
        (2, Gen.Const<ReconnectStep>(new ReconnectStep.Arrive())),
        (3, Gen.Const<ReconnectStep>(new ReconnectStep.Ack())),
        (2, Gen.Int[0, 4].Select(sends => (ReconnectStep)new ReconnectStep.CutAfter(sends))),
        (2, Gen.Const<ReconnectStep>(new ReconnectStep.BeginHandshake())),
        (2, Gen.Const<ReconnectStep>(new ReconnectStep.CompleteHandshake())),
        (2, Gen.Bool.Select(ownOrder => (ReconnectStep)new ReconnectStep.VehicleNotReady(ownOrder))),
        (1, Gen.Const<ReconnectStep>(new ReconnectStep.VehicleReady())));

    internal static readonly Gen<ReconnectStep[]> Sequence = Step.Array[1, 24];

    internal static string Print(ReconnectStep[] steps) => "[" + string.Join(", ", steps.Select(step => step.ToString())) + "]";

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
        if (current.Violation != target)
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
                if (await RunAsync(without) is { Violation: var violation } verdict && violation == target)
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
                    _ => null,
                };
                if (smaller is null)
                {
                    continue;
                }

                ReconnectStep[] simpler = [.. steps];
                simpler[index] = smaller;
                if (await RunAsync(simpler) is { Violation: var violation } verdict && violation == target)
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

            await ApplyAsync(new ReconnectStep.VehicleReady(), tail: true);
            await ApplyAsync(new ReconnectStep.Arrive(), tail: true);
            for (int round = 0; round < TailRounds; round++)
            {
                await ApplyAsync(new ReconnectStep.Round(2), tail: true);
                await ApplyAsync(new ReconnectStep.Ack(), tail: true);
                if (!_connected)
                {
                    // 确认被拒时真实的处理器会断开这条连接（它抛出，连接结束）；车会再连上来。
                    await ApplyAsync(new ReconnectStep.BeginHandshake(), tail: true);
                    await ApplyAsync(new ReconnectStep.CompleteHandshake(), tail: true);
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

            ReconnectViolation? violation = everyRoundThrows
                ? runtime.Stage == JourneyRuntimeStage.AwaitingPickupArrival
                    ? ReconnectViolation.StuckAtPickup
                    : ReconnectViolation.EveryRoundThrows
                : entryReachedVehicle || waitsVisibly
                    ? null
                    : ReconnectViolation.EntryNeverReachedVehicle;

            StringBuilder detail = new();
            detail.AppendLine(
                CultureInfo.InvariantCulture,
                $"final: stage={runtime.Stage} block={runtime.BlockReasonCode ?? "null"} generation={_generation} " +
                $"entryReachedVehicle={entryReachedVehicle} ackConflicts={_ackConflicts} regressions={_vehicle.Regressions.Count}");
            if (everyRoundThrows)
            {
                detail.AppendLine("last tail rounds: " + string.Join(" | ", lastFailures.Distinct(StringComparer.Ordinal)));
            }

            detail.AppendLine("trace:");
            foreach (string line in _trace)
            {
                detail.AppendLine(line);
            }

            return new ReconnectVerdict(violation, detail.ToString(), _ackConflicts, _vehicle.Regressions.Count, TimeSpan.Zero, default, _rounds);
        }

        public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

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
                await _fixture.HearFromPeerAsync();
            }

            _rounds++;
            int sentBefore = _delivered.Count;
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

        private string? Arrive()
        {
            if (_arrived)
            {
                return "no-op";
            }

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

        /// <summary>车以新的一代连上来：<c>SessionHello</c> 到了，握手还没完成，旧连接上没送到的确认随旧连接丢掉。</summary>
        private async Task<string?> BeginHandshakeAsync()
        {
            _generation++;
            _connected = false;
            _sendsBeforeCut = null;
            _handshakeOpen = true;
            _vehicle.LoseBufferedAcks();
            await _fixture.ReconnectAsync(_generation);
            return null;
        }

        private async Task<string?> CompleteHandshakeAsync()
        {
            if (!_handshakeOpen)
            {
                return "no-op";
            }

            await _fixture.AdvanceSessionAsync(_generation);
            SessionRecoveryRow session = await _fixture.Context.SessionRecoveries.SingleAsync(Token);
            session.RecoveryReportId = Guid.NewGuid().ToString("D");
            ResetSafety(session);
            await _fixture.Context.SaveChangesAsync(Token);
            _handshakeOpen = false;
            _connected = true;
            await _fixture.HearFromPeerAsync();
            return null;
        }

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

        private async Task<string?> VehicleReadyAsync()
        {
            if (_handshakeOpen)
            {
                return "no-op";
            }

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
}

/// <summary>收尾之后的判定：<see cref="Violation"/> 为 null 表示不变量成立。</summary>
internal sealed record ReconnectVerdict(
    ReconnectViolation? Violation,
    string Detail,
    int AckConflicts,
    int Regressions,
    TimeSpan Elapsed,
    TimeSpan Setup = default,
    int Rounds = 0);

internal enum ReconnectViolation
{
    /// <summary>车在取货站，收尾那几轮每轮都抛异常，录入请求没到车上——cs#331 的形状。</summary>
    StuckAtPickup,

    /// <summary>收尾那几轮每轮都抛异常，但不在取货站。</summary>
    EveryRoundThrows,

    /// <summary>引擎不再抛异常，录入请求却从没到车上，看板上也没有说明在等什么的码。</summary>
    EntryNeverReachedVehicle,
}
