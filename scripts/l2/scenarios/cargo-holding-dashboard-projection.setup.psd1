# 批次7-12（control-server#217）：持货等单与让站上看板。
#
# 布置照 waiting-station-yield（批次7-08）：两台车服务同一个分区，途中追加上限非零、路网引擎开着——持货等单只在允许追加时适用。
# 持货超时 10 分钟，远长于这条场景：看板上的期限在整条场景里都没到，剩余时间只会往下走；装货阶段若是关了，原因只能是让站。
# 站点等待 10 秒：主车装完先过站点等待。另开看板进程：端点是判据，看板那一页用来确认卡片把这一行渲染出来了。
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
    CargoHoldingTimeout         = '00:10:00'
    StationDepartureWaitTimeout = '00:00:10'
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
    Dashboard = $true
}
