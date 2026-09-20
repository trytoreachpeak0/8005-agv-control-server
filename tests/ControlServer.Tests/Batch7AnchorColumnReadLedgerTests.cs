using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ControlServer.Tests;

/// <summary>
/// 推进段里仍然从<b>旅程行锚列</b>读的每一处，连同它为什么还在那里（批次7-03，control-server#208，独立审查 S5）。
/// </summary>
/// <remarks>
/// <para>
/// 本票把发布侧全部改成从停靠行与从属需求行取 id，但<b>结算、查询与几处站点参数仍读旅程行上的同名列</b>。
/// 今天两边恒等——停靠行与归属行的这些列由受理时从旅程行原样搬入（<c>SingleDemandJourneyShape</c>），而本票除了
/// 离站核验过期重发那一处之外不写它们——所以零行为变化。
/// </para>
/// <para>
/// <b>但这是「同名不同源」，而它分岔时的样子特别难查</b>：命令按 A 发出去、结果按 B 去查，旅程于是永远停在等结果，
/// <b>而且不报错</b>。批次7-06（control-server#211）一旦让两边分开（例如第 6 条让清单升版换一个新的
/// <c>SublotRequestMessageId</c>），这些读者就会指着旧的那一个。
/// </para>
/// <para>
/// <b>为什么本票不就地改。</b>一是数量：实际有十几处，逐处改要重新论证每一处该读停靠还是该读归属，而那是多需求
/// 语义的设计工作，归 7-06。二是判别力：今天两边恒等，就地改前后所有测试都绿，那会是一次<b>只能凭说明相信</b>的
/// 改动——而本票刚在别处吃过「说明不是判据」的亏。
/// </para>
/// <para>
/// <b>所以留下这份台账，而不是一段注释。</b>注释会过期，台账不会：源码里多一处、少一处、改了名字，这条测试就红，
/// 逼下一个人要么改用停靠行、要么把理由写进来。7-06 把它们搬完之后，次数归零，这条测试同样会红——那时它该被删掉，
/// 而不是被改数字。
/// </para>
/// </remarks>
public sealed class Batch7AnchorColumnReadLedgerTests
{
    /// <summary>属性名 → （还剩几处读它，为什么还在读）。</summary>
    private static readonly SortedDictionary<string, (int Count, string Why)> StillReadFromTheJourneyRow =
        new(StringComparer.Ordinal)
        {
            ["GateStationId"] = (3, "离站核验命令的 targetStationId、卸货准入判断、重发时的同一个命令；" +
                                    "卸货停靠行上的 StationId 与它恒等，搬它要连着准入那条链一起看"),
            ["LoadCommandMessageId"] = (2, "装货命令的结算（装货落定、确定性失败各一处）；发布用的是归属行的同名列"),
            ["LoadSlotOperationAttemptId"] = (3, "等装货结果时按它查 StationOperations、判恢复原因、读结果原文；" +
                                                 "命令用的是归属行的同名列——这一对分岔就是「发了 A 查 B」"),
            ["OperationSessionId"] = (1, "拒收报文的载荷；发布清单与录入请求用的是停靠行的同名列"),
            ["PickupStationId"] = (1, "站点期限到时的告警日志；清单与录入请求用的是停靠行的 StationId"),
            ["SublotRequestMessageId"] = (1, "录入请求的结算；发布用的是停靠行的同名列。" +
                                             "批次7-06 的第 6 条让清单升版换新 id 时，这一处会结算不到当前那条"),
            ["UnloadCommandMessageId"] = (1, "卸货命令的结算；发布用的是归属行的同名列"),
            ["UnloadSlotOperationAttemptId"] = (3, "等卸货结果、判恢复原因、判卸货是否已备好；命令用的是归属行的同名列"),
        };

    /// <summary>
    /// 源码里读旅程行锚列的次数，与上面的台账逐项对得上。
    /// </summary>
    /// <remarks>
    /// 只数代码行：整行注释排除在外，因为解释这件事本身也要写 <c>runtime.LoadSlotOperationAttemptId</c>。
    /// </remarks>
    [Fact]
    public void EveryAnchorColumnStillReadInTheEngineIsAccountedFor()
    {
        string[] codeLines = [.. File.ReadAllLines(EnginePath())
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))];
        SortedDictionary<string, int> actual = new(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
                     string.Join('\n', codeLines),
                     @"\bruntime\.(?<column>[A-Za-z]+)\b",
                     RegexOptions.None,
                     TimeSpan.FromSeconds(5)))
        {
            string column = match.Groups["column"].Value;
            if (!StillReadFromTheJourneyRow.ContainsKey(column))
            {
                continue;
            }

            actual[column] = actual.GetValueOrDefault(column) + 1;
        }

        Assert.Equal(
            StillReadFromTheJourneyRow.ToDictionary(entry => entry.Key, entry => entry.Value.Count),
            actual);
    }

    /// <summary>台账里的每一条都写了理由——一个数字说不出「为什么还在那里」。</summary>
    [Fact]
    public void EveryLedgerEntryCarriesItsReason()
    {
        Assert.All(
            StillReadFromTheJourneyRow,
            entry =>
            {
                Assert.True(entry.Value.Count > 0, $"{entry.Key}: 数到 0 就该把这一条删掉，而不是留着");
                Assert.False(
                    string.IsNullOrWhiteSpace(entry.Value.Why),
                    $"{entry.Key}: 没写为什么还读旅程行");
            });
    }

    /// <summary>从本测试文件的位置推回仓库里的引擎源文件。</summary>
    private static string EnginePath([CallerFilePath] string sourceFile = "")
    {
        string repositoryRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return Path.Combine(
            repositoryRoot, "src", "ControlServer.Host", "Runtime", "JourneyRuntimeEngine.cs");
    }
}
