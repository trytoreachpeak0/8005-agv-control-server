# control-server#754（REQ-0352，派车问题 3）：仓位在扫码之后才分。
#
# 途中追加要有本区上限、要有路网引擎（与 multi-stop-append-same-zone 同一个前置，理由见那份 setup）：第二条需求是车停在
# 取货站上时同站追加进来的，另开一个停靠排在当前停靠后面。
#
# 站点期限给 60 秒：第一条需求要在这段时间里一直不扫码，让期限把它结掉；追加第二条要在期限到之前落库，派车循环一两秒一轮，
# 60 秒绰绰有余。持货超时给宽，这条场景不碰持货。N1-3 照默认指 FRONT，两条都进前侧。
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
    StationDepartureWaitTimeout = '00:01:00'
    CargoHoldingTimeout = '00:10:00'
}
