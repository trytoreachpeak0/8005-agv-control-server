using System.Globalization;

namespace ControlServer.Application;

// 批次8-18（control-server#389）：空闲返回的资格与原子承诺。承诺之后的建单、到点、离点与失败分流在批次8-19（control-server#390）。

/// <summary>
/// 强制充电入口线（<c>REQ-0290</c>、<c>REQ-0291</c> 的 <c>MandatoryChargeEntryThreshold</c>）：低于它的车不接普通搬运，也不做空闲返回。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是空闲返回读「强制充电线」的唯一接缝</b>（调度 Coordinator 9 于 2026-09-29 定）。批次 8 的过渡实现读 <c>JourneyRuntime:MinimumBatteryPercent</c>；
/// 批次9-05（control-server#403）把实现换成按车读充电策略版本的 <c>MandatoryChargeEntryThreshold</c>（<c>PolicyMandatoryChargeLine</c>），
/// 调用方没改，那个配置项已删。
/// </para>
/// <para>
/// 只回答「低于线没有」。电量读不到、车在充电，由调用方各自按不接新承诺处理：那是读数的事，不是线的事。
/// </para>
/// <para>
/// 签名按车、异步（审查 S3）：按车读充电策略版本、要读库。
/// </para>
/// </remarks>
public interface IMandatoryChargeLine
{
    /// <summary>这辆车在这个电量下是否低于它的强制充电入口线。低于即不接空闲返回的新承诺。</summary>
    ValueTask<bool> IsBelowLineAsync(string vehicleKey, int batteryPercent, CancellationToken cancellationToken);

    /// <summary>这辆车的线从哪来、是多少，给日志与证据用，例如 <c>30 (charging policy version 3: MandatoryChargeEntryThreshold)</c>。</summary>
    string Describe(string vehicleKey);
}

/// <summary>空闲返回的身份。</summary>
public static class IdleReturnIdentity
{
    /// <summary>身份的前缀：以它开头的 <c>JourneyId</c> 是一次空闲返回，不是一趟搬运。</summary>
    public const string JourneyIdPrefix = "idle-return:";

    /// <summary>
    /// 一次空闲返回承诺的旅程 id，写进它的用途占有与站点预占（持有者）。形如
    /// <c>idle-return:BROKERX-0001:20260929T081500123Z</c>：车与承诺时刻各一段，同一辆车先后两次承诺不会同名。
    /// </summary>
    /// <remarks>
    /// 承诺时还没有旅程行（批次8-18 只取得占有与预占）；批次8-19 按这个 id 物化旅程与订单意图，同一个承诺补建多少次都是同一趟。
    /// </remarks>
    public static string JourneyIdFor(string vehicleKey, DateTimeOffset committedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleKey);
        return JourneyIdPrefix + vehicleKey + ":" +
               committedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
    }
}
