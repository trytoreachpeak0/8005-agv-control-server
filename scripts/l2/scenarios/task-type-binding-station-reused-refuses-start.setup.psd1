# 同一个 Station（关卡／210）同时绑给 WIRE_TO_GATE 与 STAGING_TO_WIRE：规格 8.3 批次 6 机制判据 ④，
# 启动期必须拒绝（REQ-0334、REQ-0338，control-server#159）。
#
# TaskTypeStations 整份替换编排器默认装的那份预置配置；规则不写，沿用默认的六类。
# ExpectServerStartupRefusal 让编排器改为等服务端进程退出、收日志，不起对端，再交给场景判定。
@{
    TaskTypeStations           = @{
        RequiredTaskTypes = @('WIRE_TO_GATE')
        Bindings          = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'STAGING_TO_WIRE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
    ExpectServerStartupRefusal = 'TASK_TYPE_STATION_REUSED'
}
