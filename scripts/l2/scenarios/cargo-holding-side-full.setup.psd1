# 批次7-07（control-server#212）：两侧各以一种方式判满，车从持货等单进入 VEHICLE_FULL，离开最后一个装货停靠时关闭。
#
# 途中追加要有本区上限、要有路网引擎（与 multi-stop-append-same-zone 同一个前置，理由见那份 setup）：持货等单只在
# 「本车所在分区允许追加」时才有意义，上限一旦为空或为零服务端就不持货（那一半是 cargo-holding-disabled-when-append-forbidden）。
#
# 持货超时给得很宽（十分钟）：这条场景要证的是「满」，不是超时。超时要是先到，状态会变成 CLOSED/CARGO_HOLDING_TIMEOUT，
# 场景读起来会像「判满坏了」。超时本身在 cargo-holding-timeout 里用 40 秒证。
#
# 两个区域分到两侧：N1-3（12 号站）→ FRONT，C15-13（11 号站）→ REAR。站表用默认三站，不替换。
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
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
