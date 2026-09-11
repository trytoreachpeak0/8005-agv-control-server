@{
    # 真车载端 WPF + 真 slots-simulator，加上两端的恢复开关。与
    # real-onboard-recovery-compensate-load 同一套装置：那一条的 UNKNOWN 由锁反馈失效产出，
    # 这一条的起点是客户端在「开了锁、等操作员」时退出再拉起。
    #
    # RecoveryResume 门控的是整个恢复入口，补偿清空要它，也要两端的认证凭据，编排器一起配。
    Onboard        = 'Real'
    RecoveryResume = $true
}
