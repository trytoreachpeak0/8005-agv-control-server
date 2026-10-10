using System.Text.RegularExpressions;

namespace ControlServer.Tests;

/// <summary>
/// 旅程行的版本只在 <c>SaveChanges</c> 的钩子里递增（control-server#357）。绕开钩子写 <c>JourneyRuntimes</c> 的写法——
/// <c>ExecuteUpdate</c>、<c>ExecuteDelete</c>、原始 SQL——既不递增它，也不核它：用它写下的东西会被别人的旧行悄悄盖掉，
/// 用它盖掉别人的东西也不会被拒。所以全仓只许有点名放行的那几处，而且每一处都要说清为什么它盖不掉别人。
/// </summary>
/// <remarks>
/// <para>
/// 依据是第一步的普查（票面评论）：CI 全量里对这张表的原始写只有迁移脚本和等人监看那一处。迁移目录不扫——迁移在任何运行时写入者
/// 出现之前跑完。
/// </para>
/// <para>
/// <b>检查器先校准再用</b>：一段必须抓到的正例、一段只在条件里提到旅程表的反例（<c>OwnOrderRebuilds</c> 就是这个形状）。
/// 检查器坏成「什么都抓不到」时，正例那一条红；坏成「什么都抓」时，反例那一条红。
/// </para>
/// </remarks>
public sealed class JourneyRowWriteArchitectureTests
{
    /// <summary>
    /// 等人监看（control-server#273）写它自己的三列，条件里带「等人起点仍是读到的那一个」：它写不到别人的列，旅程换了等待它就一行不写。
    /// 它不递增版本，是有意的：递增会让引擎这一轮稍后的保存与自己冲突。
    /// </summary>
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["src/ControlServer.Host/Runtime/WaitingJourneyWatch.cs"] =
            ["WaitingBatteryObservedAt", "WaitingBatteryPercent", "WaitingWarnedAt"],
    };

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public void TheCheckerFindsABulkWriteToTheJourneyTableAndIgnoresOneThatOnlyMentionsItInItsCondition()
    {
        Assert.Single(BulkJourneyWrites(
            """
            int written = await dbContext.JourneyRuntimes
                .Where(row => row.JourneyId == id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Stage, stage), cancellationToken);
            """));
        Assert.Single(BulkJourneyWrites("await db.Set<JourneyRuntimeRow>().Where(x => x.AgvId == a).ExecuteDeleteAsync(token);"));
        Assert.Single(RawJourneyWrites("command.CommandText = \"UPDATE \\\"JourneyRuntimes\\\" SET Stage = 'Blocked'\";"));
        Assert.Empty(BulkJourneyWrites(
            """
            int claimed = await dbContext.OwnOrderRebuilds
                .Where(row => dbContext.JourneyRuntimes.Any(journey => journey.JourneyId == row.JourneyId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CargoEvidenceRequestedAt, now), cancellationToken);
            """));
        Assert.Empty(BulkJourneyWrites("// await dbContext.JourneyRuntimes.ExecuteDeleteAsync(token);"));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public void NothingOutsideTheNamedPlacesWritesTheJourneyTableAroundTheSaveHook()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        Dictionary<string, List<string>> found = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Migrations/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }
            string text = File.ReadAllText(file);
            List<string> statements = [.. BulkJourneyWrites(text), .. RawJourneyWrites(text)];
            if (statements.Count > 0)
            {
                found[relative] = statements;
            }
        }

        Assert.Equal(Allowed.Keys.Order(StringComparer.Ordinal), found.Keys.Order(StringComparer.Ordinal));
        foreach ((string file, string[] columns) in Allowed)
        {
            string statement = Assert.Single(found[file]);
            Assert.Equal(
                columns,
                Regex.Matches(statement, @"SetProperty\(\s*\w+\s*=>\s*\w+\.(\w+)")
                    .Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        }
    }

    /// <summary>调用了批量写的语句里，第一个被访问的表是旅程表的那些。</summary>
    private static IEnumerable<string> BulkJourneyWrites(string source)
    {
        foreach (string statement in Statements(source))
        {
            if (!Regex.IsMatch(statement, @"\.\s*Execute(Update|Delete|Sql\w*)\s*(Async)?\s*\("))
            {
                continue;
            }
            Match first = Regex.Match(statement, @"\b\w+\s*\.\s*(Set<(?<entity>\w+)>\s*\(\s*\)|(?<set>[A-Z]\w+))\s*(\.|$)");
            if (first.Success &&
                (first.Groups["set"].Value == "JourneyRuntimes" || first.Groups["entity"].Value == "JourneyRuntimeRow"))
            {
                yield return statement;
            }
        }
    }

    private static IEnumerable<string> RawJourneyWrites(string source) =>
        Statements(source).Where(statement => Regex.IsMatch(
            statement,
            @"(UPDATE|INSERT\s+INTO|DELETE\s+FROM|REPLACE\s+INTO)\s+(\\?""|\[)?JourneyRuntimes\b",
            RegexOptions.IgnoreCase));

    private static IEnumerable<string> Statements(string source)
    {
        string code = string.Join('\n', source.Split('\n').Select(line =>
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            return comment >= 0 && !line[..comment].Contains('"') ? line[..comment] : line;
        }));
        return code.Split(';').Select(statement => statement.Trim()).Where(statement => statement.Length > 0);
    }
}
