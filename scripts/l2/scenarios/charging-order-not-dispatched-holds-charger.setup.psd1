# control-server#863：一台车的充电单在 RIoT 一直排队、不派给车，占着唯一的桩；另一台低电车等这个桩。
#
# 两台车（主车 BROKERX-L2-0001、BROKERX-L2-0002）从关卡 210（节点 5）起步。唯一的充电桩是站 211「充电点1」，放在假地图的节点 6。
# 路网引擎要开着：没有它，充电分配一个桩也不分。
#
# ChargingOrderUndispatchedAlarmAfter 30 秒（默认 5 分钟）：编排器把它透传成 JourneyRuntime__ChargingOrderUndispatchedAlarmAfter。
# 假 RIoT 建出的单一直是 1 QUEUEING、executeVehicleKey "--"，场景不推它，正是 10-22 那张单的样子。
#
# FieldOperatorRoles 是服务端的 R-11／R-13 名单：合成车载端以 L2-R11 的身份发人工清桩确认。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    Chargers = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    FieldOperatorRoles = @(
        @{ OperatorId = 'L2-R11'; Roles = @('R-11') }
    )
    ChargingOrderUndispatchedAlarmAfter = '00:00:30'
}
