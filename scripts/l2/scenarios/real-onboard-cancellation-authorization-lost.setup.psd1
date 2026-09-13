@{
    # 真车载端 + 真 slots-simulator，按钮经车载端 HTTP 自动化面按。取消请求的日志与重试走的是车载端的真代码。
    Onboard            = 'Real'
    OnboardAutomation  = $true

    # 装货途中的「取消装货」只在恢复入口开着时出现。
    RecoveryResume     = $true

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发。代理默认什么都不丢，
    # 场景在运行时布下「丢一条 LoadCancellationAuthorization、链路不断」。
    ProtocolFaultProxy = $true
}
