@{
    # 真车载端 WPF + 真 slots-simulator，加上两端的 RESUME_AFTER_REPAIR 开关。
    # 恢复要走完必须两端一起开：车载端出厂 appsettings 里 recoveryResumeEnabled 是 false，
    # 服务端也没有配恢复管理员凭据，少一边握手会停在一条看起来像协议错误的认证拒绝上。
    Onboard        = 'Real'
    RecoveryResume = $true
}
