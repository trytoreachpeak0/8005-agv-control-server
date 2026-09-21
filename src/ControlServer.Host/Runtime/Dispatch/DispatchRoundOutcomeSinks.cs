namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// 轮末的两个汇总，按固定先后各调一次（批次7-09，control-server#214）：结构性阻断在前，防饥饿升级在后。
/// </summary>
/// <remarks>
/// <para>
/// 派车轮只认一个 <see cref="IDispatchRoundOutcomeSink"/>，这个组合让第二个汇总加进来时不必改派车轮。
/// 一个抛出就不再调后面的，与只有一个时「汇总抛出即整轮抛出」相同。
/// </para>
/// <para>
/// <b>先后写在这里，不写在注册处</b>（审查中 1）。「本轮刚立结构性阻断的需求不先告一次饥饿」只靠这个先后成立：一条老需求
/// 第一次被看到时就越过了阈值、同一轮又第一次立 <c>AREA_STATION_NOT_FOUND</c>，防饥饿汇总要读到这一轮刚写下的阻断才不告警。
/// 原来的写法是注册处工厂里的一个列表，两项对调后全部用例照样绿——测试夹具自己组了一个次序正确的列表。现在两个汇总是构造参数，
/// 宿主与夹具用的是同一个类，排反只能改这里的方法体，而那会让
/// <c>Batch7StarvationEscalationTests.ADemandUnderAStructuralDispatchBlockIsNotEscalatedHoweverLongItWaits</c> 变红。
/// </para>
/// </remarks>
public sealed class DispatchRoundOutcomeSinks(StructuralDispatchBlockSink structural, StarvationEscalationSink starvation)
    : IDispatchRoundOutcomeSink
{
    public async Task RecordAsync(DispatchRoundOutcome outcome, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await structural.RecordAsync(outcome, cancellationToken).ConfigureAwait(false);
        await starvation.RecordAsync(outcome, cancellationToken).ConfigureAwait(false);
    }
}
