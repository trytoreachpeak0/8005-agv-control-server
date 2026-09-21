# 批次7-08（control-server#213）：让站不打断开着的仓门与正在执行的装货。与 waiting-station-yield 同一套分区参数、
# 路网与持货超时，另加两样：
#
# - 主车后侧 5..8 号仓报成 DISABLED（握手种子）。于是主车接下前侧 4 花篮的需求甲，受理那一刻两侧就都没有空仓了——
#   VEHICLE_FULL，而且甲还没装：车停在站上、装货在执行、门开着，同时它是一辆「满了尚未离开」的等单车，别的车被承诺
#   以这个站为下一停靠时它要让站。
# - 车载端按车列出，不由 Fleet 派生：只有主车那一台带种子。
#
# 为什么不让一辆空闲等单的车开着门：那辆车的会话会离开 Ready（门开着、又没有本车的仓位操作来解释它，
# WireToGateStore.IsUnsafetyExplainedByOwnCommandAsync），服务端对它什么都不发、也不判装货阶段，让站快照要等门关上才发得出去。
# 那一格由 L1 覆盖（Batch7StationYieldTests 的断联一例）。这里要的是票面那句「让站已触发（快照已是 WAITING_STATION_YIELD），
# 离站等门」，只有门由本车在执行的装货打开时才同时成立。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    OnboardPeers = @(
        @{
            AgvId      = 'AGV-L2-001'
            SlotStates = @(
                @{ SlotNo = 5; administrativeAvailability = 'DISABLED' }
                @{ SlotNo = 6; administrativeAvailability = 'DISABLED' }
                @{ SlotNo = 7; administrativeAvailability = 'DISABLED' }
                @{ SlotNo = 8; administrativeAvailability = 'DISABLED' }
            )
        }
        @{ AgvId = 'AGV-L2-002' }
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
