namespace ControlServer.Application;

/// <summary>
/// 业务键 <c>TransportDemandKey</c> 的拼法与它的逆：<c>{sublot}|{workType}</c>（批次7-12，control-server#217 从
/// <c>HttpMesIngestCatalog</c> 提出来）。
/// </summary>
/// <remarks>
/// <para>
/// <b>拼法由目录适配器承担</b>：业务键在 <c>HttpMesIngestCatalog</c> 读目录时由这里拼出，之后原样存进积压行、受理行与抑制行。
/// 需要从业务键读回任务类型的只有看板的积压卡片——积压行没有任务类型列（批次 7 零迁移），而优先级带要任务类型。拼与拆放在一处，
/// 改了拼法这里的拆法跟着改，不会有第二份悄悄对不上。
/// </para>
/// <para>
/// 任务类型是 <see cref="TransportTaskTypes"/> 那几个字面值，不含竖线；批次号可能含，所以拆取<b>最后一个</b>竖线之后那段。
/// </para>
/// </remarks>
public static class TransportDemandKeys
{
    public const char Separator = '|';

    public static string Compose(string sublot, string workType) => $"{sublot}{Separator}{workType}";

    /// <summary>业务键里的任务类型；不是这个拼法（没有竖线）时为空。</summary>
    public static string? WorkTypeOf(string transportDemandKey)
    {
        ArgumentNullException.ThrowIfNull(transportDemandKey);
        int separator = transportDemandKey.LastIndexOf(Separator);
        return separator >= 0 ? transportDemandKey[(separator + 1)..] : null;
    }
}
