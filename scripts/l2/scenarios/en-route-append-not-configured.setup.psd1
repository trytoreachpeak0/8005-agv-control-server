# 本区没有派车参数——编排器的默认前置，所以这里「不写」 DispatchZoneParameters 那一行。
#
# 路网引擎要开着，否则追加会先撞上 ROUTE_GRAPH_NEVER_REFRESHED 而不是「本区未配置」，这条场景就
# 证不到它要证的那一条。周期与预算照 route-graph-engine。
@{
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
