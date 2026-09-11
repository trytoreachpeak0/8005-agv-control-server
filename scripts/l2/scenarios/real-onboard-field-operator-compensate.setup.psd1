@{
    # 真车载端 + 真 slots-simulator，打开车载端自己的 HTTP 自动化面：驱动脚本在现场说话的就是它。
    # UI Automation 在这条场景里一次都不用——用了就证不到上车的那份代码。
    Onboard           = 'Real'
    OnboardAutomation = $true

    # 补偿清空要恢复入口开着、两端凭据配齐（CanUseRecoveryOperator(requireProof: true)）。
    # 现场由 14-set-recovery-window.ps1 表达，这里由编排器两端一起配。
    RecoveryResume    = $true
}
