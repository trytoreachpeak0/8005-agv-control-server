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
