### 本服务端的单在 RIoT 被取消、车上有货：同车重建前先证明货在原仓（control-server#366，CP-0007 修订的 REQ-0360）。真车载端 WPF + 真 slots-simulator。
#
# 四处改动环境，与 real-onboard-rebuild-stopped-cargo-handoff 相同：
# - 打开车载端恢复入口（RecoveryResume）：出厂的 wireToGate.recoveryResumeEnabled 是 false，不开就没有「故障交接」按钮。
# - 打开故障人工清除入口（VehicleFaultRecovery）：转交接挂在 control-server#299 的同一个入口上，产品里默认不挂。
# - 到站期限 30 秒，理由与 real-onboard-normal-load 相同（真装置上录入并提交要 5 秒上下，默认 5 秒卡在线上）。
# - 同车重建的延迟由产品默认 30 秒调成 8 秒，省机时。第 1 件里延迟在别人的急停锁着期间走完，第 2 件里延迟一到就等快照。
@{
    Onboard                     = 'Real'
    RecoveryResume              = $true
    VehicleFaultRecovery        = $true
    StationDepartureWaitTimeout = '00:00:30'
    OwnOrderRebuildDelay        = '00:00:08'
}
