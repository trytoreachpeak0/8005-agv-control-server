# 批次7-07（control-server#212）：本区不允许途中追加时不持货。
#
# 第一趟面对「上限为 0」：写成一版参数，途中追加上限 0——0 与未配置同义，都是「不许追加」（EnRouteAppendPlanner.MaxAllowedIncrease）。
# 第二趟在服务端不停的情况下导入一份上限为空的新版本，面对「未配置」。两种写法都必须得到同一个结果，所以一条场景里各走一趟。
#
# 持货超时 40 秒、站点等待 10 秒：车要是持货了，装完到离站至少 40 秒；不持货时只有站点等待那 10 秒。两者差得足够远，
# 判据按「装完 25 秒内离站」判，不会被机器快慢拨到另一边。
#
# 路网引擎照开：关着的时候追加本来就走不通，场景就分不清「不持货」是因为参数还是因为引擎。
@{
    DispatchZoneParameters = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 0 }
    }
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    CargoHoldingTimeout         = '00:00:40'
    StationDepartureWaitTimeout = '00:00:10'
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
