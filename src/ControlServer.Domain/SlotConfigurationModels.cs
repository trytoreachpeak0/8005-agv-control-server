using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ControlServer.Domain;

/// <summary>
/// 一次变更走哪条治理路径。
/// </summary>
/// <remarks>
/// REQ-0260 与 REQ-0261 的分界：只动模板的长宽高或花篮兼容性走模板资料变更；动仓位集合、物理
/// 编号、SlotPosition、IO 点位、信号极性或传感器，走整车维护。
///
/// REQ-0261 是安全条款而不是治理偏好：当前形态下改一颗 IO 点位只改车上的本地配置，服务端毫不
/// 知情，车照常接单。分出这条路要落的正是「服务端知情」。
/// </remarks>
public enum SlotConfigurationChangeRoute
{
    /// <summary>没有可识别的差异。</summary>
    NoChange,

    /// <summary>模板资料变更：长、宽、高、花篮兼容性。</summary>
    TemplateSpecification,

    /// <summary>整车维护：仓位集合、物理编号、SlotPosition、IO 点位、信号极性、传感器。</summary>
    WholeVehicleMaintenance
}

/// <summary>单仓模板的受控业务规格。只有长宽高与花篮兼容性，不记载重、不记所属面。</summary>
public sealed record SlotTemplateSpecification(
    int LengthMm,
    int WidthMm,
    int HeightMm,
    IReadOnlyList<string> CompatibleBasketTypes);

/// <summary>整车模型里的一个仓位：物理编号、SlotPosition，以及它引用的模板。</summary>
public sealed record SlotModelSlotSpecification(
    int PhysicalSlotNumber,
    string SlotPosition,
    string SlotTemplateKey,
    long SlotTemplateVersion);

/// <summary>一台车上一个仓位的 IO 事实。</summary>
public sealed record SlotIoBindingSpecification(
    int PhysicalSlotNumber,
    string UnlockOutputPoint,
    string LockFeedbackInputPoint,
    string LightCurtainInputPoint,
    string SignalPolarity,
    int PulseResetMilliseconds);

/// <summary>
/// 仓位配置指纹：两端**必须逐字节一致**地算出同一个值的那套规范化摘要。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么它必须是两端共用的一套算法。</b>协议 v2 的消息 7 <c>SlotConfigurationActivationCommand</c>
/// **不携带配置内容**——整个协议里没有任何一条消息携带仓位 IO 绑定。它带的是版本号与指纹。所以那次
/// 激活是一次**核验**：服务端发它批准的那一版的指纹，车算自己手上那份的指纹，相等才切换，不等就
/// 拒绝并报 <c>SLOT_CONFIGURATION_FINGERPRINT_MISMATCH</c>。两端各算各的，这条握手就永远不成立。
/// </para>
/// <para>
/// <b>摘要只取两端都有的那六个字段。</b>车载端另有一个 <c>SlotPosition</c>（仓位在车上的位置名），
/// 服务端这一侧根本没有这个概念——把它算进去，服务端就算不出车能算出的那个值。
/// </para>
/// <para>
/// <b>版本名不在摘要里。</b>摘要回答的是「两边手上的硬件事实是不是同一份」，版本名是另一个问题，
/// 消息 7 用 <c>targetSlotConfigurationVersion</c> 单独带。把版本名算进去，等于要求车知道服务端的
/// 版本命名，而车恰恰不知道。
/// </para>
/// <para>
/// <b>它不是 <c>GovernedConfigurationSnapshotRow.ContentSha256</c>。</b>那一个是 #9 的不可改写快照
/// 对自己内容的摘要，服务端内部审计用，格式随快照的序列化走；这一个是跨端契约，格式在这里写死。
/// 两者恰好都对同一批绑定取 SHA-256，但它们回答的是不同的问题，不该共用一个实现。
/// </para>
/// </remarks>
public static class SlotConfigurationFingerprint
{
    /// <summary>字段分隔符。选一个不可能出现在 IO 点名里的字符。</summary>
    private const char FieldSeparator = '';

    /// <summary>仓位分隔符。</summary>
    private const char SlotSeparator = '';

    /// <summary>
    /// 按仓号升序把六个字段规范化后取 SHA-256，小写十六进制。
    /// </summary>
    /// <remarks>
    /// 数字一律用不变文化格式化——按当前区域格式化会在某些区域下给出带分组分隔符的毫秒数，那样两台
    /// 机器算出的指纹会不同，而且只在部署到那些机器上时才不同。
    /// </remarks>
    public static string Compute(IEnumerable<SlotIoBindingSpecification> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        StringBuilder canonical = new();
        foreach (SlotIoBindingSpecification binding in bindings.OrderBy(item => item.PhysicalSlotNumber))
        {
            if (canonical.Length > 0)
            {
                canonical.Append(SlotSeparator);
            }
            canonical
                .Append(binding.PhysicalSlotNumber.ToString(CultureInfo.InvariantCulture)).Append(FieldSeparator)
                .Append(binding.UnlockOutputPoint).Append(FieldSeparator)
                .Append(binding.LockFeedbackInputPoint).Append(FieldSeparator)
                .Append(binding.LightCurtainInputPoint).Append(FieldSeparator)
                .Append(binding.SignalPolarity).Append(FieldSeparator)
                .Append(binding.PulseResetMilliseconds.ToString(CultureInfo.InvariantCulture));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}

/// <summary>
/// 车载端报上来的配置声明的核验结论。
/// </summary>
/// <remarks>
/// REQ-0258：服务端是唯一权威维护入口。车上报的东西只被核验，**永远不被采信为权威** —— 这个
/// 类型没有任何一条路能写回模板、模型或生效配置。
/// </remarks>
public sealed record VehicleDeclarationVerdict(
    string AgvId,
    bool Matches,
    IReadOnlyList<string> MismatchedFields);

/// <summary>
/// 8005 三台现有车已批准的八仓硬件事实（REQ-0267）。
/// </summary>
/// <remarks>
/// <c>DO1</c>～<c>DO8</c> 对应 1～8 号物理仓位开锁，<c>DI1</c>～<c>DI8</c> 对应锁反馈，
/// <c>DI9</c>～<c>DI16</c> 对应仓内光幕，500 ms 脉冲复位。这些是**已批准的版本化不可改写内容**，
/// 不是可随手编辑的初始数据：它们和别的版本走同一条发布路径，因此同样产出快照与审计，发布之后
/// 同样改不动。
/// </remarks>
public static class ApprovedSlotHardwareFacts
{
    public const string ModelKey = "8005-eight-slot";
    public const string TemplateKey = "8005-standard-slot";
    public const int SlotCount = 8;
    public const int PulseResetMilliseconds = 500;
    public const string SignalPolarity = "ACTIVE_HIGH";

    public static IReadOnlyList<SlotIoBindingSpecification> IoBindings { get; } =
    [
        .. Enumerable.Range(1, SlotCount).Select(number => new SlotIoBindingSpecification(
            number,
            $"DO{number}",
            $"DI{number}",
            $"DI{number + SlotCount}",
            SignalPolarity,
            PulseResetMilliseconds))
    ];
}
