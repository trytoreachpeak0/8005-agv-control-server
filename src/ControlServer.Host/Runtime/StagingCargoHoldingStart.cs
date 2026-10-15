namespace ControlServer.Host.Runtime;

/// <summary>
/// 待送点取货停靠上持货期限的起算点此刻该是什么（control-server#727，CP-0014 修订项一，协议 v4 候选 wire-notes「Staging pickup stops」第 3 条）。
/// 不做 I/O：引擎读好事实交进来，把结果写回 <c>JourneyRuntimeRow.CargoHoldingStartedAt</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>起算点只有三种来历</b>，这里管后两种：
/// </para>
/// <list type="number">
/// <item>使车上有货的那个 <c>LoadBatch</c> 安全闭环：装货落定那一处写（<c>??=</c>，与机台取货同一行），这里不管——还没有起算点时原样返回空，
/// 追加不让一个没开始的钟开始（零篮不计时，CP-0014 第五节第 2 处）。</item>
/// <item>以本站为取货站的需求追加给这辆车：挪到追加时刻（第 3 处）。只认起算之后的追加；锚需求、零篮时追加的那些，追加时刻都早于起算点。
/// 放回、取消与清单的其它变化不是输入，所以挪不动它；被放回的追加照样算（追加是事实，放回不收回它挪过的期限）。</item>
/// <item>货被撤销到一篮不剩：撤掉（为空），下一篮闭环时由第 1 种重新写（第 2 处）。</item>
/// </list>
/// <para>
/// 机台取货停靠不走这里：那里的期限「不因新增停靠重置」（REQ-0354 原文），一旦给出就不变。
/// </para>
/// </remarks>
public static class StagingCargoHoldingStart
{
    /// <param name="startedAt">落库的起算点。</param>
    /// <param name="cargoOnBoard">旅程上有已装上（<c>LOADED</c>）的需求。正在装的那一批还没闭环，不算。</param>
    /// <param name="appendedForTheStationAt">这趟旅程上以本站为取货站的各条需求的追加时刻，被移除的也在内。</param>
    public static DateTimeOffset? Next(
        DateTimeOffset? startedAt,
        bool cargoOnBoard,
        IEnumerable<DateTimeOffset> appendedForTheStationAt)
    {
        ArgumentNullException.ThrowIfNull(appendedForTheStationAt);
        if (!cargoOnBoard || startedAt is not { } start)
        {
            return null;
        }
        DateTimeOffset latest = start;
        foreach (DateTimeOffset appendedAt in appendedForTheStationAt)
        {
            if (appendedAt > latest)
            {
                latest = appendedAt;
            }
        }
        return latest;
    }
}
