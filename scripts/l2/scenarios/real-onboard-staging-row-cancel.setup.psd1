# control-server#726（CP-0014 修订项二，REQ-0211；车载端 onboard-hmi#353）：真装置上「待送点清单按行取消」。
#
# 装置照 real-onboard-staging-depart-partial-load（cs#713），换一处：
# - 甲先在 305 的第一个停靠装上；丙、乙、丁三条在车还停在第一个停靠上时追加进来——第一条新开同站的第二个停靠（当前停靠不并，REQ-0196），
#   后两条并进第二个停靠（它不是当前停靠，可以并），于是第二个停靠的清单上有三行。这是 cs#714 之前生产上唯一能让待送点一站多行的形状
#   （#733 审查 L3）。三条要在车离开第一个停靠之前都挂上，所以同站追加的纠错宽限放到九十秒（partial-load 是五秒）；
# - 归属表多一个区域 N1-7（12 号站 `N1-3_N1-7` 本来就挂着它）分到前侧，乙用它；丁与丙同在 C15-13（后侧）；
# - 离站与持货期限给十分钟：持货期限（cs#727）自甲装上起算、每次追加重新起算，十分钟内不会到期，场景里的「出发」是操作员按的。
@{
    Onboard                          = 'Real'
    StagingDepartureRulesEnabled     = $true
    StationDepartureWaitTimeout      = '00:10:00'
    CargoHoldingTimeout              = '00:10:00'
    SameStationAppendCorrectionGrace = '00:01:30'
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
        '305' = '派工待送取货'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'STAGING_TO_WIRE')
        Bindings = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'STAGING_TO_WIRE'; StationRiotId = 305; StationName = '派工待送取货'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'N1-7'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
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
