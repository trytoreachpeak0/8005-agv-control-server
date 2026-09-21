# 批次7-07（control-server#212）：VEHICLE_FULL 在离开最后一个装货停靠之前仍接追加。
#
# 追加上限、路网引擎、两侧归属照 cargo-holding-side-full。站点等待 60 秒：车在第二个取货站装完之后要在站上停够这么久，
# 场景在这段时间里先让车判满、再追加一条放得下的单；30 秒的默认值在一台慢机器上不够两轮派车加一次追加。
# 持货超时十分钟，不让它在场景里到期。
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
    CargoHoldingTimeout         = '00:10:00'
    StationDepartureWaitTimeout = '00:01:00'
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
