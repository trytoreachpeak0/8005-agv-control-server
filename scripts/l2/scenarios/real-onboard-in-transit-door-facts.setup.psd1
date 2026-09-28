### control-server#335（REQ-0246）：真车载端正常行驶、途中重连不按住；锁反馈变 0 按住本单并急停；恢复后自动解除，单仍停着。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。
#
# 到站期限 30 秒，与 real-onboard-normal-load 相同。这个值同时是装货后离站前的等待：第一版设成 5 分钟，
# 装完货旅程停在 AwaitingStationDeparture 等满 180 秒超时，车没能进入关卡段（run 36386347310）。
# 判据都在行驶途中取，那时没有站点期限在走。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:30'

    # 断线重连那一段用 POST /control/v1/disconnect：不丢任何行，只断开，车自己重连。
    ProtocolFaultProxy          = $true
}
