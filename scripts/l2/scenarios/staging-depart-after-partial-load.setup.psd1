# control-server#713：待送点装了一部分，客户在车载屏上点「出发」（program#184 R2，CP-0012）。
#
# 前置照 staging-other-side-fits-holds（cs#710）：待送点规则打开，第二条需求要靠途中追加挂上来，所以要本区追加上限与路网引擎。
# 两个区域分到两侧：N1-3（卸在 12 号站）→ FRONT，C15-13（卸在 11 号站）→ REAR。离站与持货期限给十分钟，待送点上都不生效（cs#709）：
# 给大是为了让「点了出发就走」与「等完期限才走」分得开。
@{
    StagingDepartureRulesEnabled = $true
    StationDepartureWaitTimeout  = '00:10:00'
    CargoHoldingTimeout          = '00:10:00'
    # 后仓那条追加进来新开一个同站停靠，车原地走过去（control-server#646 的同站追加）；宽限默认三十秒，这里缩到五秒省时间。
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
