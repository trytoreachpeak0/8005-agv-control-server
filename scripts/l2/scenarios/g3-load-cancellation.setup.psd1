### G3 FP-IS-02：装载进行中由操作员取消，全部仓位证空。真车载端 WPF + 真 slots-simulator。
#
# 「取消装货」要从车载端界面发起，而车载端的全部恢复入口都受 recoveryResumeEnabled 管
# （CanUseRecoveryOperator），所以两端的恢复开关一起打开。
@{
    Onboard        = 'Real'
    RecoveryResume = $true
}
