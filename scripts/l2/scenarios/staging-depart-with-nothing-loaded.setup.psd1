# control-server#713：待送点一篮没装，客户在车载屏上点「出发」：旅程原地收尾、需求放回，车不动（program#184 R2，CP-0012）。
#
# 地图与绑定照 staging-pickup-no-station-timeout：派工待送取货站 305 绑 STAGING_TO_WIRE，待送点规则打开（服务端出厂是关的）。
@{
    StationDepartureWaitTimeout  = '00:10:00'
    StagingDepartureRulesEnabled = $true
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
