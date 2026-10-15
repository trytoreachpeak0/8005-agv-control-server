using ControlServer.Host.Runtime;

namespace ControlServer.Tests;

/// <summary>
/// 待送点持货期限的起算点怎样挪（control-server#727，CP-0014 修订项一、第五节第 2、3 处）：只看车上有没有货、有没有以本站为取货站的新需求追加进来。
/// </summary>
public sealed class StagingCargoHoldingStartTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>货被撤销到一篮不剩：期限撤掉（起算点为空），不论有没有追加。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void NoCargoLeftClearsTheStart() =>
        Assert.Null(StagingCargoHoldingStart.Next(Start, cargoOnBoard: false, [Start.AddMinutes(1)]));

    /// <summary>还没有起算点（一篮没装过）：追加不让它开始，开始只由装货闭环写。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void AnAppendDoesNotStartAClockThatHasNotStarted() =>
        Assert.Null(StagingCargoHoldingStart.Next(null, cargoOnBoard: true, [Start.AddMinutes(1)]));

    /// <summary>起算之后追加进来的：起算点挪到追加时刻。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void AnAppendAfterTheStartMovesItToTheAppend() =>
        Assert.Equal(Start.AddMinutes(3), StagingCargoHoldingStart.Next(Start, cargoOnBoard: true, [Start.AddMinutes(3)]));

    /// <summary>起算之前就在旅程上的（锚、零篮时追加的）：不挪。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void AnAppendBeforeTheStartLeavesIt() =>
        Assert.Equal(Start, StagingCargoHoldingStart.Next(Start, cargoOnBoard: true, [Start.AddMinutes(-1), Start]));

    /// <summary>几条追加取最晚的那一条，与先后顺序无关。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void TheLatestAppendWins() =>
        Assert.Equal(
            Start.AddMinutes(4),
            StagingCargoHoldingStart.Next(Start, cargoOnBoard: true, [Start.AddMinutes(1), Start.AddMinutes(4), Start.AddMinutes(2)]));

    /// <summary>车上有货、没有新追加：原样。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-08")]
    [Trait("IntegrationSlice", "FP-IS-11")]
    public void NothingNewKeepsTheStart() =>
        Assert.Equal(Start, StagingCargoHoldingStart.Next(Start, cargoOnBoard: true, []));
}
