# 本区配了途中追加上限，但只有一毫米：配置存在，所以第一道门（本区未配置）过得去，拒绝只能来自
# 延迟门禁本身。这是这条场景与 en-route-append-not-configured 唯一的差别——两条一起看，才分得清
# 「本区不许追加」和「这一次追加太贵」是两件事、两个原因码。
#
# 一毫米不是随手写的：上限配成 0 与没配置是同一件事（REQ-0198 里「未配置」的意思是本区禁止追加），
# 所以要试「配了但很紧」，最小的合法值就是 1。
#
# 路网引擎要开着：门禁按计划路径代价的增量判，而代价只有引擎给得出。
@{
    DispatchZoneParameters = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 1 }
    }
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
