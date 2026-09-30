using System.Globalization;

namespace ControlServer.Application;

// 批次9-06（control-server#404）：充电分配、预占与去桩。到桩、充电中、充满、离桩与释放在批次9-07（control-server#405）。

/// <summary>一次充电承诺的身份：旅程、充电周期与 RIoT 单号都从同一个旅程 id 派生。</summary>
/// <remarks>
/// 充电与空闲返回一样挂在「无需求旅程」上（control-server#386 的方案甲）：故障监看、急停确认、建单与对账、并发令牌都挂在旅程上，
/// 站点独占行的持有者列就是 <c>JourneyId</c>。同一次承诺的周期 id 与单号不随重试、重启而变（<c>REQ-0283</c>：不换 <c>upperId</c>）。
/// </remarks>
public static class ChargingIdentity
{
    /// <summary>身份的前缀：以它开头的 <c>JourneyId</c> 是一次充电，不是一趟搬运，也不是空闲返回。</summary>
    public const string JourneyIdPrefix = "charging:";

    private const string CycleIdPrefix = "charging-cycle:";

    private const string UpperIdPrefix = "W2G-CHARGE-";

    /// <summary>
    /// 一次充电承诺的旅程 id，形如 <c>charging:BROKERX-0001:20260930T081500123Z</c>：车与承诺时刻各一段，同一辆车先后两次承诺不会同名。
    /// </summary>
    public static string JourneyIdFor(string vehicleKey, DateTimeOffset committedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        return JourneyIdPrefix + vehicleKey + ":" +
               committedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>这趟充电旅程的充电周期 id。</summary>
    public static string CycleIdFor(string journeyId) => CycleIdPrefix + Suffix(journeyId);

    /// <summary>
    /// 这次充电的 RIoT 单号：<c>charging:BROKERX-0001:20260930T081500123Z</c> → <c>W2G-CHARGE-BROKERX-0001-20260930T081500123Z</c>。
    /// 与搬运（<c>W2G-{需求}-PICKUP-{代次}</c>）、空闲返回（<c>W2G-IDLE-…</c>）同一个前缀、不同的第二段。
    /// </summary>
    public static string UpperIdFor(string journeyId) => UpperIdPrefix + Suffix(journeyId).Replace(':', '-');

    private static string Suffix(string journeyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journeyId);
        if (!journeyId.StartsWith(JourneyIdPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{journeyId}' is not a charging journey id.", nameof(journeyId));
        }
        return journeyId[JourneyIdPrefix.Length..];
    }
}
