# 批次7-07（control-server#212）：装不下的原因不是「本车货物占着」时，这一侧不算满。
#
# 追加上限、路网引擎照 cargo-holding-side-full（持货要适用，否则根本没有「满不满」可判）。持货超时十分钟：
# 这条场景要证的是「一直没满」，超时先到的话状态会变成 CLOSED，判据就分不清是没判满还是超时了。
#
# 起看板进程：场景后半段经看板暂停 WIRE_TO_GATE，让一条本来放得下的需求被暂停挡住。
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
    CargoHoldingTimeout = '00:10:00'
    Dashboard           = $true
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
