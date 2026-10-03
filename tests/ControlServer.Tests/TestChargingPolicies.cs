using ControlServer.Application;
using ControlServer.Host.Runtime.Dispatch.Criteria;

namespace ControlServer.Tests;

/// <summary>
/// 测试夹具里的已批准测试策略（control-server#400）。派车链的逐车投运判据经 <see cref="IChargingPolicyResolver"/> 读策略；公共夹具默认装
/// <see cref="AllApproved"/>，个别用例换成 <see cref="Only"/> 或 <see cref="None"/>。
/// </summary>
/// <remarks>
/// <para>
/// 取值照票面：充满线 80、强制充电线 30、最低任务后电量余量 30、每趟耗电估计 0，适用全部车辆。批次9-05 把电量判据改成「电量 − 每趟估计 ≥ 余量，
/// 且不低于强制充电线」之后，这组值与今天的 <c>MinimumBatteryPercent = 30</c> 逐条等价，既有用例的派车结论不变。
/// </para>
/// <para>批准来源写 <see cref="ChargingPolicyApprovalSources.TestFixture"/>，不冒充现场批准。</para>
/// </remarks>
internal static class TestChargingPolicies
{
    public static readonly ChargingPolicyContent Content = new(
        MinimumPostTaskBatteryMarginPercent: 30,
        MandatoryChargeEntryThresholdPercent: 30,
        ChargingCompletionThresholdPercent: 80,
        EstimatedTaskConsumptionPercent: 0,
        ProgressStabilizationSeconds: 180,
        ProgressObservationWindowSeconds: 600,
        ProgressMinimumIncreasePercent: 3,
        VehicleScope: []);

    private static readonly DateTimeOffset Approved = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);

    /// <summary>每辆车都有一版已批准、已激活的测试策略（版本 1）。</summary>
    public static IChargingPolicyResolver AllApproved { get; } = new FixedResolver(_ => true, Content);

    /// <summary>一版都没有：每辆车都不投运。</summary>
    public static IChargingPolicyResolver None { get; } = new FixedResolver(_ => false, Content);

    /// <summary>生效版本的适用范围只含这些车。</summary>
    public static IChargingPolicyResolver Only(params string[] vehicleKeys)
    {
        HashSet<string> covered = new(vehicleKeys, StringComparer.Ordinal);
        return new FixedResolver(covered.Contains, Content);
    }

    /// <summary>
    /// 与 <see cref="Content"/> 相同、只把强制充电线与最低任务后电量余量都换成 <paramref name="line"/>（每趟估计仍是 0）：与此前
    /// <c>MinimumBatteryPercent = line</c> 逐条等价（批次9-05，control-server#403）。公共夹具此前配的是 40，靠它保持既有用例的判定不变。
    /// </summary>
    public static ChargingPolicyContent ContentAt(int line) =>
        Content with { MandatoryChargeEntryThresholdPercent = line, MinimumPostTaskBatteryMarginPercent = line };

    /// <summary>每辆车都有一版已批准的测试策略，两道线都是 <paramref name="line"/>。</summary>
    public static IChargingPolicyResolver AllApprovedAt(int line) => new FixedResolver(_ => true, ContentAt(line));

    /// <summary>每辆车都有一版已批准的这份内容（版本 1）。</summary>
    public static IChargingPolicyResolver AllApprovedWith(ChargingPolicyContent content) => new FixedResolver(_ => true, content);

    /// <summary>派车事实里的那一份（批次9-05）：版本 1、<see cref="ContentAt"/> 的两道线。</summary>
    public static DispatchBatteryPolicy Battery(int line = 30) => new(1, ContentAt(line));

    public static EffectiveChargingPolicy Effective(IReadOnlyList<string>? scope = null) => Effective(Content, scope);

    public static EffectiveChargingPolicy Effective(ChargingPolicyContent content, IReadOnlyList<string>? scope = null, long version = 1) =>
        new(
            new ChargingPolicyVersion(version, "test-fixture", null, Approved, "test fixture", content with { VehicleScope = scope ?? [] }),
            [new ChargingPolicyApproval("test-fixture-approval", version, "test fixture", "TEST", Approved, "tests/ControlServer.Tests/TestChargingPolicies.cs", ChargingPolicyApprovalSources.TestFixture)],
            new ChargingPolicyActivation("test-fixture-activation", version, version, Approved, "test fixture"));

    private sealed class FixedResolver(Func<string, bool> covers, ChargingPolicyContent content) : IChargingPolicyResolver
    {
        public Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(string vehicleKey, CancellationToken cancellationToken) =>
            Task.FromResult(covers(vehicleKey)
                ? new VehicleChargingPolicyDecision(vehicleKey, ChargingPolicyCommissioningReasons.Effective, Effective(content), null)
                : new VehicleChargingPolicyDecision(vehicleKey, ChargingPolicyCommissioningReasons.NotApproved, null, null));

        public Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken) =>
            version == 1
                ? Task.FromResult(Effective(content).Policy)
                : throw new InvalidOperationException($"The test fixture has only policy version 1, not {version}.");
    }

    /// <summary>
    /// 可以轮中激活新版本的解析器（批次9-05）：版本号递增、只追加，<see cref="Activate"/> 之后新决定读新版本，已记下的号照旧读回旧内容。
    /// </summary>
    public sealed class Versions : IChargingPolicyResolver
    {
        private readonly Dictionary<long, ChargingPolicyContent> _versions = [];

        public Versions(ChargingPolicyContent first) => Activate(first);

        public long Active { get; private set; }

        /// <summary>写一个新版本并激活它，返回版本号。</summary>
        public long Activate(ChargingPolicyContent content)
        {
            Active += 1;
            _versions[Active] = content;
            return Active;
        }

        /// <summary>
        /// 下一次新决定读完之后运行一次（读到的仍是运行之前的版本），再清空：用来在一轮派车的中途激活新版本。
        /// </summary>
        public Action? AfterNextResolve { get; set; }

        /// <summary>新决定读了几次。</summary>
        public int Resolves { get; private set; }

        public Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(string vehicleKey, CancellationToken cancellationToken)
        {
            VehicleChargingPolicyDecision decision = new(
                vehicleKey, ChargingPolicyCommissioningReasons.Effective, Effective(_versions[Active], null, Active), null);
            Resolves++;
            Action? after = AfterNextResolve;
            AfterNextResolve = null;
            after?.Invoke();
            return Task.FromResult(decision);
        }

        public Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken) =>
            _versions.TryGetValue(version, out ChargingPolicyContent? content)
                ? Task.FromResult(Effective(content, null, version).Policy)
                : throw new InvalidOperationException($"Charging policy version {version} was never written.");
    }
}
