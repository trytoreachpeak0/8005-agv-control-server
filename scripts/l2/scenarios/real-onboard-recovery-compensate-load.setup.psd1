@{
    # 真车载端 WPF + 真 slots-simulator，加上两端的恢复开关。与
    # real-onboard-recovery-entry-on-unknown 同一套装置：那一条停在恢复入口前，这一条按下去。
    #
    # RecoveryResume 门控的是整个恢复入口（车载端配置项就叫 recoveryResumeEnabled），不只是
    # RESUME_AFTER_REPAIR 那一条向量。COMPENSATE_LOAD_ALL_EMPTY 还额外要求认证凭据
    # （CanUseRecoveryOperator(requireProof: true)），编排器在打开这个开关时两端一起配。
    Onboard        = 'Real'
    RecoveryResume = $true
}
