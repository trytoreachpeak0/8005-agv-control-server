# 批次10-02（control-server#546），规格 8.3 批次 10：WIRE_TO_OPTICAL 绑定完备、走完一趟，AREA 端按侧取仓。
#
# Stations 整张替换假 RIoT 的站点表：关卡 210、机台站 12（N1-3_N1-7）、11 号站原样留着，多一个本类的固定站 403「三光」，
# 站名不是 AREA 格式。四条同向场景各用一个站号（401～404），REQ-0334：一站不被两类绑定。
# TaskTypeStations 整份替换编排器默认的预置配置：规则沿用默认六类，本图要求 WIRE_TO_GATE 与 WIRE_TO_OPTICAL，两者都绑定。
# AreaAssignments：同挂机台站 12 的两个 AREA 分到两侧，N1-3 指 REAR、N1-7 指 FRONT——分组只能来自需求的 AREA。
@{
    Stations         = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
        '403' = '三光'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'WIRE_TO_OPTICAL')
        Bindings          = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'WIRE_TO_OPTICAL'; StationRiotId = 403; StationName = '三光'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
    AreaAssignments  = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
        @{ Area = 'N1-7'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
    )
}
