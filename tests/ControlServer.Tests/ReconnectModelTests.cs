using System.Diagnostics;
using System.Globalization;
using System.Text;
using CsCheck;

namespace ControlServer.Tests;

/// <summary>
/// 断线重连的基于模型随机测试（control-server#342），见 <see cref="ReconnectModel"/>。
/// </summary>
public sealed class ReconnectModelTests
{
    /// <summary>固定种子的起点。改它等于换一批组合，旧的失败输出就对不上了。</summary>
    private const ulong MasterSeed = 342;

    /// <summary>
    /// 种子串能原样复现同一个序列：失败输出里打印的种子，交给 CsCheck 的 <c>seed</c> 参数就是这一串，否则化简无从谈起。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public void APrintedSeedRegeneratesTheSameSequence()
    {
        for (int index = 0; index < 20; index++)
        {
            (string seed, ReconnectStep[] steps) = ReconnectModel.Fixed(MasterSeed, index);
            ReconnectStep[] again = ReconnectModel.Sequence.Generate(PCG.Parse(seed), null, out _);
            Assert.Equal(ReconnectModel.Print(steps), ReconnectModel.Print(again));
        }
    }

    /// <summary>
    /// 原型量测（只在显式要求时跑）：按固定种子跑 <c>CS342_ITER</c> 个组合（默认 300），记每个组合的耗时与违规类别；
    /// 对「卡在取货站」先交给 CsCheck 化简，再对最短的那一串做确定性删减。报告写到 <c>CS342_REPORT</c>。
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task PrototypeMeasurement()
    {
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("CS342_ITER"), out int configured) ? configured : 300;
        int shrinkIterations = int.TryParse(Environment.GetEnvironmentVariable("CS342_SHRINK_ITER"), out int shrink) ? shrink : 400;
        string reportPath = Environment.GetEnvironmentVariable("CS342_REPORT") is { Length: > 0 } path
            ? path
            : Path.Combine(Path.GetTempPath(), "cs342-prototype.txt");

