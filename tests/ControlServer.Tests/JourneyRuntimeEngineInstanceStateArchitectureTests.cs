using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ControlServer.Host.Runtime;

namespace ControlServer.Tests;

/// <summary>
/// <see cref="JourneyRuntimeEngine"/> 不许有注入以外的可变实例状态（control-server#904，PR #905 审查裁定）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要一道护栏。</b>生产里引擎按轮取自新的 scope（<c>Program.cs</c> 的 <c>AddScoped</c>、<c>JourneyRuntimeWorker</c> 每轮
/// <c>CreateAsyncScope</c>），测试夹具却全程复用一个实例（<c>RuntimeFixture.Engine</c>，<c>RecreateEngineAsync</c> 要用例自己调）。
/// 引擎上任何跨轮的实例状态在测试里都会「记得」上一轮，在生产里每轮都是空的：cs#904 的 <c>stagingPickupStops</c> 正是这样，
/// 测试靠这份记忆走通，生产里取消之后那一轮认不出待送点，每轮抛。夹具默认每轮新引擎不在本票（调度裁定），所以在产品这一侧挡住：
/// 引擎的实例字段只能是注入进来的协作者。
/// </para>
/// <para>
/// <b>判法。</b>反射引擎声明的每一个实例字段：主构造参数被捕获时编译器生成的字段（<c>&lt;name&gt;P</c>，name 是一个构造参数）算注入；
/// 其余的必须在 <see cref="Allowed"/> 里，并写明它为什么不是「跨轮记忆藏在测试里」的那一类。加回 cs#904 那样的
/// <c>private readonly HashSet&lt;string&gt;</c>，这里变红。
/// </para>
/// <para>
/// <b>白名单里的 <c>?? new</c> 协作者方向相反</b>：生产里它们是宿主的单例、<b>有意</b>跨轮（告警只报一次、连续失败计数、门的宽限），
/// 引擎每轮取同一个；测试夹具不传时引擎自己 <c>new</c> 一个，跨轮与否跟着引擎实例走。它们是注入的，只是带了默认值。
/// </para>
/// <para>
/// <b>没有 <c>IntegrationSlice</c> 标记</b>，理由同 <c>JourneyClosureSingleExitArchitectureTests</c>：横切的护栏挂在某个切片上，
/// 那个切片被推迟时护栏也跟着熄灭。
/// </para>
/// </remarks>
public sealed class JourneyRuntimeEngineInstanceStateArchitectureTests
{
    /// <summary>注入以外允许的实例字段，及其理由。</summary>
    private static readonly SortedDictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["_stagingDepartureWarnings"] =
            "injected (?? new): host singleton of journeys already warned about (control-server#713 review L2), deliberately outlives the per-round engine",
        ["_idleReturnMaterializationFailures"] =
            "injected (?? new): host singleton counting consecutive idle-return materialization failures (control-server#390 review L3), deliberately outlives the per-round engine",
        ["_fixedStationWarnings"] =
            "injected (?? new): host singleton of the fixed task station sweep's open warnings (control-server#391), deliberately outlives the per-round engine",
        ["_readinessRecheck"] =
            "injected (?? new): host singleton the processor's heartbeat also reads (control-server#837)",
        ["_doorReadings"] =
            "injected (?? new): host singleton holding the in-transit door grace the worker also reads (control-server#789)",
        ["runtimeOptions"] =
            "injected: options.Value, read-only configuration",
        ["_taskTypeStations"] =
            "injected: the taskTypeStations constructor parameter itself",
    };

    private static readonly Regex CapturedParameter = new(@"^<(?<name>[A-Za-z_][A-Za-z0-9_]*)>P$", RegexOptions.CultureInvariant);

    [Fact]
    public void TheEngineHoldsNoInstanceStateBeyondItsInjectedCollaborators()
    {
        Type engine = typeof(JourneyRuntimeEngine);
        HashSet<string> parameters = [.. engine.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.Name!)];
        Assert.NotEmpty(parameters);

        FieldInfo[] fields = engine.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        string[] offending = [.. fields
            .Where(field => !IsCapturedParameter(field, parameters) && !Allowed.ContainsKey(field.Name))
            .Select(field => $"{field.Name} ({field.FieldType.Name})")];

        Assert.True(
            offending.Length == 0,
            "JourneyRuntimeEngine holds instance state that is not an injected collaborator. Production takes a new engine "
            + "every round while the test fixture reuses one, so such state hides cross-round behaviour production never has "
            + "(control-server#904). Make it a local, put it on a host singleton passed in, or add it to Allowed with the reason: "
            + string.Join(", ", offending));
    }

    /// <summary>白名单里的每一个名字都还是引擎上的字段：字段改名或删掉之后白名单不留死条目。</summary>
    [Fact]
    public void EveryAllowedFieldStillExists()
    {
        HashSet<string> names = [.. typeof(JourneyRuntimeEngine)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(field => field.Name)];
        Assert.All(Allowed.Keys, name => Assert.Contains(name, names));
    }

    private static bool IsCapturedParameter(FieldInfo field, HashSet<string> parameters) =>
        field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) &&
        CapturedParameter.Match(field.Name) is { Success: true } match &&
        parameters.Contains(match.Groups["name"].Value);
}
