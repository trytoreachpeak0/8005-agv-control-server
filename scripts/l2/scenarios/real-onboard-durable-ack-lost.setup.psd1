@{
    # 真车载端 WPF + 真 slots-simulator。补发走的是车载端 journal 与 WireToGateSessionClient 的真代码，
    # 合成对端没有 journal，也不补发。
    Onboard            = 'Real'

    # 车载端的 wireToGate 连接改经 tools/ControlServer.ProtocolFaultProxy 转发。代理默认什么都不丢，
    # 场景在运行时布下「丢一次 OperationResult 的 DurableAck」。
    ProtocolFaultProxy = $true
}
