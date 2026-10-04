using System.Text.RegularExpressions;
using ControlServer.Domain;

namespace ControlServer.Tests;

/// <summary>
/// 派车闸门关/开脚本（control-server#472，<c>scripts/parallel</c> 的 <c>Get-ParallelDispatchGateRefusal</c>）判据所依赖的服务端事实，
/// 钉在 CI 会跑的这一侧。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么在这里。</b>那个脚本读 v2 实例自己的库判断能不能关、开闸门：关闸看「旅程 <c>Stage</c> 不是 <c>Completed</c>」，开闸看
/// 「旅程的单是否肯定没发给 RIoT」。这两条判据各自依赖服务端的几条事实，任何一条变了，脚本就会静默判错——关闸时漏掉在途的车，
/// 或开闸时把它当成没在跑。脚本的自测 <c>Test-ParallelInstance.ps1</c> 不在 CI 里，所以这些事实在这里钉一次，
/// 那边只核对这里的用例还在（control-server#472 审查 S3）。
/// </para>
/// <para>
/// <b>钉的是哪几条</b>：建单在全仓只有一个调用点；走到那个调用点的方法只有那两条建单路径，且两者都先查闸门、在任何 <c>await</c> 之前；
/// 旅程阶段的完整成员表（终态只有 <c>Completed</c>）；引擎读在途旅程的那一整条查询；「肯定没发过」的定义原文。
/// 任何一条变红，先去改 <c>scripts/parallel/ParallelInstance.psm1</c> 的 <c>Get-ParallelDispatchGateRefusal</c> 与
/// <c>Test-ParallelOrderIntentNeverSent</c>，再改这里的期望。
/// </para>
/// <para>
/// <b>它按构造看不见什么</b>（防回归，不是证明）：不经 <c>gateway.CreateAsync(</c> 这个写法的建单（换了字段名、反射、绕过网关的 HTTP），
/// 以及不经 <c>DispatchCreateAttemptAsync</c> 却自己调网关的新路径——后者会让第一条变红（调用点变成两个）。
/// </para>
/// <para>
/// <b>没有 <c>IntegrationSlice</c> 标记</b>，理由同 <c>OnboardOutboundFunnelArchitectureTests</c>：横切的护栏挂在某个切片上，
/// 那个切片被推迟时护栏也跟着熄灭。
/// </para>
/// </remarks>
public sealed class DispatchGatePremiseArchitectureTests
{
    private const string Orchestration = "src/ControlServer.Application/WireToGateOrchestration.cs";

    private const string Engine = "src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs";

    private const string Store = "src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs";

    /// <summary>经过它才会真的建单的两条路径；它们都必须先查闸门。</summary>
    private static readonly string[] CreatePaths = ["CreateAfterExperimentalAbsenceAsync", "CreateAfterConfirmedAbsenceAsync"];

    private const string GateCheck = "if (!createDispatchPolicy.CreateEnabled)";

    private static readonly string[] ProductRoots = ["src", "tools"];

