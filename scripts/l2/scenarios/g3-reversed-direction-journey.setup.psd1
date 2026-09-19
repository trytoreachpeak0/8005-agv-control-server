# G3 FP-IS-11（批次 6，control-server#164）：STAGING_TO_WIRE 反向旅程。真车载端 WPF + 真 slots-simulator。
#
# 站点表整张替换（Stations 是整张替换，不是合并）：默认三站原样留着，另加 230「派工待送」作为派工待送取货站。
# 站名刻意不是 AREA 格式——control-server#159 的启动校验拒绝把 AREA 命名的站点绑成固定站，编排器的默认分区归属表
# 也只收 AREA 格式的站名，所以它不会被当成机台。
#
# TaskTypeStations 整份替换编排器默认的出厂预置配置（control-server#159）：本图需求集是 WIRE_TO_GATE 与
# STAGING_TO_WIRE，WIRE_TO_GATE 仍绑 210「关卡」，STAGING_TO_WIRE 绑 230。规则不写，取出厂六类（STAGING_TO_WIRE 的
# 固定端是起点）。写法与 control-server#163 的合成场景 staging-to-wire-reversed-journey 一致。
@{
    Onboard          = 'Real'
    Stations         = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
        '230' = '派工待送'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'STAGING_TO_WIRE')
        Bindings          = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'STAGING_TO_WIRE'; StationRiotId = 230; StationName = '派工待送'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
}
