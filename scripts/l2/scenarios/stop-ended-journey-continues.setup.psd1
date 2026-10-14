# control-server#324（program#86 v2 的 B 形态）：一站被期限结束而旅程继续时，车上不残留那一站。
#
# 追加上限、路网引擎、两侧归属照 cargo-holding-timeout（两条需求要在两个取货站，第二条靠途中追加进来）。
# 站点等待 20 秒：车到第二个取货站后，场景在这段时间里确认录入请求已在车上挂着，然后等它到期。
# 持货超时 75 秒：本站结束后车带着第一站的货持货等单，这一段正是车上残留最久的时候；到期后车离站去关卡，
# 场景才能断「下一站的清单号在空清单之上」。
# 原来是 20 秒。control-server#290 起持货期限一到就截断没开始装的需求，而这条场景要的是站点期限结束第二站：
# CI 37953826544 里甲落定后 20 秒车才到第二站（恰在旧期限上）、42 秒站点期限结束乙。持货期限必须晚于后者，75 秒留出 30 秒余量，
# 否则乙在录入请求发出之前就被持货期限截掉，L2-SEJ-02 之后的判据全都无从谈起。
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
    CargoHoldingTimeout         = '00:01:15'
    StationDepartureWaitTimeout = '00:00:20'
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
