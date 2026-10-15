# control-server#725（CP-0014 第 1 项）、onboard-hmi#352：真装置上「机台取货装完、车在持货等单，操作员在车载屏上点出发」。
#
# - 本区允许途中追加（上限与路网引擎照 cargo-holding-timeout），持货等单才适用；两个区域分到两侧：N1-3（12 号站取货）→ FRONT，
#   C15-13（11 号站取货）→ REAR，第二条需求追加进来时新开一个取货停靠；
# - 离站与持货期限都给十分钟：点了出发之后一分钟内就有离站核验，与「等完期限才走」分得开；
# - 待送点规则不开（出厂值）：机台取货的出发不受这个开关控制（C11 定），场景就在开关关着时证它。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:10:00'
    CargoHoldingTimeout         = '00:10:00'
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
    }
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
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
}
