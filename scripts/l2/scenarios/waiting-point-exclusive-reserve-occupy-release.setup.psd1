# 批次8-19（control-server#390）：空闲返回在一个等待点上的一行两状态——在途预占、在点占用、凭离点证据释放。单车。
#
# 等待点 214 放在假地图的节点 6：从关卡（节点 5）一条边就到，从 214 回机台 12（节点 3）经 6 → 1 → 2 → 3 也走得通，
# 所以车卸完货能回等待点、之后也能被派走。路网引擎要开着：没有它空闲返回无法核验 RouteCost 可达，一个点也不承诺。
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
