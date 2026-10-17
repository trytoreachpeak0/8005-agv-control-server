# control-server#752：只做 STAGING_TO_WIRE 的车不在待送点、没有需求时，空闲返回开往待送固定起点（现场是 217），不去等待点。
#
# 装置与 staging-only-vehicle-stays-at-staging-origin 相同：两台合成车，FleetAllowedTaskTypes 让两台都只接 STAGING_TO_WIRE
# （单车部署做不到：单车的任务类型取自 appsettings 的七类数组，场景只能按下标覆盖前几位，剩下的仍在）。
#
# 两个等待点都在路网上（214 在节点 6，215 在节点 4），都对两台车开放：开局两台车停在关卡，等待点都空着、开得过去——
# 红证据要的就是「有等待点可去而去了待送点」。待送点 305 只容一台车，第二台车这一次回落到等待点。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    FleetAllowedTaskTypes = @('STAGING_TO_WIRE')
    IdleReturn = $true
    WaitingPoints = @(
        @{ StationId = 214; StationName = '等待点1'; Node = 6 },
        @{ StationId = 215; StationName = '等待点2'; Node = 4 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
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
}
