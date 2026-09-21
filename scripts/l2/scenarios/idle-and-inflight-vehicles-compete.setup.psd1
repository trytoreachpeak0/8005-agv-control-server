# 两台车：一台会在场景里变成在途，另一台一直空着。`Fleet` 列的是主车之外的车（编排器把主对放在第一位）。
# 车载端不用再列一遍：OnboardPeers 缺席时编排器按 Fleet 逐车派生。
#
# 分区参数给一个很宽的上限，追加才可能发生；路网引擎要开着，否则代价算不出来，在途车那一路会以
# ROUTE_GRAPH_NEVER_REFRESHED 整个缺席，而这条场景要问的正是「它在不在竞争池里」。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
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
