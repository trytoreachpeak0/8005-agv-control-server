# control-server#742 扩项（CP-0015）：出发请求清单号过期、但新旧两版没有实质差别时照样受理。
#
# 三条需求、两个取货站，乙、丁靠途中追加进来：追加上限、路网引擎、两侧归属照 stop-ended-journey-continues。乙、丁都在 C15-13，
# 车开往 12 号站时追加，并进同一个 11 号站停靠（那时它不是当前停靠）。持货等单照开：机台取货上本站装完的出发请求在持货时同样受理（CP-0014）。
#
# 站点等待用模拟器测试的缩短值 90 秒、持货 2 分钟（用户 10-11 定的测试期取值）。判据用「早于期限」，不用固定秒数。
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
    CargoHoldingTimeout         = '00:02:00'
    StationDepartureWaitTimeout = '00:01:30'
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
