# 按绑定解终点：本图 WIRE_TO_GATE 绑到站点 220「关卡2」（control-server#160，规格 8.3 批次 6 机制判据 ②）。
#
# Stations 整张替换假 RIoT 的站点表：默认三个站（关卡 210、取货点 12、11 号站）原样留着，多一个 220「关卡2」。
# TaskTypeStations 整份替换编排器默认装的预置配置，规则不写、沿用默认的六类；需求集只有 WIRE_TO_GATE，绑到 220。
# 关卡 210 仍在地图上：终点若还按旧的关卡标量或回落到默认站，关卡腿就会开去 210。
@{
    Stations         = @{
        '210' = '关卡'
        '220' = '关卡2'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE')
        Bindings          = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 220; StationName = '关卡2'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
}
