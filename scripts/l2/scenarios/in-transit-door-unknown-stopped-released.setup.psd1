### control-server#789（CP-0017，REQ-0246 与 REQ-0167）：行驶中仓位状态读不到 5 秒，宽限用完按住、急停；读数恢复连续 10 秒后
### 服务端自己解除，单仍停着，人按「继续原单」后车再走。
#
# 单车、合成车载端。三处改动环境：
# - 急停重试的退避调到 20 秒，理由与 in-transit-door-not-locked 相同：本装置每秒评估一次，场景要看的几个阶段加起来都比它短。
# - 打开故障人工处置入口：续行（RESUME_HELD_ORDER）挂在它上面，产品里默认不挂，现场要明确打开。
# - 宽限与自动解除前连续读回锁闭的时长写明成产品默认值（3 秒、10 秒），取红时只改 DoorReadingRestoredFor（改成 00:05:00 即没有自动解除）。
@{
    VehicleFaultRecovery = $true
    RiotCommands = @{
        EmergencyRetryInitialDelay = '00:00:20'
    }
    VehicleFault = @{
        DoorUnknownGrace       = '00:00:03'
        DoorReadingRestoredFor = '00:00:10'
    }
}
