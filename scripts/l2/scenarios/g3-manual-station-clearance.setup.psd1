# G3 FP-IS-13（批次9-08，control-server#406）：充不上之后的人工清桩。真车载端 WPF + 真 slots-simulator。只写场景，不登记进 G3 runner。
#
# 充电桩是站 211「充电点1」，放在假地图的节点 6（从关卡一条边就到）。路网引擎要开着。
# RecoveryResume 开着：车载端「确认清桩」入口与「充电后返回服务」同一道门（维护开关、操作员号、管理员凭据都已配置，hmi#221）。
# FieldOperatorRoles：车载端配置的操作员号 L2-OPERATOR（编排器的 CONTROL_SERVER_OPERATOR_ID）在服务端名单里是 R-11。
@{
    Onboard            = 'Real'
    RecoveryResume     = $true
    Chargers           = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 }
    )
    RouteGraph         = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    FieldOperatorRoles = @(
        @{ OperatorId = 'L2-OPERATOR'; Roles = @('R-11') }
    )
}
