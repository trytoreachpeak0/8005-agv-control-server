# 一台车、一个等待点 214（假地图节点 6，从关卡所在的节点 5 走一条边就到），空闲返回打开（control-server#912）。
#
# 路网引擎要开着：没有它，空闲返回无法核验 RouteCost 可达，一个点也不承诺，场景就测不到「断线时不承诺」。
@{
    IdleReturn = $true
    WaitingPoints = @(
        @{ StationId = 214; StationName = '等待点1'; Node = 6 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
