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

    /// <summary>CI 里每次跑的组合数，理由见 <see cref="EverySeededSequenceEndsWithTheEntryRequestOnTheVehicle"/>。</summary>
    private const int CiCombinations = 200;

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
    /// 打印出来的序列能原样读回：失败输出里的最短序列交给 <see cref="ReplaySequences"/>（<c>CS342_SEQUENCES</c>）就能在别的提交上复跑。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public void APrintedSequenceParsesBackToItself()
    {
        HashSet<Type> seen = [];
        for (int index = 0; index < 50; index++)
        {
            (_, ReconnectStep[] steps) = ReconnectModel.Fixed(MasterSeed, index);
            Assert.Equal(steps, ReconnectModel.Parse(ReconnectModel.Print(steps)));
            seen.UnionWith(steps.Select(step => step.GetType()));
        }

        // 五十串里每一种动作都出现过，否则没出现的那一种读回来对不对没被核对过。
        Assert.Equal(
            typeof(ReconnectStep).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).Where(type => type.IsSubclassOf(typeof(ReconnectStep))).Order(TypeNameComparer.Instance),
            seen.Order(TypeNameComparer.Instance));
    }

    /// <summary>
    /// CI 里跑的那一批：固定种子的 <see cref="CiCombinations"/> 个组合，每一个收尾之后都要满足不变量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 已开票未修的违规（<see cref="ReconnectModel.KnownDefects"/>）打印次数、不判失败；其余任何一条都判失败。
    /// </para>
    /// <para>
    /// 失败时打印每个违规组合的种子与序列，并把第一个确定性地删减到最短再打印一遍：复现只要种子，看懂只要最短的那一串。
    /// 用 <see cref="ReplaySeeds"/>（<c>CS342_SEEDS</c>）在别的提交上复跑同样的种子。
    /// </para>
    /// <para>
    /// 组合数的取舍：握手经真实处理器之后，本机稳态每个组合 0.20～0.29 秒（control-server#342 量测，四个提交各 300 个），200 个约 40～60 秒，
    /// 在「测试步最多多 2 分钟」以内；它与别的测试类并行跑，占的测试步墙钟比这更少。
    /// 更多的组合用 <see cref="PrototypeMeasurement"/> 手动跑（<c>CS342_ITER</c>）。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task EverySeededSequenceEndsWithTheEntryRequestOnTheVehicle()
    {
        List<(string Seed, ReconnectStep[] Steps, ReconnectViolation Violation, string Detail)> unknown = [];
        Dictionary<string, int> known = new(StringComparer.Ordinal);
        for (int index = 0; index < CiCombinations; index++)
        {
            (string seed, ReconnectStep[] steps) = ReconnectModel.Fixed(MasterSeed, index);
            ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);
            foreach ((ReconnectViolation violation, string detail) in verdict.Violations)
            {
                if (ReconnectModel.KnownDefectFor(violation, detail) is { } defect)
                {
                    known[defect.Ticket] = known.GetValueOrDefault(defect.Ticket) + 1;
                }
                else
                {
                    unknown.Add((seed, steps, violation, detail));
                }
            }
        }

        // 已知未修的照样报出来：修复票合入之前，它们在这里出现几次是有用的信息。
        foreach ((string ticket, int count) in known)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"known defect {ticket}: {count} violation(s) in {CiCombinations} sequences");
        }

        if (unknown.Count == 0)
        {
            return;
        }

        StringBuilder message = new();
        message.AppendLine(CultureInfo.InvariantCulture, $"{unknown.Count} violation(s) in {CiCombinations} seeded sequences that no known defect accounts for:");
        foreach ((string seed, ReconnectStep[] steps, ReconnectViolation violation, string detail) in unknown)
        {
            message.AppendLine(CultureInfo.InvariantCulture, $"  seed {seed} {violation}: {detail}");
            message.AppendLine(CultureInfo.InvariantCulture, $"    {ReconnectModel.Print(steps)}");
        }

        (string firstSeed, ReconnectStep[] firstSteps, ReconnectViolation firstViolation, _) = unknown[0];
        (ReconnectStep[] minimal, ReconnectVerdict minimalVerdict) = await ReconnectModel.MinimizeAsync(firstSteps, firstViolation);
        message.AppendLine(CultureInfo.InvariantCulture, $"shortest form of seed {firstSeed} for {firstViolation}: {ReconnectModel.Print(minimal)}");
        message.AppendLine(minimalVerdict.Detail);
        Assert.Fail(message.ToString());
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
        int waitOnPersonChances = 0;
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
            waitOnPersonChances += verdict.WaitOnPersonChances;
            // 每一类都记，不只记第一条：一个组合里先撞上的那一类会把后面的挡住，按第一条分组会少数别的类。
            foreach (ReconnectViolation violation in verdict.Violations.Select(item => item.Violation).Distinct())
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
        report.AppendLine(CultureInfo.InvariantCulture, $"rounds that failed while carrying a wait-on-person code: {waitOnPersonChances}");
        foreach ((ReconnectViolation violation, List<(string Seed, ReconnectStep[] Steps)> found) in byViolation)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{violation}: {found.Count} of {iterations}");
            foreach ((string seed, ReconnectStep[] steps) in found)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {seed} {ReconnectModel.Print(steps)}");
            }
        }

        foreach ((ReconnectViolation target, List<(string Seed, ReconnectStep[] Steps)> found) in byViolation)
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"CsCheck shrinking {target} from seed {found[0].Seed}, iter={shrinkIterations}");
            Stopwatch shrinkWatch = Stopwatch.StartNew();
            try
            {
                await Check.SampleAsync(
                    ReconnectModel.Sequence,
                    async steps =>
                    {
                        ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);
                        if (verdict.Has(target))
                        {
                            throw new ReconnectInvariantException(verdict);
                        }
                    },
                    seed: found[0].Seed,
                    iter: shrinkIterations,
                    print: ReconnectModel.Print);
                report.AppendLine("shrink: the seed did not fail again (not reproducible)");
            }
            catch (CsCheckException error)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"shrink took {shrinkWatch.Elapsed.TotalSeconds:F1}s");
                report.AppendLine(FirstLines(error.Message, 2));
            }

            ReconnectStep[] shortest = [.. found.Select(item => item.Steps).OrderBy(steps => steps.Length).First()];
            Stopwatch minimizeWatch = Stopwatch.StartNew();
            (ReconnectStep[] minimal, ReconnectVerdict minimalVerdict) = await ReconnectModel.MinimizeAsync(shortest, target);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"deterministic minimisation of the shortest {target} ({shortest.Length} steps) took {minimizeWatch.Elapsed.TotalSeconds:F1}s:");
            report.AppendLine(CultureInfo.InvariantCulture, $"  from {ReconnectModel.Print(shortest)}");
            report.AppendLine(CultureInfo.InvariantCulture, $"  to   {ReconnectModel.Print(minimal)}");
            report.AppendLine(minimalVerdict.Detail);
        }

        await File.WriteAllTextAsync(reportPath, report.ToString(), TestContext.Current.CancellationToken);
    }

    private static string FirstLines(string text, int count) =>
        string.Join(Environment.NewLine, text.ReplaceLineEndings().Split(Environment.NewLine).Take(count));

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

    /// <summary>
    /// 复跑打印出来的序列（只在显式要求时跑）：<c>CS342_SEQUENCES</c> 里用竖线分开的每一串，原样是失败输出里 <c>[...]</c> 的样子。
    /// 判定与逐步记录写到 <c>CS342_REPORT</c>。
    /// </summary>
    [Fact(Explicit = true)]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ReplaySequences()
    {
        string[] printed = (Environment.GetEnvironmentVariable("CS342_SEQUENCES")
                ?? throw new InvalidOperationException("CS342_SEQUENCES is not set."))
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(printed);
        string reportPath = Environment.GetEnvironmentVariable("CS342_REPORT") is { Length: > 0 } path
            ? path
            : Path.Combine(Path.GetTempPath(), "cs342-replay-sequences.txt");
        StringBuilder report = new();
        foreach (string sequence in printed)
        {
            ReconnectStep[] steps = ReconnectModel.Parse(sequence);
            ReconnectVerdict verdict = await ReconnectModel.RunAsync(steps);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"== {ReconnectModel.Print(steps)} violation={verdict.Violation?.ToString() ?? "none"}");
            report.AppendLine(verdict.Detail);
        }

        await File.WriteAllTextAsync(reportPath, report.ToString(), TestContext.Current.CancellationToken);
    }
}

internal sealed class TypeNameComparer : IComparer<Type>
{
    public static readonly TypeNameComparer Instance = new();

    public int Compare(Type? x, Type? y) => string.CompareOrdinal(x?.Name, y?.Name);
}

/// <summary>不变量不成立时抛出，消息里带着判定与逐步记录，CsCheck 化简时原样打印。</summary>
internal sealed class ReconnectInvariantException(ReconnectVerdict verdict)
    : Exception($"{verdict.Violation}\n{verdict.Detail}")
{
    public ReconnectVerdict Verdict { get; } = verdict;
}
