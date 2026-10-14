# control-server#712：只做 STAGING_TO_WIRE 的车，旅程在派工待送点结束后留在原地，不空闲返回；下一条待送需求它原地受理。
#
# 两台合成车，FleetAllowedTaskTypes 让两台都只接 STAGING_TO_WIRE——本票的规则看的就是车队配置里的这一项。单车部署做不到：
# 单车的任务类型取自 appsettings 的六类数组，场景只能按下标覆盖前几位，剩下的仍在。
#
# 两个等待点都在路网上（214 在节点 6，215 在节点 4），都对两台车开放：车停在待送点 305（节点 2）时，至少它自己刚离开的那个点
# 是空的、开得过去——红证据要的就是「有点可去而没去」。没有本票的规则，车在 305 收尾后下一轮就会被派回等待点。
#
# 站点离站期限拉到十分钟，远超本场景时长：第一趟在待送点的收尾必须来自扫码前取消，而不是期限到了。
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
    StationDepartureWaitTimeout = '00:10:00'
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
