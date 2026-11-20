# control-server#909：多需求停靠上扫码前取消一条，去掉它的新一版清单与录入请求随取消落定那一次保存发出。
#
# 形状照 machine-departure-stale-worklist-after-cancel：三条需求、两个取货站，乙、丁途中追加并进同一个 11 号站停靠；追加上限、路网引擎、两侧归属相同。
# 站点等待用模拟器测试的缩短值 90 秒、持货 2 分钟（用户 10-11 定的测试期取值）。
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
