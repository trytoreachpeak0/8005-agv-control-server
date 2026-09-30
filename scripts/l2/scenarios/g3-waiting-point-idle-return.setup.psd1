# G3 FP-IS-12（批次8-19，control-server#390）：空闲返回。真车载端 WPF + 真 slots-simulator。
#
# 等待点 214 放在假地图的节点 6：从关卡（节点 5，车起步的地方）一条边就到，从 214 回机台 12（节点 3）经 6 → 1 → 2 → 3 也走得通。
# 路网引擎要开着：没有它空闲返回无法核验 RouteCost 可达，一个点也不承诺。单车，不登记别的等待点。
#
# 站点等待 30 秒写明（与真装置默认值相同）：最后一段在 12 号站装一次，确认空闲返回之后录入照常开放。
@{
    Onboard                     = 'Real'
    IdleReturn                  = $true
    WaitingPoints               = @(
        @{ StationId = 214; StationName = '等待点1'; Node = 6 }
    )
    RouteGraph                  = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    StationDepartureWaitTimeout = '00:00:30'
}
