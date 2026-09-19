# 批次 6（control-server#163）：STAGING_TO_WIRE 反向旅程，推翻 I6。
#
# 假 RIoT 的 25 号图加一个派工待送取货站 305（站名不是 AREA 格式，#159 的启动校验会拒绝 AREA 命名的绑定站）；
# Stations 是整张替换，所以默认的 210、12、11 原样抄在这里。预置配置整份替换编排器默认那份：需求集含
# WIRE_TO_GATE 与 STAGING_TO_WIRE，前者绑关卡 210、后者绑派工待送站 305，siteVerificationRef 用合成值。
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
