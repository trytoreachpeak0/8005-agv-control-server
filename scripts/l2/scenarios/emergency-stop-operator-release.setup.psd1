### 人工确认解除急停（REQ-0356，control-server#63）。
#
# 单车、合成车载端。两处改动环境：
# - 打开人工确认解除入口。产品里它默认不挂，现场要明确打开；场景打开的是同一个开关。
# - 急停重试退避调到 20 秒，理由与 emergency-stop-single-trigger 相同：本装置每秒评估一次，默认 2 秒的退避会让
#   「重试到期又发了一次」混进场景要数的调用次数里。退避本身是现场参数，不是开关。
@{
    EmergencyStopRelease = $true
    RiotCommands = @{
        EmergencyRetryInitialDelay = '00:00:20'
    }
}
