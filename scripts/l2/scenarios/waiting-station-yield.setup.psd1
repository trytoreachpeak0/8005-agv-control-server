# 批次7-08（control-server#213）：让站。
#
# 两台车服务同一个分区（Fleet 列主车之外的那一台，编排器按它再起一个合成车载端）。途中追加上限与路网引擎照
# cargo-holding-timeout：持货等单只在允许追加时适用。持货超时 10 分钟，远长于这条场景——装货阶段关了，原因只能是让站。
# 站点等待 10 秒：主车装完之后先过站点等待，才能开走；让站不替代它（票面第 6 条）。
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
}
