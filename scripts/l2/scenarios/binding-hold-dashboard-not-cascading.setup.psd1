# 批次6-06（control-server#162）规格 8.3 批次 6 机制判据 ⑤：看板按 Map + TASK_TYPE 暂停，只停该任务类型、不连带其它。
#
# 起看板进程：暂停经看板应用的确认页提交，判据也读看板那一页。假 RIoT 的站点表整张替换，保留默认三站，再加 230
# 「派工待送取货」给 STAGING_TO_WIRE 绑；预置配置按 control-server#159 的默认写法，另给 STAGING_TO_WIRE 一条绑定。
# 本装置不发 STAGING_TO_WIRE 需求：它在这里只是「被暂停的另一个任务类型」。
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
