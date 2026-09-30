namespace ControlServer.Host.Runtime.Dispatch;

// Outside Dispatch/Criteria on purpose: StructuralDispatchBlockTests reads every SCREAMING_CASE literal in that directory as a
// dispatch reason code, and these are wire values, not verdicts.

/// <summary><c>VehicleBusinessStateSnapshot.batteryState</c> 的四个值（协议 <c>protocol-v1.0.0</c> 起）。</summary>
public static class BatteryStates
{
    public const string Sufficient = "SUFFICIENT";

    public const string Low = "LOW";

    public const string MandatoryCharge = "MANDATORY_CHARGE";

    public const string Unknown = "UNKNOWN";

    /// <summary>
    /// 本票之前每张快照写死的值。<c>PublishedBatteryState</c> 为空的旅程（本票上线前派出的）照旧发它：那些旅程已经排给车的快照里
    /// 就是这个值，同一个消息 id 重发必须逐字相同；它们也没有冻结的策略版本可以据以投影。
    /// </summary>
    public const string BeforePolicyProjection = Sufficient;
}
