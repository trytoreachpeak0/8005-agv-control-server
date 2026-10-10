# 途中追加要有本区的上限才可能发生（REQ-0198）：没有这一行，服务端对在途车一律拒绝追加，
# 而那正是编排器的默认前置。上限给得很宽（100 米），因为这条场景要证的是「追加这条链路接通了」，
# 不是门禁的数值判定——那一半在 Batch7EnRouteAppendPlannerTests 里手算验过。
#
# 防饥饿阈值留空：它是批次7-09 的事，这条场景不碰。
#
# 分区名在这里只能写死，因为 setup 是一份静态数据文件读不到服务端配置；它必须与
# src/ControlServer.Host/appsettings.json 的 JourneyRuntime.dispatchZone 一致，
# 否则参数落在一个没有需求的分区上，追加会以「本区未配置」被拒而场景看起来像功能坏了。
# 路网引擎必须开着：途中追加的门禁按「计划路径代价的增量」判（REQ-0198），而代价只有引擎给得出。
# 引擎默认关（既有场景面对的仍是它出现之前的那台服务端），关着的时候每一次追加都以
# ROUTE_GRAPH_NEVER_REFRESHED 被拒——fail closed 是对的，延迟门禁保护的是既有需求的交付时间，
# 算不出就不能放行。**这意味着现场不开路网引擎，途中追加就完全不工作**，PR 与出口报告如实写。
# 周期与预算照 route-graph-engine 那条：合成 RIoT 在回环上，10 秒一次不会漏，45 秒容得下一次抖动。
@{
    DispatchZoneParameters = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 100000 }
    }
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
