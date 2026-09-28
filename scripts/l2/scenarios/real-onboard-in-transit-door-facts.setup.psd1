### control-server#335 开工查证：车在路上时，服务端手里有没有新鲜的门锁事实；断线重连后多久恢复。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。
#
# 到站期限 30 秒，与 real-onboard-normal-load 相同。这个值同时是装货后离站前的等待：第一版设成 5 分钟，
# 装完货旅程停在 AwaitingStationDeparture 等满 180 秒超时，车根本没出关卡段（run 36386347310）。
# 几段探针都在行驶途中做，那时没有站点期限在走；到关卡后卸货与 normal-load 一样在 30 秒内完成。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:30'

    # 断线重连那一段用 POST /control/v1/disconnect：不丢任何行，只断开，车自己重连。
    ProtocolFaultProxy          = $true
}
