# control-server#316：真车载端在场时的在途单挂起与 continue。
#
# 真车载端 WPF + 真 slots-simulator，两者绑在一起：没有模拟器供 Modbus，车载端八个仓位全报 UNKNOWN，
# 服务端永远不会给出会话就绪，旅程连取货站都派不出去。
# 到站期限 30 秒，理由同 real-onboard-normal-load：本场景只走到「车载端允许录入」，不录入，期限要长于那一段。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:30'
}
