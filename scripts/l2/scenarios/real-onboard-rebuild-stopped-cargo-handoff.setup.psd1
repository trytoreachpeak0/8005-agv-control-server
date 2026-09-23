### 货不在原仓、重建停住之后转进车载端异常处置会话交接（control-server#345，交接衔接 a/b/c）。真车载端 WPF + 真 slots-simulator。
#
# 四处改动环境：
# - 打开车载端恢复入口（RecoveryResume）：出厂的 wireToGate.recoveryResumeEnabled 是 false，不开就没有「故障交接」按钮。
# - 打开故障人工清除入口（VehicleFaultRecovery）：清除与转交接都挂在 control-server#299 的同一个入口上，产品里默认不挂。
# - 到站期限 30 秒，理由与 real-onboard-normal-load 相同（真装置上录入并提交要 5 秒上下，默认 5 秒卡在线上）。
# - 同车重建（control-server#318）的延迟由产品默认 30 秒调成 8 秒，省机时。本场景的重建不会真的建单（货不在原仓就停住），
#   延迟只决定停住来得多快。
@{
    Onboard                     = 'Real'
    RecoveryResume              = $true
    VehicleFaultRecovery        = $true
    StationDepartureWaitTimeout = '00:00:30'
    OwnOrderRebuildDelay        = '00:00:08'
}
