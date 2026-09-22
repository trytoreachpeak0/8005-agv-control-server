### 车辆故障的人工清除（control-server#299）。
#
# 单车、合成车载端。三处改动环境：
# - 打开故障人工清除入口。产品里它默认不挂，现场要明确打开；场景打开的是同一个开关。
# - 打开人工确认解除急停入口（REQ-0356）：在途单 FAILED 的车停在两站之间会被急停锁住，清除故障要求急停先解除。
# - 急停重试退避调到 20 秒，理由与 emergency-stop-operator-release 相同：本装置每秒评估一次，默认 2 秒的退避会让
#   「重试到期又发了一次」混进场景要数的调用次数里。
@{
    VehicleFaultRecovery = $true
    EmergencyStopRelease = $true
    RiotCommands = @{
        EmergencyRetryInitialDelay = '00:00:20'
    }
}
