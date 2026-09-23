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
            (string seed, ReconnectScenario scenario) = ReconnectModel.Fixed(MasterSeed, index);
            ReconnectScenario again = ReconnectModel.Sequence.Generate(PCG.Parse(seed), null, out _);
            Assert.Equal(ReconnectModel.Print(scenario), ReconnectModel.Print(again));
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
        HashSet<bool> onboards = [];
        for (int index = 0; index < 50; index++)
        {
            (_, ReconnectScenario scenario) = ReconnectModel.Fixed(MasterSeed, index);
            Assert.Equal(scenario, ReconnectModel.Parse(ReconnectModel.Print(scenario)));
            seen.UnionWith(scenario.Steps.Select(step => step.GetType()));
            onboards.Add(scenario.RealOnboard);
        }

        // 五十串里每一种动作、两种车载端都出现过，否则没出现的那一种读回来对不对没被核对过。
        Assert.Equal(
            typeof(ReconnectStep).GetNestedTypes(System.Reflection.BindingFlags.NonPublic).Where(type => type.IsSubclassOf(typeof(ReconnectStep))).Order(TypeNameComparer.Instance),
            seen.Order(TypeNameComparer.Instance));
        Assert.Equal([false, true], onboards.Order());
    }

    /// <summary>
    /// CI 里跑的那一批：固定种子的 <see cref="CiCombinations"/> 个组合，每一个收尾之后都要满足不变量。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 已开票未修的违规（<see cref="ReconnectModel.KnownDefects"/>）打印次数、不判失败；其余任何一条都判失败。<b>已知缺陷表里任何一行在这一批里
    /// 一次都没命中，也判失败</b>：那一行对应的缺陷多半已经修了，留着它会悄悄吞掉将来同一形状的新问题（调度与审查必修 M3）。判法在
    /// <see cref="JudgeAgainstKnownDefects"/>，它自己的用例是 <see cref="AKnownDefectRowThatNothingHitsFailsTheRun"/>。
    /// </para>
    /// <para>
    /// 失败时先打印全部违规的种子与原序列，再把第一个确定性地删减到最短；删减本身出错（第一条复现不了）不会吞掉前面那份清单。
    /// 用 <see cref="ReplaySeeds"/>（<c>CS342_SEEDS</c>）在别的提交上复跑同样的种子。
    /// </para>
    /// <para>
    /// 组合数：CI 上这一条自己跑了 2 分 31.7 秒，Test 步整体没有变长（9 分 34 秒，相邻三轮 9 分 29 秒～9 分 49 秒），它不在最长的路径上；
    /// 调度定保持 200——每轮多查的组合正是这张票的价值。Test 步若因它比相邻几轮长出一分钟以上，再降到 120 左右。
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
        List<(string Seed, ReconnectScenario Scenario, ReconnectVerdict Verdict)> runs = [];
        for (int index = 0; index < CiCombinations; index++)
        {
            (string seed, ReconnectScenario scenario) = ReconnectModel.Fixed(MasterSeed, index);
            runs.Add((seed, scenario, await ReconnectModel.RunAsync(scenario)));
        }

        (string? failure, Dictionary<string, int> hits, (string Seed, ReconnectScenario Scenario, ReconnectViolation Violation)? firstUnknown) =
            JudgeAgainstKnownDefects(runs, ReconnectModel.KnownDefects);

        // 已知未修的照样报出来：修复票合入之前，它们在这里出现几次是有用的信息。
        foreach ((string ticket, int count) in hits)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"known defect {ticket}: {count} violation(s) in {CiCombinations} sequences");
        }

        if (failure is null)
        {
            return;
        }

        StringBuilder message = new(failure);
        if (firstUnknown is { } first)
        {
            try
            {
                (ReconnectScenario minimal, ReconnectVerdict minimalVerdict) =
                    await ReconnectModel.MinimizeAsync(first.Scenario, first.Violation);
                message.AppendLine(CultureInfo.InvariantCulture, $"shortest form of seed {first.Seed} for {first.Violation}: {ReconnectModel.Print(minimal)}");
                message.AppendLine(minimalVerdict.Detail);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                message.AppendLine(CultureInfo.InvariantCulture, $"minimising seed {first.Seed} failed: {error.GetType().Name}: {error.Message}");
            }
        }

        Assert.Fail(message.ToString());
    }

    /// <summary>
    /// 已知缺陷表的判法本身：表里有一行在这一批里一次都没命中，判失败，并说出是哪一行、该怎么处理；认不出的违规照样判失败；
    /// 认得出的只计数。用造出来的判定测，不跑模型。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    public void AKnownDefectRowThatNothingHitsFailsTheRun()
    {
        KnownDefect real = new("onboard-hmi#206", ReconnectViolation.LegitimateMessageRefused, ["has conflicting content"]);
        KnownDefect neverHit = new("control-server#0", ReconnectViolation.OneInboundManyAnswers, ["a fragment no run ever prints"]);
        ReconnectScenario scenario = new(false, [new ReconnectStep.Arrive()]);
        (string, ReconnectScenario, ReconnectVerdict)[] runs =
        [
            ("seed-a", scenario, Verdict((ReconnectViolation.LegitimateMessageRefused, "safety revision 8 has conflicting content."))),
            ("seed-b", scenario, Verdict()),
        ];

        (string? clean, Dictionary<string, int> hits, _) = JudgeAgainstKnownDefects(runs, [real]);
        (string? stale, _, _) = JudgeAgainstKnownDefects(runs, [real, neverHit]);
        (string? unknown, _, var firstUnknown) = JudgeAgainstKnownDefects(
            [.. runs, ("seed-c", scenario, Verdict((ReconnectViolation.StuckAtPickup, "stage=AwaitingPickupArrival")))],
            [real]);

        Assert.Null(clean);
        Assert.Equal(1, hits["onboard-hmi#206"]);
        Assert.NotNull(stale);
        Assert.Contains("control-server#0", stale, StringComparison.Ordinal);
        Assert.Contains("matched nothing", stale, StringComparison.Ordinal);
        Assert.DoesNotContain("onboard-hmi#206", stale, StringComparison.Ordinal);
        Assert.NotNull(unknown);
        Assert.Contains("seed-c", unknown, StringComparison.Ordinal);
        Assert.Equal("seed-c", firstUnknown?.Seed);
    }

    /// <summary>
    /// 一批判定对已知缺陷表：认得出的计入各自的票，认不出的与一次没命中的行都写进失败说明（null 表示通过）。认不出的第一条另外交回，
    /// 调用方拿它去删减。
    /// </summary>
    internal static (string? Failure, Dictionary<string, int> Hits, (string Seed, ReconnectScenario Scenario, ReconnectViolation Violation)? FirstUnknown)
        JudgeAgainstKnownDefects(
            IReadOnlyList<(string Seed, ReconnectScenario Scenario, ReconnectVerdict Verdict)> runs,
            IReadOnlyList<KnownDefect> table)
    {
        Dictionary<string, int> hits = new(StringComparer.Ordinal);
        List<(string Seed, ReconnectScenario Scenario, ReconnectViolation Violation, string Detail)> unknown = [];
        foreach ((string seed, ReconnectScenario scenario, ReconnectVerdict verdict) in runs)
        {
            foreach ((ReconnectViolation violation, string detail) in verdict.Violations)
            {
                if (ReconnectModel.KnownDefectFor(table, violation, detail) is { } defect)
                {
                    hits[defect.Ticket] = hits.GetValueOrDefault(defect.Ticket) + 1;
                }
                else
                {
                    unknown.Add((seed, scenario, violation, detail));
                }
            }
        }

        KnownDefect[] stale = [.. table.Where(defect => !hits.ContainsKey(defect.Ticket))];
        if (unknown.Count == 0 && stale.Length == 0)
        {
            return (null, hits, null);
        }

        StringBuilder message = new();
        foreach (KnownDefect defect in stale)
        {
            message.AppendLine(
                CultureInfo.InvariantCulture,
                $"known defect row {defect.Ticket} ({defect.Violation}: {string.Join(" / ", defect.DetailFragments)}) matched nothing in {runs.Count} sequences: " +
                $"the defect may be fixed -- remove this row together with the Skip that names the same ticket.");
        }

        if (unknown.Count > 0)
        {
            message.AppendLine(CultureInfo.InvariantCulture, $"{unknown.Count} violation(s) in {runs.Count} seeded sequences that no known defect accounts for:");
            foreach ((string seed, ReconnectScenario scenario, ReconnectViolation violation, string detail) in unknown)
            {
                message.AppendLine(CultureInfo.InvariantCulture, $"  seed {seed} {violation}: {detail}");
                message.AppendLine(CultureInfo.InvariantCulture, $"    {ReconnectModel.Print(scenario)}");
            }
        }

        return (message.ToString(), hits, unknown.Count == 0 ? null : (unknown[0].Seed, unknown[0].Scenario, unknown[0].Violation));
    }

    private static ReconnectVerdict Verdict(params (ReconnectViolation Violation, string Detail)[] violations) =>
        new(violations, string.Empty, 0, 0, TimeSpan.Zero);

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
        Dictionary<ReconnectViolation, List<(string Seed, ReconnectScenario Scenario)>> byViolation = [];
        int ackConflicts = 0;
        int regressions = 0;
        int entryReached = 0;
        int waitOnPersonChances = 0;
        Stopwatch total = Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
        {
            (string seed, ReconnectScenario steps) = ReconnectModel.Fixed(MasterSeed, index);
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
                if (!byViolation.TryGetValue(violation, out List<(string Seed, ReconnectScenario Scenario)>? found))
                {
                    byViolation[violation] = found = [];
                    report.AppendLine(CultureInfo.InvariantCulture, $"first {violation}: index={index} seed={seed} steps={steps.Steps.Length}");
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
        foreach ((ReconnectViolation violation, List<(string Seed, ReconnectScenario Scenario)> found) in byViolation)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{violation}: {found.Count} of {iterations}");
            foreach ((string seed, ReconnectScenario steps) in found)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {seed} {ReconnectModel.Print(steps)}");
            }
        }

        foreach ((ReconnectViolation target, List<(string Seed, ReconnectScenario Scenario)> found) in byViolation)
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

            ReconnectScenario shortest = found.Select(item => item.Scenario).OrderBy(scenario => scenario.Steps.Length).First();
            Stopwatch minimizeWatch = Stopwatch.StartNew();
            (ReconnectScenario minimal, ReconnectVerdict minimalVerdict) = await ReconnectModel.MinimizeAsync(shortest, target);
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"deterministic minimisation of the shortest {target} ({shortest.Steps.Length} steps) took {minimizeWatch.Elapsed.TotalSeconds:F1}s:");
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
            ReconnectScenario steps = ReconnectModel.Sequence.Generate(PCG.Parse(seed), null, out _);
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
            ReconnectScenario steps = ReconnectModel.Parse(sequence);
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
