# control-server#709：待送点取货停靠没有倒计时，没人扫码也不结束本站、不放回需求。
#
# 地图与绑定照 staging-to-wire-reversed-journey：派工待送取货站 305 绑 STAGING_TO_WIRE。离站期限十秒，场景等满它的三倍以上：
# 期限若还在待送点上生效，这段时间里本站早就被结束、需求早就放回了。待送点规则打开（服务端出厂是关的）。
@{
    StationDepartureWaitTimeout  = '00:00:10'
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
