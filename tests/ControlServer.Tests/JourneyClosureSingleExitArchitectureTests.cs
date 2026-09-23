using System.Text.RegularExpressions;

namespace ControlServer.Tests;

/// <summary>
/// 旅程变成 <c>Completed</c> 只有一个出口，而那个出口顺带暂存车要收的收尾快照（control-server#323）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要一道护栏。</b>修之前全仓有两处把旅程写成 Completed：收尾尾巴（<c>PickupStopTermination.StageJourneyClosureAsync</c>，
/// 来路 1～5 共用）与正常卸完（引擎里的 <c>SetStage</c>）。两处各自负责「告诉车」就是两处会漏，而漏的表现是车上永远留着一站
/// 已经结束的清单——没有任何一条既有用例会因此变红，因为它们断的是库，不是车看见了什么。现在两处都经过
/// <c>JourneyClosure.StageAsync</c>，它写阶段、也暂存快照，所以「写了 Completed 却没告诉车」在构造上不可能。
/// 这道护栏守的是那个构造：下一条来路自己写 <c>Stage = Completed</c> 时，这里变红，而不是等车上看出来。
/// </para>
/// <para>
/// <b>判法。</b>产品源码（<c>src/</c>、<c>tools/</c>，迁移除外）里每一处 <c>JourneyRuntimeStage.Completed</c>，去掉注释之后，
/// 只要不是「比较或模式匹配」——前面是 <c>==</c>、<c>!=</c>、<c>is</c>、<c>not</c>、<c>or</c>、<c>and</c>、<c>case</c>（中间可以隔着括号），
/// 或后面紧跟 switch 分支的 <c>=&gt;</c>——就算一次写入，而写入只许出现在 <see cref="SingleExit"/> 那一个文件里。
/// 赋值、<c>SetStage(…, Completed, …)</c>、三元式、放进变量再赋值，都会在这里被认成写入。
/// </para>
/// <para>
/// <b>它按构造看不见什么</b>（不是证明，是防回归）：不经过这个枚举成员名的写法——<c>(JourneyRuntimeStage)6</c>、
/// <c>Enum.Parse</c>、把 Completed 当默认值的构造——以及绕过 EF 的 SQL。后者单独查一条最地道的形状
/// （<c>Stage = 'Completed'</c>）；前几种没有地道写法，枚举不完，也不去枚举。
/// </para>
/// <para>
/// <b>没有 <c>IntegrationSlice</c> 标记</b>，理由同 <c>OnboardOutboundFunnelArchitectureTests</c>：横切的护栏挂在某个切片上，
/// 那个切片被推迟时护栏也跟着熄灭。
/// </para>
/// </remarks>
public sealed class JourneyClosureSingleExitArchitectureTests
{
    /// <summary>唯一允许把旅程写成 Completed 的文件。</summary>
    private const string SingleExit = "src/ControlServer.Host/Runtime/JourneyClosure.cs";

    private static readonly Regex Occurrence = new(@"JourneyRuntimeStage\.Completed\b", RegexOptions.CultureInvariant);

    private static readonly Regex ReadBefore = new(
        @"(==|!=|\bis|\bnot|\bor|\band|\bcase)[\s(]*$", RegexOptions.CultureInvariant);

    private static readonly Regex ReadAfter = new(@"^\s*=>", RegexOptions.CultureInvariant);

    [Fact]
    public void OnlyTheSingleExitWritesAJourneyCompleted()
    {
        string[] writers = [.. ProductSourceFiles()
            .SelectMany(file => Writes(File.ReadAllText(file)).Select(line => $"{Relative(file)}:{line}"))
            .Order(StringComparer.Ordinal)];

        Assert.All(writers, writer => Assert.StartsWith(SingleExit + ":", writer, StringComparison.Ordinal));
        Assert.NotEmpty(writers);
    }

    [Fact]
    public void NoProductSqlWritesTheCompletedStage()
    {
        Regex sql = new(@"Stage\s*=\s*'Completed'", RegexOptions.CultureInvariant);
        string[] writers = [.. ProductSourceFiles()
            .Where(file => sql.IsMatch(File.ReadAllText(file)))
            .Select(Relative)];

        Assert.Empty(writers);
    }