        StringBuilder report = new();
        report.AppendLine(CultureInfo.InvariantCulture, $"iterations={iterations} masterSeed={MasterSeed}");
        List<TimeSpan> elapsed = [];
        List<TimeSpan> setups = [];
        List<int> rounds = [];
        Dictionary<ReconnectViolation, List<(string Seed, ReconnectStep[] Steps)>> byViolation = [];
        int ackConflicts = 0;
        int regressions = 0;
        int entryReached = 0;
        Stopwatch total = Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
        {
            (string seed, ReconnectStep[] steps) = ReconnectModel.Fixed(MasterSeed, index);
            ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);
            elapsed.Add(verdict.Elapsed);
            setups.Add(verdict.Setup);
            rounds.Add(verdict.Rounds);
            ackConflicts += verdict.AckConflicts;
            regressions += verdict.Regressions;
            entryReached += verdict.Detail.Contains("entryReachedVehicle=True", StringComparison.Ordinal) ? 1 : 0;
            if (verdict.Violation is { } violation)
            {
                if (!byViolation.TryGetValue(violation, out List<(string Seed, ReconnectStep[] Steps)>? found))
                {
                    byViolation[violation] = found = [];
                    report.AppendLine(CultureInfo.InvariantCulture, $"first {violation}: index={index} seed={seed} steps={steps.Length}");
                    report.AppendLine(verdict.Detail);
                }

                found.Add((seed, steps));
            }
        }

        total.Stop();
        double[] ms = [.. elapsed.Select(span => span.TotalMilliseconds).Order()];
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"per combination (ms): n={ms.Length} mean={ms.Average():F1} p50={ms[ms.Length / 2]:F1} " +
            $"p90={ms[(int)(ms.Length * 0.9)]:F1} max={ms[^1]:F1}; wall total {total.Elapsed.TotalSeconds:F1}s");
        // 第一个组合带着整个引擎的 JIT；CI 里每个组合付的是稳态的价。
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"per combination excluding the first (ms): n={elapsed.Count - 1} mean={elapsed.Skip(1).Average(span => span.TotalMilliseconds):F1}; " +
            $"first={elapsed[0].TotalMilliseconds:F1}");
        report.AppendLine(
            CultureInfo.InvariantCulture,
            $"setup mean={setups.Average(span => span.TotalMilliseconds):F1}ms; engine rounds per combination mean={rounds.Average():F1}; " +
            $"per round (excluding setup) mean={(elapsed.Sum(span => span.TotalMilliseconds) - setups.Sum(span => span.TotalMilliseconds)) / rounds.Sum():F1}ms");
        report.AppendLine(CultureInfo.InvariantCulture, $"ack conflicts={ackConflicts} vehicle regressions={regressions}");
        // 不变量成立的组合里，有多少是真的把录入请求送到了车上，而不是靠「看板上有码」过关的。
        report.AppendLine(CultureInfo.InvariantCulture, $"entry request reached the vehicle in {entryReached} of {iterations}");
        foreach ((ReconnectViolation violation, List<(string Seed, ReconnectStep[] Steps)> found) in byViolation)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{violation}: {found.Count} of {iterations}");
            foreach ((string seed, ReconnectStep[] steps) in found)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {seed} {ReconnectModel.Print(steps)}");
            }
        }

        if (byViolation.TryGetValue(ReconnectViolation.StuckAtPickup, out List<(string Seed, ReconnectStep[] Steps)>? stuck))
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"CsCheck shrinking StuckAtPickup from seed {stuck[0].Seed}, iter={shrinkIterations}");
            Stopwatch shrinkWatch = Stopwatch.StartNew();
            try
            {
                await Check.SampleAsync(
                    ReconnectModel.Sequence,
                    async steps =>
                    {
                        ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);
                        if (verdict.Violation == ReconnectViolation.StuckAtPickup)
                        {
                            throw new ReconnectInvariantException(verdict);
                        }
                    },
                    seed: stuck[0].Seed,
                    iter: shrinkIterations,
                    print: ReconnectModel.Print);
                report.AppendLine("shrink: the seed did not fail again (not reproducible)");
            }
            catch (CsCheckException error)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"shrink took {shrinkWatch.Elapsed.TotalSeconds:F1}s");
                report.AppendLine(error.Message);
            }

            ReconnectStep[] shortest = [.. stuck.Select(item => item.Steps).OrderBy(steps => steps.Length).First()];
            Stopwatch minimizeWatch = Stopwatch.StartNew();
            (ReconnectStep[] minimal, ReconnectVerdict minimalVerdict) =
                await ReconnectModel.MinimizeAsync(shortest, ReconnectViolation.StuckAtPickup);
            report.AppendLine();
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"deterministic minimisation of the shortest StuckAtPickup ({shortest.Length} steps) took {minimizeWatch.Elapsed.TotalSeconds:F1}s:");
            report.AppendLine(CultureInfo.InvariantCulture, $"  from {ReconnectModel.Print(shortest)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"  to   {ReconnectModel.Print(minimal)}");
            report.AppendLine(minimalVerdict.Detail);
        }

        await File.WriteAllTextAsync(reportPath, report.ToString(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 校准（只在显式要求时跑）：手写几串已知答案的序列，看模型在三个提交上给出的红绿是否与 PR #338 记下的一致。
    /// 报告写到 <c>CS342_REPORT</c>。
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task Calibration()
    {
        (string Name, ReconnectStep[] Steps)[] sequences =
        [
            ("cut-after-vbs-no-ack", [new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(2), new ReconnectStep.Round(1)]),
            ("field-immediate-reconnect", [
                new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(3), new ReconnectStep.Round(1), new ReconnectStep.Ack()]),
            ("field-not-ready-round-in-handshake", [
                new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(3), new ReconnectStep.Round(1), new ReconnectStep.Ack(),
                new ReconnectStep.BeginHandshake(), new ReconnectStep.Round(7)]),
            ("field-not-ready-round-before-reconnect", [
                new ReconnectStep.Arrive(), new ReconnectStep.CutAfter(3), new ReconnectStep.Round(1), new ReconnectStep.Ack(),
                new ReconnectStep.VehicleNotReady(false), new ReconnectStep.Round(7)]),
        ];
        string reportPath = Environment.GetEnvironmentVariable("CS342_REPORT") is { Length: > 0 } path
            ? path
            : Path.Combine(Path.GetTempPath(), "cs342-calibration.txt");
        StringBuilder report = new();
        StringBuilder details = new();
        foreach ((string name, ReconnectStep[] steps) in sequences)
        {
            ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"{name} violation={verdict.Violation?.ToString() ?? "none"} {ReconnectModel.Print(steps)}");
            details.AppendLine(CultureInfo.InvariantCulture, $"== {name}");
            details.AppendLine(verdict.Detail);
        }

        report.AppendLine();
        report.Append(details);
        await File.WriteAllTextAsync(reportPath, report.ToString(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// 复跑种子（只在显式要求时跑）：<c>CS342_SEEDS</c>（空格分隔）里每个种子生成的那一串各跑一遍，判定与逐步记录写到
    /// <c>CS342_REPORT</c>。用来在另一个提交上确认「同样的种子不再报」。
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ReplaySeeds()
    {
        string[] seeds = (Environment.GetEnvironmentVariable("CS342_SEEDS")
                ?? throw new InvalidOperationException("CS342_SEEDS is not set."))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(seeds);
        string reportPath = Environment.GetEnvironmentVariable("CS342_REPORT") is { Length: > 0 } path
            ? path
            : Path.Combine(Path.GetTempPath(), "cs342-replay.txt");
        StringBuilder report = new();
        StringBuilder details = new();
        foreach (string seed in seeds)
        {
            ReconnectStep[] steps = ReconnectModel.Sequence.Generate(PCG.Parse(seed), null, out _);
            ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"{seed} violation={verdict.Violation?.ToString() ?? "none"} {ReconnectModel.Print(steps)}");
            details.AppendLine(CultureInfo.InvariantCulture, $"== {seed}");
            details.AppendLine(verdict.Detail);
        }

        report.AppendLine();
        report.Append(details);
        await File.WriteAllTextAsync(reportPath, report.ToString(), TestContext.Current.CancellationToken);
    }
}

/// <summary>不变量不成立时抛出，消息里带着判定与逐步记录，CsCheck 化简时原样打印。</summary>
internal sealed class ReconnectInvariantException(ReconnectVerdict verdict)
    : Exception($"{verdict.Violation}\n{verdict.Detail}")
{
    public ReconnectVerdict Verdict { get; } = verdict;
}
