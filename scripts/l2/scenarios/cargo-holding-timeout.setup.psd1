# 批次7-07（control-server#212）：持货等单超时。
#
# 途中追加上限与路网引擎照 multi-stop-append-same-zone（持货只在允许追加时适用）。持货超时 40 秒：票面定的值，
# 足够让场景看到「期限之前一直在等」，又不至于把一轮 L2 拖到几分钟。站点等待 10 秒，比持货超时短得多，
# 这样车在站上多停的那三十秒只能是持货造成的，不会被站点等待盖住。
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
    CargoHoldingTimeout         = '00:00:40'
    StationDepartureWaitTimeout = '00:00:10'
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
