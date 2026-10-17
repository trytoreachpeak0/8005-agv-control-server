# control-server#742（CP-0015 方案 A）：机台取货停靠取消后本站一篮没装、旅程继续时，车不等站点离站期限就走。
#
# 两条需求在两个取货站，第二条靠途中追加进来：追加上限、路网引擎、两侧归属照 stop-ended-journey-continues。
# 乙追加进来之后场景导入一版「未配置」的分区参数，本区不许途中追加，持货等单就不适用（cargo-holding-disabled-when-append-forbidden
# 证的那一条）。不关掉它，车在 11 号站本站空了之后照旧持货等单（用户 10-17 选甲），场景就分不清车走不走是离站等待还是持货。
#
# 站点等待用模拟器测试的缩短值 90 秒、持货 2 分钟（用户 10-11 定的测试期取值）。判据不用固定秒数，
# 用「早于期限」：11 号站离站核验的发出时刻早于这一站的离站期限；12 号站（甲已装上）的离站核验不早于它那一站的期限，作对照。
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
