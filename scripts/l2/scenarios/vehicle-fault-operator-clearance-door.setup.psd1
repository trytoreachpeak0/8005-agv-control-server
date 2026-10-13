### 车辆故障人工清除的门锁那一种（CP-0011，control-server#539 审查 S-4）：门锁急停、单在 RIoT 结束、确认门锁已检修后经空车入口解除、
### 清除门锁故障、同车同需求重建。
#
# 单车、合成车载端。四处改动环境：
# - 打开故障人工清除入口与人工确认解除入口。产品里都默认不挂，现场要明确打开。
# - 急停重试退避调到 20 秒，理由与 emergency-stop-single-trigger 相同。
# - 同车重建延迟由产品默认 30 秒调成 8 秒，省机时；延迟本身在 L1 按拨钟测。
@{
    OwnOrderRebuildDelay = '00:00:08'
    VehicleFaultRecovery = $true
    EmergencyStopRelease = $true
    RiotCommands = @{
        EmergencyRetryInitialDelay = '00:00:20'
    }
}