    [Fact]
    public void RiotOrdersAreCreatedAtExactlyOneCallSite()
    {
        string[] sites = [.. ProductSourceFiles()
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"gateway\.CreateAsync\(").Select(_ => Relative(file)))];

        Assert.Equal([Orchestration], sites);
        Assert.Contains("gateway.CreateAsync(", MethodBody(Read(Orchestration), "DispatchCreateAttemptAsync"), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheTwoCreatePathsReachTheCreateAttempt()
    {
        string orchestration = Read(Orchestration);
        int total = ProductSourceFiles().Sum(file => Regex.Matches(File.ReadAllText(file), @"\bDispatchCreateAttemptAsync\(").Count);
        int definition = Regex.Matches(orchestration, @"Task<MovementDispatchResult> DispatchCreateAttemptAsync\(").Count;
        int inCreatePaths = CreatePaths.Sum(path => Regex.Matches(MethodBody(orchestration, path), @"\bDispatchCreateAttemptAsync\(").Count);

        Assert.Equal(1, definition);
        Assert.Equal(CreatePaths.Length, inCreatePaths);
        Assert.Equal(definition + inCreatePaths, total);
    }

    [Theory]
    [InlineData("CreateAfterExperimentalAbsenceAsync")]
    [InlineData("CreateAfterConfirmedAbsenceAsync")]
    public void EachCreatePathChecksTheGateBeforeAnythingElseAwaits(string path)
    {
        string body = MethodBody(Read(Orchestration), path);
        int gate = body.IndexOf(GateCheck, StringComparison.Ordinal);
        int firstAwait = body.IndexOf("await ", StringComparison.Ordinal);

        Assert.True(gate >= 0, $"{path} has no '{GateCheck}'.");
        Assert.True(firstAwait < 0 || gate < firstAwait, $"{path} awaits something at {firstAwait} before the gate check at {gate}.");
        // Both arms (ArmCreateDispatchAsync, ArmExperimentalCreateDispatchAsync) and every other store call, awaited or not.
        int firstStoreCall = body.IndexOf("store.", StringComparison.Ordinal);
        Assert.True(firstStoreCall < 0 || gate < firstStoreCall, $"{path} calls the store at {firstStoreCall} before the gate check at {gate}.");
    }

    [Fact]
    public void AClosedGateWritesNothing()
    {
        Assert.Contains(
            Squash("private static MovementDispatchResult CreateDispatchDisabled(OrderIntent intent) => new(MovementDispatchOutcome.CreateDispatchDisabled, intent.UpperId, null);"),
            Squash(Read(Orchestration)), StringComparison.Ordinal);
    }

    [Fact]
    public void JourneyStagesAreExactlyTheKnownSetWithCompletedTheOnlyTerminal()
    {
        Assert.Equal(
            ["AwaitingPickupArrival", "AwaitingSublot", "AwaitingLoadResult", "AwaitingStationDeparture", "AwaitingDepartureSafety",
             "AwaitingGateArrival", "AwaitingUnloadResult", "Completed", "Blocked"],
            Enum.GetNames<JourneyRuntimeStage>());
    }

    [Fact]
    public void TheEngineReadsActiveJourneysAsStageNotCompleted()
    {
        const string query =
            "JourneyRuntimeRow[] active = await dbContext.JourneyRuntimes .Where(row => row.Stage != JourneyRuntimeStage.Completed) " +
            ".ToArrayAsync(cancellationToken).ConfigureAwait(false);";

        Assert.Single(Regex.Matches(Squash(Read(Engine)), Regex.Escape(query)));
    }

    [Fact]
    public void NeverSentIsDefinedAsTheGateScriptPortsIt()
    {
        string store = Squash(Read(Store));

        Assert.Contains(Squash(
            "return (row.Status == \"PENDING_RECONCILIATION\" && row.CreateAttemptCount == 0 && row.CreateAttemptId is null && " +
            "row.OrderId is null) || await IsNeverSentAfterUnansweredReadsAsync(row, cancellationToken).ConfigureAwait(false);"), store, StringComparison.Ordinal);
        Assert.Contains(Squash(
            "if (row.Status != \"RESULT_UNKNOWN\" || row.DispatchAuditVersion != 1 || row.CreateAttemptCount != 0 || " +
            "row.CreateAttemptId is not null || row.ExperimentalCreateAuthorizationId is not null)"), store, StringComparison.Ordinal);
        Assert.Contains(Squash(
            "return reads.Length > 0 && reads.All(item => item.Phase == \"PRE_CREATE_RECONCILIATION\" && item.Outcome is \"UNKNOWN\" or " +
            "\"NOT_FOUND\" && item.AttemptId is null && item.ReturnedOrderId is null && item.ResultPresent != true);"), store, StringComparison.Ordinal);
    }

    /// <summary>
    /// The body of the one method named <paramref name="name"/>: from the first <c>{</c> after its declaration to the matching
    /// <c>}</c>. Braces inside strings would confuse it; the methods it is pointed at have none.
    /// </summary>
    private static string MethodBody(string source, string name)
    {
        MatchCollection declarations = Regex.Matches(source, $@"\b(?:private|public|internal|protected)\b[^;{{}}=]*\s{Regex.Escape(name)}\s*\(");
        Assert.True(declarations.Count == 1, $"expected one declaration of {name}, found {declarations.Count}");
        int open = source.IndexOf('{', declarations[0].Index);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            depth += source[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"unbalanced braces after {name}");
    }

    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ");

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(ProtocolIdentityArchitectureTests.RepositoryRoot(), relative));

    private static IEnumerable<string> ProductSourceFiles()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        char separator = Path.DirectorySeparatorChar;
        return ProductRoots
            .SelectMany(top => Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                && !file.Contains($"{separator}Migrations{separator}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }

    private static string Relative(string file) =>
        Path.GetRelativePath(ProtocolIdentityArchitectureTests.RepositoryRoot(), file).Replace(Path.DirectorySeparatorChar, '/');
}
