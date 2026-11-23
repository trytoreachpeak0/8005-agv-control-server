# control-server#911：同一子批先后两程 STAGING_TO_WIRE（焊线、焊线2）。
#
# 站点与任务类型绑定照 staging-to-wire-reversed-journey：假 RIoT 的 25 号图加派工待送取货站 305，STAGING_TO_WIRE 绑它，
# WIRE_TO_GATE 绑关卡 210（启动校验要求需求集里的每类都有绑定）。车从关卡起步（缺省的 RiotStartStationId）。
@{
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
