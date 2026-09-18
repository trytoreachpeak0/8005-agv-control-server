### control-server#88（program#61 ②）：补偿清空对账之后断线重连，CLOSED 的恢复会话快照不被重放进新会话、补偿命令已结算。
### 真车载端 WPF + 真 slots-simulator + 恢复入口 + 协议故障代理。不属于任何 G3 片，不进 l2.yml（真装置不上 CI）。
#
# 到站期限用编排器默认的 30 秒：计时从到站起、到录入为止（control-server#79），补偿发生在录入之后。
@{
    Onboard            = 'Real'

    # 「补偿清空」仍在恢复开关后面（onboard-hmi#78 只把在途装货取消从开关上解下来），两端的恢复凭据由编排器一起配。
    RecoveryResume     = $true

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发。场景在补偿对账之后用
    # POST /control/v1/disconnect 断开一次，不丢任何行，车自己重连。
    ProtocolFaultProxy = $true
}
