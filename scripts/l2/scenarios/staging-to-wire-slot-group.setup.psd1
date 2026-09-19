# 批次 6（control-server#163），规格 8.3 批次 6 判据⑥：送往前侧机台的 STAGING_TO_WIRE 需求在派工待送点装入前侧，后侧同理。
#
# 两个 AREA 挂在同一个机台站 12（N1-3_N1-7），归属表把 N1-3 指 REAR、N1-7 指 FRONT：分组只能来自需求的 AREA，
# 取货站（派工待送站 305）与机台站都给不出它。站点表与预置配置同 staging-to-wire-reversed-journey。
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
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
        @{ Area = 'N1-7'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
    )
}
