# control-server#186 Map 级改名检测（REQ-0341、REQ-0340）。
#
# 场景运行中经假 RIoT 的 PUT /control/v1/maps/{mapId}/name 在同一 mapId 下给地图改名，另用 PUT /control/v1/maps/list-fault
# 只让地图列表答 500。两个任务类型都绑定，看改名是否暂停该图生效绑定的全部任务类型。起看板进程读暂停卡片。
# 站点表与预置配置同 catalog-change-binding-hold。
@{
    Dashboard        = $true
    Stations         = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
        '230' = '派工待送取货'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'STAGING_TO_WIRE')
        Bindings          = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'STAGING_TO_WIRE'; StationRiotId = 230; StationName = '派工待送取货'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
}
