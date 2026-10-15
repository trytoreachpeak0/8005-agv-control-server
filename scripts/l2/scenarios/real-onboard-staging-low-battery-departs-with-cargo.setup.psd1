# program#184 第一波集成准备：真装置上「待送点缺电而车上有货」：自动出发（CHARGING_REQUIRED），先送货，旅程收尾之后进充电分配
# （control-server#713 的 CP-0013 一支，用户 10-14 改定「先送货再充电」；车载端 onboard-hmi#349 显示关闭原因）。
#
# 地图与绑定照 real-onboard-staging-depart-nothing-loaded：派工待送取货站 305 绑 STAGING_TO_WIRE，待送点规则打开。
# 充电桩照 real-onboard-charging-cycle：站 211「充电点1」放在假地图节点 6，路网引擎开着，单车、不打开空闲返回；场景自己把桩登记到
# 假 RIoT、经 FieldOps 导入名册（L2Chargers.psm1）。没有桩，缺电的车只会被挂上人工充电保持，看不到「进充电分配」。
# 离站与持货期限十分钟：甲装完之后车在待送点上持货等单（cs#710），出发只能来自缺电。
@{
    Onboard                      = 'Real'
    StagingDepartureRulesEnabled = $true
    StationDepartureWaitTimeout  = '00:10:00'
    CargoHoldingTimeout          = '00:10:00'
    Chargers = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
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
