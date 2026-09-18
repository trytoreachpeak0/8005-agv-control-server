#
# 到站期限 30 秒：批次 5（control-server#79）起，同一个值也是「车到站后多久没人录入子批号，服务端就结束
# 本站、取消需求」。真装置上「服务端采信到站 → UIA 录入并提交」实测 4.7–5.4 秒
# （evidence/l2/20260913-b2close-real-onboard-normal-load-003、20260914-real-onboard-restart-with-open-recovery-session-004），
# 装置默认的 5 秒正卡在这条线上。30 秒给足余量，也远小于本场景装货提交之后那几条判据的预算。
@{
    # 真车载端 WPF + 真 slots-simulator，加上两端的恢复开关。
    # 恢复要走完必须两端一起开：车载端出厂 appsettings 里 recoveryResumeEnabled 是 false，
    # 服务端也没有配恢复管理员凭据，少一边握手会停在一条看起来像协议错误的认证拒绝上。
    #
    # 这个开关仍叫 RecoveryResume 是因为车载端那个配置项就叫 recoveryResumeEnabled——它门控的是
    # 整个恢复入口，不只是 RESUME_AFTER_REPAIR 这一条向量。改名要动对端配置，等接手那边的开发再说。
    Onboard                     = 'Real'
    RecoveryResume              = $true
    StationDepartureWaitTimeout = '00:00:30'
}
