# program#184 第一波集成准备：真装置上「待送点装了一部分，客户在车载屏上点出发」（control-server#713 服务端、onboard-hmi#349 车载端）。
#
# 装置照合成场景 staging-depart-after-partial-load（cs#713），换成真车载端 WPF + 真 slots-simulator：
# - 派工待送取货站 305 绑 STAGING_TO_WIRE，待送点规则打开（服务端出厂是关的）；
# - 第二条需求要靠途中追加挂上来（车停在待送点上时），所以要本区追加上限与路网引擎；两个区域分到两侧：N1-3（卸在 12 号站）→ FRONT，
#   C15-13（卸在 11 号站）→ REAR；
# - 离站与持货期限给十分钟，待送点上都不生效（cs#709）：给大是为了让「点了出发就走」与「等完期限才走」分得开；
# - 后仓那条追加进来新开一个同站停靠，车原地走过去（control-server#646 的同站追加）；宽限默认三十秒，这里缩到五秒省时间。
@{
    Onboard                          = 'Real'
    StagingDepartureRulesEnabled     = $true
    StationDepartureWaitTimeout      = '00:10:00'
    CargoHoldingTimeout              = '00:10:00'
    SameStationAppendCorrectionGrace = '00:00:05'
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
