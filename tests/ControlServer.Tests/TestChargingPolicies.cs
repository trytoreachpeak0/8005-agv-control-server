using ControlServer.Application;

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
    public static IChargingPolicyResolver AllApproved { get; } = new FixedResolver(_ => true);

    /// <summary>一版都没有：每辆车都不投运。</summary>
    public static IChargingPolicyResolver None { get; } = new FixedResolver(_ => false);

    /// <summary>生效版本的适用范围只含这些车。</summary>
    public static IChargingPolicyResolver Only(params string[] vehicleKeys)
    {
        HashSet<string> covered = new(vehicleKeys, StringComparer.Ordinal);
        return new FixedResolver(covered.Contains);
    }

    public static EffectiveChargingPolicy Effective(IReadOnlyList<string>? scope = null) =>
        new(
            new ChargingPolicyVersion(1, "test-fixture", null, Approved, "test fixture", Content with { VehicleScope = scope ?? [] }),
            [new ChargingPolicyApproval("test-fixture-approval", 1, "test fixture", "TEST", Approved, "tests/ControlServer.Tests/TestChargingPolicies.cs", ChargingPolicyApprovalSources.TestFixture)],
            new ChargingPolicyActivation("test-fixture-activation", 1, 1, Approved, "test fixture"));

    private sealed class FixedResolver(Func<string, bool> covers) : IChargingPolicyResolver
    {
        public Task<VehicleChargingPolicyDecision> ResolveForNewDecisionAsync(string vehicleKey, CancellationToken cancellationToken) =>
            Task.FromResult(covers(vehicleKey)
                ? new VehicleChargingPolicyDecision(vehicleKey, ChargingPolicyCommissioningReasons.Effective, Effective(), null)
                : new VehicleChargingPolicyDecision(vehicleKey, ChargingPolicyCommissioningReasons.NotApproved, null, null));

        public Task<ChargingPolicyVersion> ReadFrozenAsync(long version, CancellationToken cancellationToken) =>
            version == 1
                ? Task.FromResult(Effective().Policy)
                : throw new InvalidOperationException($"The test fixture has only policy version 1, not {version}.");
    }
}
