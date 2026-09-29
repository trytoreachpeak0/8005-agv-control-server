# 批次8-20（control-server#391，REQ-0204 修订）：固定公共站点单车位，两台合成车。
#
# Fleet 列的是主车之外的车，所以这是两台车；两台都停在关卡 210 起步（编排器的默认）。FleetAllowedTaskTypes 让两台都能接
# STAGING_TO_WIRE——本票的判据真正挡住新任务的地方就是它的取货端（派工待送站 305）；不写时每台只接 WIRE_TO_GATE。
#
# 站点表与预置配置照 staging-to-wire-reversed-journey：WIRE_TO_GATE 绑关卡 210，STAGING_TO_WIRE 绑派工待送站 305。
# 12 是 AREA N1-3／N1-7 的机台，11 是 C15-13 的机台。等待点（每车一个，214 起）由编排器补进站点表。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    FleetAllowedTaskTypes = @('WIRE_TO_GATE', 'STAGING_TO_WIRE')
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
