# control-server#710：待送点一侧满、等着的需求都要这一侧，即走（program#184 R1）。
#
# 地图与绑定照 staging-pickup-no-station-timeout：派工待送取货站 305 绑 STAGING_TO_WIRE，待送点规则打开（服务端出厂是关的）。
# 「等着的需求装不下」要派车轮把候选对在途车判一遍，所以要途中追加的本区上限与路网引擎（与 cargo-holding-side-full 同一个前置）：
# 上限为空或为零时持货等单不适用，候选在途中追加门就以 EN_ROUTE_APPEND_NOT_CONFIGURED 被拒，走不到仓位判据。
#
# 两个区域分到两侧：N1-3（卸在 12 号站）→ FRONT，C15-13（卸在 11 号站）→ REAR。305、12、11 都在假 RIoT 的路网节点上。
# 离站期限与持货期限都给十分钟：待送点上两样都不生效（control-server#709），给大是为了让「立即离站」与「等完期限才走」分得开。
@{
    StagingDepartureRulesEnabled = $true
    StationDepartureWaitTimeout  = '00:10:00'
    CargoHoldingTimeout          = '00:10:00'
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
