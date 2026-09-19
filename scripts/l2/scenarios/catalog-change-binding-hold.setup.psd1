# 批次6-06（control-server#162）目录变化按稳定身份分类、影响只收敛到受影响的任务类型（REQ-0341、REQ-0342、REQ-0345）。
#
# 场景运行中经假 RIoT 的 PUT /control/v1/maps/{mapId}/stations 整张替换站点表：先把 230 改名，再把 210 删掉。
# 起看板进程读两条暂停的来源。站点表与预置配置同 binding-hold-dashboard-not-cascading。
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
