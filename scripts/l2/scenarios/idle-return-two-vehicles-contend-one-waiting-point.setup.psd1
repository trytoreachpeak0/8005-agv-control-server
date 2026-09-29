# 两台空闲车、两个等待点，但实际只有一个可用：空闲返回只让其中一辆承诺（批次8-18，control-server#389；REQ-0291、REQ-0292）。
#
# 两个点都登记、都启用、白名单都为空——启动校验按二分图匹配要求两辆车各能分到一个点，这一条满足。让两辆车都用不了 215
# 靠的是路网：214 放在假地图的节点 6（从关卡所在的节点 5 走一条边就到），215 不放在任何节点上，空闲返回判它不可达。
# 票面建议用白名单造「实际只有一个可用点」，那做不到：任何让两辆车都用不了 215 的白名单都会让服务端拒绝启动。
#
# 路网引擎要开着：没有它，空闲返回无法核验 RouteCost 可达，一个点也不承诺。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    IdleReturn = $true
    WaitingPoints = @(
        @{ StationId = 214; StationName = '等待点1'; Node = 6 },
        @{ StationId = 215; StationName = '等待点2' }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
