using System.Text.RegularExpressions;

namespace ControlServer.Tests;

/// <summary>
/// 「是不是同一件活」只按需求身份（业务键 + 程）判，不在别处直接拿业务键比（control-server#911）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要一道护栏。</b>本票把受理唯一、抑制、完成三处的身份从业务键改成「业务键 + 程」，改的是十几处按键比较。漏改一处不会编译不过，
/// 而是到现场才出：同一子批的焊线2 那程在那一处被当成焊线那程——被挡、或在存储层抛身份冲突、或卸货结果被拒。
/// 三张身份表上的比较都收口在 <c>TransportDemandIdentityRows.cs</c>（<c>WhereIdentity</c>、<c>WhereDemandIdOrIdentity</c>、
/// <c>WhereKeyAmong</c>）与 <c>TransportDemandIdentity</c>；这里守的是下一处新写的按键比较。
/// </para>
/// <para>
/// <b>判法。</b>产品源码（<c>src/</c>、<c>tools/</c>，迁移除外）去掉注释后，凡是业务键出现在相等比较（<c>==</c>、<c>!=</c>、
/// <c>Equals(</c>）、<c>Contains(…TransportDemandKey)</c>、或以它为键的 <c>ToLookup</c>／<c>ToDictionary</c>／<c>GroupBy</c> 里，
/// 都要在 <see cref="Allowed"/> 里点名、写明为什么那里按键比是对的；次数也要对得上，同一个文件多出一处也红。
/// </para>
/// <para>
/// <b>它按构造看不见什么</b>（防回归，不是证明）：把业务键先放进别的变量再比、经 SQL 字符串比、或用别的写法（模式匹配、
/// <c>CompareOrdinal</c>）。这些今天在产品源码里都没有。
/// </para>
/// <para>
/// <b>没有 <c>IntegrationSlice</c> 标记</b>，理由同 <c>JourneyClosureSingleExitArchitectureTests</c>：横切的护栏挂在某个切片上，
/// 那个切片被推迟时护栏也跟着熄灭。
/// </para>
/// </remarks>
public sealed class TransportDemandIdentityArchitectureTests
{
    /// <summary>允许按业务键比较的位置：文件 → 次数与理由。</summary>
    private static readonly Dictionary<string, (int Count, string Why)> Allowed = new(StringComparer.Ordinal)
    {
        ["src/ControlServer.Infrastructure/Persistence/TransportDemandIdentityRows.cs"] =
            (1, "the one place the identity is turned into a query (WhereDemandIdOrIdentity compares key and leg together)"),
        ["src/ControlServer.Application/WireToGateOrchestration.cs"] =
            (1, "HasSameDecisionFacts compares every field of two snapshots of the same DemandId, the leg's STEP among them"),
        ["src/ControlServer.Host/Runtime/Dispatch/DispatchRoundRunner.cs"] =
            (1, "a backlog row is per DemandId; a changed key is a changed decision fact of that one row"),
        ["src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs"] =
            (1, "the cargo binding and the resumption are checked to name the same DemandId, key and vehicle"),
        ["src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs"] =
            (1, "a completion must name the business key its own DemandId was accepted under"),
    };

    private static readonly Regex[] Comparisons =
    [
        new(@"\bTransportDemandKey\s*(==|!=)", RegexOptions.CultureInvariant),
        new(@"(==|!=)\s*[\w.]*\bTransportDemandKey\b", RegexOptions.CultureInvariant),
        new(@"\bEquals\([^;]*\bTransportDemandKey\b", RegexOptions.CultureInvariant),
        new(@"\bContains\(\s*[\w.]*\bTransportDemandKey\s*\)", RegexOptions.CultureInvariant),
        new(@"\b(ToLookup|ToDictionary|ToDictionaryAsync|GroupBy)\(\s*\w+\s*=>\s*\w+\.TransportDemandKey\b", RegexOptions.CultureInvariant),
    ];

    private static readonly string[] SourceDirectories = ["src", "tools"];

    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    [Fact]
    public void ProductSourceComparesBusinessKeysOnlyWhereTheLedgerSaysWhy()
    {
        Dictionary<string, int> found = new(StringComparer.Ordinal);
        foreach (string file in ProductSourceFiles())
        {
            string source = BlockComment.Replace(LineComment.Replace(File.ReadAllText(file), string.Empty), string.Empty);
            int count = source.Split('\n').Count(line => Comparisons.Any(pattern => pattern.IsMatch(line)));
            if (count > 0)
            {
                found[Relative(file)] = count;
            }
        }

        Assert.Equal(
            Allowed.Select(entry => $"{entry.Key}={entry.Value.Count}").Order(StringComparer.Ordinal),
            found.Select(entry => $"{entry.Key}={entry.Value}").Order(StringComparer.Ordinal));
    }

    /// <summary>判法自己会红：一行典型的按键比较被认出来，注释里的不算。</summary>
    [Theory]
    [InlineData("rows.Where(row => row.TransportDemandKey == key)", true)]
    [InlineData("if (key != demand.TransportDemandKey)", true)]
    [InlineData("string.Equals(a.TransportDemandKey, b, StringComparison.Ordinal)", true)]
    [InlineData("rows.Where(row => keys.Contains(row.TransportDemandKey))", true)]
    [InlineData(".ToLookup(row => row.TransportDemandKey, row => row.DemandId)", true)]
    [InlineData(".ToDictionaryAsync(row => row.DemandId, row => row.TransportDemandKey)", false)]
    [InlineData("TransportDemandKey = snapshot.TransportDemandKey,", false)]
    public void TheJudgementRecognisesAComparison(string line, bool comparison) =>
        Assert.Equal(comparison, Comparisons.Any(pattern => pattern.IsMatch(line)));

    private static IEnumerable<string> ProductSourceFiles()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        return SourceDirectories
            .Select(directory => Path.Combine(root, directory))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(file =>
            {
                string relative = Relative(file);
                return !relative.Contains("/Migrations/", StringComparison.Ordinal) &&
                       !relative.Contains("/bin/", StringComparison.Ordinal) &&
                       !relative.Contains("/obj/", StringComparison.Ordinal);
            });
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(ProtocolIdentityArchitectureTests.RepositoryRoot(), file).Replace('\\', '/');
}
