@{
    # 真车载端 WPF + 真 slots-simulator，加上两端的恢复开关。
    # 恢复要走完必须两端一起开：车载端出厂 appsettings 里 recoveryResumeEnabled 是 false，
    # 服务端也没有配恢复管理员凭据，少一边握手会停在一条看起来像协议错误的认证拒绝上。
    #
    # 这个开关仍叫 RecoveryResume 是因为车载端那个配置项就叫 recoveryResumeEnabled——它门控的是
    # 整个恢复入口，不只是 RESUME_AFTER_REPAIR 这一条向量。改名要动对端配置，等接手那边的开发再说。
    Onboard        = 'Real'
    RecoveryResume = $true
}