    /// <summary>
    /// 能让旅程收尾的每个文件，提交之后也要把收尾快照发出去：出现 <c>new PickupStopTermination(</c> 或
    /// <c>JourneyClosure.StageAsync(</c> 的产品文件（这两个类型自己的文件除外），同一文件里必须有 <c>JourneyClosure.SendAsync(</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上面那条守「只有一个出口写 Completed」，所以收尾快照一定落库；它守不住「落了库却没发」。control-server#299 带来的
    /// 故障人工清除（<c>VehicleFaultRecoveryService</c>）就是这样：经出口收尾、快照在发件箱里，但提交之后没发，连着的车要等到下一次
    /// 重连才收到（#323 来路 7，<c>VehicleFaultRecoveryTests.AClearedFaultThatReleasesTheLastDemandTellsTheVehicleTheJourneyIsOver</c>）。
    /// </para>
    /// <para>
    /// <b>它按构造看不见什么</b>：发送在同一文件里但不在那次提交之后、不在那条分支上；经别的文件间接调用收尾尾巴。
    /// 这是按文件的防回归信号，不是逐条路径的证明——逐条路径由各来路的用例断「车收到了」。
    /// 将来某个文件只用 <c>PickupStopTermination.StageDemandTerminationAsync</c>、从不收尾，这里会误报；那时把它连同理由列进
    /// <see cref="ConstructsTerminationWithoutClosing"/>，不要删这条。
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryFileThatCanCloseAJourneySendsTheClosure()
    {
        // The target-typed form ("PickupStopTermination x = new(...)") counts too: control-server#345 wrote the first one, and
        // the scan did not see it.
        Regex closes = new(
            @"new\s+PickupStopTermination\s*\(|PickupStopTermination\s+\w+\s*=\s*new\s*\(|JourneyClosure\.StageAsync\s*\(",
            RegexOptions.CultureInvariant);
        Regex sends = new(@"JourneyClosure\.SendAsync\s*\(", RegexOptions.CultureInvariant);
        string[] closers = [.. ProductSourceFiles()
            .Where(file => !Relative(file).EndsWith("/PickupStopTermination.cs", StringComparison.Ordinal)
                && Relative(file) != SingleExit
                && !ConstructsTerminationWithoutClosing.Contains(Relative(file)))
            .Where(file => closes.IsMatch(CodeOnly(File.ReadAllText(file))))];

        string[] silent = [.. closers.Where(file => !sends.IsMatch(CodeOnly(File.ReadAllText(file)))).Select(Relative)];

        Assert.Empty(silent);
        // 判据要有东西可判：点名今天会收尾的四个文件，扫描必须认出每一个——比只数个数更硬，数够了也可能是认错了文件。
        // 故障人工清除（VehicleFaultRecoveryService）原是第四个；control-server#318 起清除之后不收尾、留在本车重建，
        // 那个文件里已没有收尾也没有发送，它离开这张表是行为变了，不是扫描漏了。哪天它又收尾，上面的 silent 会先替它说话。
        Assert.Superset(KnownClosers, new HashSet<string>(closers.Select(Relative), StringComparer.Ordinal));
    }

    /// <summary>今天会让旅程收尾的产品文件（出口与收尾尾巴自己的文件除外）。</summary>
    private static readonly HashSet<string> KnownClosers = new(StringComparer.Ordinal)
    {
        "src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs",
        "src/ControlServer.Host/Runtime/Release/DemandReleaseService.cs",
        "src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs",
        // A person giving a stopped trip up (control-server#345).
        "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
    };

    /// <summary>构造了收尾尾巴、却从不让旅程收尾的文件。今天没有。</summary>
    private static readonly HashSet<string> ConstructsTerminationWithoutClosing = new(StringComparer.Ordinal);

    private static string CodeOnly(string source) =>
        string.Join('\n', source.ReplaceLineEndings("\n").Split('\n').Select(StripComment));

    /// <summary>
    /// 判法自己的正反例：修之前那两处写法必须被认成写入，全仓今天那些读法必须不被认成写入。
    /// </summary>
    /// <remarks>
    /// 护栏的判法是一段正则，而正则写宽了会把一切放过（那样上面那条永远绿），写窄了会把读当成写（那样它永远红、然后被人删掉）。
    /// 所以两个方向各钉几条真实出现过的形状。
    /// </remarks>
    [Theory]
    [InlineData("runtime.Stage = JourneyRuntimeStage.Completed;", true)]
    [InlineData("SetStage(runtime, JourneyRuntimeStage.Completed, now);", true)]
    [InlineData("runtime.Stage = done ? JourneyRuntimeStage.Completed : JourneyRuntimeStage.Blocked;", true)]
    [InlineData("JourneyRuntimeStage next = JourneyRuntimeStage.Completed;", true)]
    [InlineData(".Where(row => row.Stage != JourneyRuntimeStage.Completed)", false)]
    [InlineData("if (stop.Stage == JourneyRuntimeStage.Completed)", false)]
    [InlineData("if (runtime.Stage is not (JourneyRuntimeStage.Blocked or JourneyRuntimeStage.Completed))", false)]
    [InlineData("if (runtime.Stage is JourneyRuntimeStage.Completed or JourneyRuntimeStage.Blocked)", false)]
    [InlineData("            case JourneyRuntimeStage.Completed:", false)]
    [InlineData("        JourneyRuntimeStage.Completed => JourneyStageActivity.Finished,", false)]
    [InlineData("// runtime.Stage = JourneyRuntimeStage.Completed;", false)]
    [InlineData("/// <see cref=\"JourneyRuntimeStage.Completed\"/>", false)]
    public void TheJudgementTellsAWriteFromARead(string source, bool isWrite) =>
        Assert.Equal(isWrite, Writes(source).Length > 0);

    /// <summary>去掉注释之后，每一处不是读法的出现所在的行号。</summary>
    private static int[] Writes(string source)
    {
        string[] lines = source.ReplaceLineEndings("\n").Split('\n');
        List<int> writes = [];
        for (int index = 0; index < lines.Length; index++)
        {
            string code = StripComment(lines[index]);
            foreach (Match match in Occurrence.Matches(code))
            {
                string before = code[..match.Index];
                string after = code[(match.Index + match.Length)..];
                if (!ReadBefore.IsMatch(before) && !ReadAfter.IsMatch(after))
                {
                    writes.Add(index + 1);
                }
            }
        }

        return [.. writes];
    }

    /// <summary>行内 <c>//</c> 之后的都是注释；字符串里的 <c>//</c>（URL）只会让这一行少扫一截，不会多出写入。</summary>
    private static string StripComment(string line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static IEnumerable<string> ProductSourceFiles()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string[] roots = ["src", "tools"];
        char separator = Path.DirectorySeparatorChar;
        return roots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(ProtocolIdentityArchitectureTests.RepositoryRoot(), file).Replace(Path.DirectorySeparatorChar, '/');
}
