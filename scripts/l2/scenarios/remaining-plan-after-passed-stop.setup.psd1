# control-server#749：车走过一站之后，计划只下发剩余停靠，途中追加照样受理。
#
# 途中追加要有本区的上限才可能发生（REQ-0198），所以写一版很宽的每区参数（1000 米）：这条场景要证的是「计划里没有走过的站」
# 与「走过一站之后追加的链路照样接通」，不是延迟门禁的数值判定。第三条需求的取货在车已走过的 12 号站，要折返，所以给得比
# multi-stop-append-same-zone 宽。9 段只数剩余、整趟上限 20 的数值判定在 L1（RemainingJourneyPlanTests）。
#
# 路网引擎必须开着：途中追加按计划路径代价判，算不出一律拒绝。周期与预算照 multi-stop-append-same-zone。
#
# 离站等待调到最短的 5 秒：车在 12 号站装完要等它到期才离站，场景要的是「离站之后」，等待本身不是要证的事。
# 持货超时照原值写明 30 分钟（control-server#609），整场不会到期。
@{
    StationDepartureWaitTimeout = '00:00:05'
    CargoHoldingTimeout = '00:30:00'
    DispatchZoneParameters = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 1000000 }
    }
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
