### control-server#36：开着恢复会话时重启真车载端，重连之后恢复入口还在不在。真车载端 WPF + 真 slots-simulator + 恢复入口。
### 不属于任何 G3 片：不进 scripts/run-journey-g3.ps1，也不进 scripts/g3-slice-evidence.ps1。
#
# 到站期限 30 秒：批次 5（control-server#79）起，同一个值也是「车到站后多久没人录入子批号，服务端就结束
# 本站、取消需求」。真装置上「服务端采信到站 → UIA 录入并提交」实测 4.7–5.4 秒
# （evidence/l2/20260913-b2close-real-onboard-normal-load-003、20260914-real-onboard-restart-with-open-recovery-session-004），
# 装置默认的 5 秒正卡在这条线上。30 秒给足余量，也远小于本场景装货提交之后那几条判据的预算。
@{
    Onboard                     = 'Real'
    RecoveryResume              = $true
    StationDepartureWaitTimeout = '00:00:30'
}
