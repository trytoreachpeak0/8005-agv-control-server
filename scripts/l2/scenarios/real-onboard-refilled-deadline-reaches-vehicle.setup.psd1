### control-server#339：等录入时断一次链路，重连后服务端重填的离站期限要送到真车载端，车上那一站的期限与服务端一致。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。
#
# 到站期限 5 分钟：场景停在等录入，断开、重连、握手、等确认、读车载端日志库都在这段期限里跑；编排器默认的 30 秒
# 在慢的装置上可能先把本站结掉。重填与原期限的差是断开前后走过的那几秒，5 分钟不影响它可测。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:05:00'

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发，场景用 POST /control/v1/disconnect 断开一次，
    # 不丢任何行，车自己重连。
    ProtocolFaultProxy          = $true
}
