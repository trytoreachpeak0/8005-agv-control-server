### control-server#325（program#86 v2）：站点期限结束一站之后，真车载端上不残留那一站；迟到的取消与扫码得到明确的拒绝。
@{
    # 真车载端 + 真 slots-simulator。车上挂着哪一站、给不给「取消装货」，只有真车载端自己的录入框与按钮说得清——合成对端
    # 没有 _currentEntryRequest 这回事。
    Onboard                     = 'Real'

    # 第二、三趟丢掉收尾的那张空清单，造出「服务端已收尾、车上还挂着」的窗口。
    ProtocolFaultProxy          = $true

    # 四十五秒：够车收下录入请求、UI Automation 读到录入框与按钮，又不至于三趟跑太久。出厂配置不开 RecoveryResume：
    # 扫码前取消只要会话 Ready 与站点操作员权限。
    StationDepartureWaitTimeout = '00:00:45'
}
