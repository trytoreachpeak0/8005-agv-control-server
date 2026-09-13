### G3 FP-IS-02：取货点录入 sublot、两仓装载、装载修正。真车载端 WPF + 真 slots-simulator。
#
# 「修正装货」要从车载端界面发起，而车载端的全部恢复入口都受 recoveryResumeEnabled 管
# （CanUseRecoveryOperator），所以两端的恢复开关一起打开。
#
# 离站等待 20 秒：修正只能在这段时间里开始（REQ-0237），场景要证修正期间车不走、修正收敛后等满
# 这 20 秒才走。场景里的 G3-02-13 按同一个值判，改这里要一起改那里。
@{
    Onboard                     = 'Real'
    RecoveryResume              = $true
    StationDepartureWaitTimeout = '00:00:20'
}
