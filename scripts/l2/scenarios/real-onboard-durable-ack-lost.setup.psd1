### control-server#88（program#61 ①）：丢一次装货结果的 DurableAck，车重连补发，服务端按首次受理重签、同一连接走完握手。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。不属于任何 G3 片，不进 l2.yml（真装置不上 CI）。
#
# 出厂配置：不开 RecoveryResume。补发与握手不经过任何恢复入口。
# 到站期限用编排器默认的 30 秒：本场景在 AwaitingSublot 只停「服务端采信到站 → UIA 录入」那 5 秒左右。
@{
    Onboard            = 'Real'

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发。代理默认什么都不丢，场景在运行时布下
    # 「丢一次 acceptedMessageType = OperationResult 的 DurableAck」。
    ProtocolFaultProxy = $true
}
