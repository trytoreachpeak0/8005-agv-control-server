# 批次7-15（control-server#218），规格第 8.3 节批次 7 行的真装置 L2：混挂站点一次停靠前后两侧各一条需求。
# 卸货按第 20 节补记写（一次一扇、先前后后），不按第 8.3 节原文的「卸货时两组同开」。
#
# 站表整张替换（Stations 是整张替换）：12 号站挂两个区域号 `N1-3_N2-5`，与 mixed-side-station-two-trips 同一个站名；关卡 210
# 与 11 号站原样。归属表把 N1-3 分到前侧、N2-5 分到后侧，C15-13 也写上（前侧），三个都在同一个分区——途中追加要求分区连续。
#
# 途中追加的上限与路网引擎照 multi-stop-append-same-zone（理由见那份 setup）。
#
# 两只钟都要放宽，而且有先后约束：
# - StationDepartureWaitTimeout 60 秒：它从到站起算，是本站的录入期限——12 号站要录两条、装两条，第二次扫码必须在期限之内，
#   而装完第一条不重置它（JourneyRuntimeEngine 的站点期限只在最后一条装完时重设为「装完后的修正窗口」）。真装置一次录入加
#   一次装货约二十秒，两次留足余量。它同时是 11 号站装完后的修正窗口，车在那里多停这么久。
# - CargoHoldingTimeout 3 分钟：起算点是整趟第一笔装货落定（甲在 11 号站），不是到 12 号站。期限要盖过「11 号站 60 秒修正窗口
#   + 开到 12 号站 + 乙丙两次录入装货」，车才会在 12 号站装完之后真的进入持货等单；给短了，期限在 12 号站装货途中就到，阶段直接
#   CLOSED/CARGO_HOLDING_TIMEOUT，场景看不到「持货等单确实发生过」（票面第 2 条要的正事实）。
@{
    Onboard                     = 'Real'
    Stations                    = @{
        '210' = '关卡'
        '12'  = 'N1-3_N2-5'
        '11'  = 'C15-13'
    }
    AreaAssignments             = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'N2-5'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
    )
    DispatchZoneParameters      = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 100000 }
    }
    RouteGraph                  = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    StationDepartureWaitTimeout = '00:01:00'
    CargoHoldingTimeout         = '00:03:00'
}
