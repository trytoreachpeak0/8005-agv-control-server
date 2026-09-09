namespace ControlServer.Domain;

/// <summary>
/// 一个仓位的逐仓核验确认（REQ-0263）。
/// </summary>
/// <remarks>
/// 三个信号各自确认：开、关、到位。三个都是 <c>true</c> 才算这一仓通过——半个确认不是确认。
/// </remarks>
public sealed record SlotVerificationConfirmation(
    int PhysicalSlotNumber,
    bool OpenSignalConfirmed,
    bool CloseSignalConfirmed,
    bool InPlaceSignalConfirmed,
    string? FieldRecordReference = null)
{
    public bool IsComplete => OpenSignalConfirmed && CloseSignalConfirmed && InPlaceSignalConfirmed;
}

/// <summary>
/// 一台车没能取得仓位配置就绪的原因码。
/// </summary>
/// <remarks>
/// 原因码是每车一份的。**门禁是每车判定**：一台车缺东西，说的只是这一台车的事，
/// 与另外两台车是否就绪毫无关系。
/// </remarks>
public static class SlotReadinessReasonCode
{
    public const string Ready = "SLOT_CONFIGURATION_READY";
    public const string NeverVerified = "SLOT_CONFIGURATION_NEVER_VERIFIED";
    public const string IoBindingIncomplete = "SLOT_IO_BINDING_INCOMPLETE";
    public const string VerificationIncomplete = "SLOT_VERIFICATION_INCOMPLETE";
    public const string VerificationNegative = "SLOT_VERIFICATION_NEGATIVE";
    public const string WholeVehicleMaintenance = "WHOLE_VEHICLE_MAINTENANCE_IN_PROGRESS";
    public const string HardwareChanged = "HARDWARE_CHANGED_REVERIFICATION_REQUIRED";
}

/// <summary>
/// 一台车在某一版整车模型下的仓位配置就绪判定。
/// </summary>
/// <remarks>
/// <see cref="SlotsMissingBinding"/> 与 <see cref="SlotsMissingVerification"/> 列的是**这台车之内**
/// 的缺口。REQ-0263 的「不允许抽样」是一台车之内不许抽几个仓测，不是三台车不能分批推进。
/// </remarks>
public sealed record SlotConfigurationReadinessVerdict(
    string AgvId,
    string SlotModelVersionId,
    bool Ready,
    string ReasonCode,
    IReadOnlyList<int> SlotsMissingBinding,
    IReadOnlyList<int> SlotsMissingVerification,
    IReadOnlyList<int> SlotsVerifiedNegative);

/// <summary>
/// 一次「服务端在线核验」失败：车载端不能在断线时自行进入整车配置维护态（REQ-0262）。
/// </summary>
public sealed class VehicleMaintenanceRequiresServerLinkException : InvalidOperationException
{
    public VehicleMaintenanceRequiresServerLinkException(string message)
        : base(message)
    {
    }

    public VehicleMaintenanceRequiresServerLinkException()
        : base("Whole-vehicle configuration maintenance requires an online server link.")
    {
    }

    public VehicleMaintenanceRequiresServerLinkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 一次抽样式核验被拒（REQ-0263）。
/// </summary>
/// <remarks>
/// 拒的是「一台车里只测了几个仓就报核验通过」。这个异常存在本身就是那条禁令的落点：少交一个仓位，
/// 得到的不是一份缺口清单，而是一次拒绝——缺口清单会被人当成待办，拒绝不会。
/// </remarks>
public sealed class SampledVerificationRejectedException : InvalidOperationException
{
    public SampledVerificationRejectedException(string message)
        : base(message)
    {
    }

    public SampledVerificationRejectedException()
        : base("REQ-0263 requires every slot on the vehicle to be confirmed; sampling is rejected.")
    {
    }

    public SampledVerificationRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
