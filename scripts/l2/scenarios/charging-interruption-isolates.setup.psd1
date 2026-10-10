# 批次9-09（control-server#407）：一台车在桩上充着电、没充满就停了（假 RIoT 的「到某个电量中断」注入）——桩的分配与车的充电资格同时暂停，
# 车进清桩中、留在原地。
#
# 单车（主车 BROKERX-L2-0001），从关卡 210（节点 5）起步。唯一的充电桩是站 211「充电点1」，放在假地图的节点 6。路网引擎要开着：
# 没有它，充电分配一个桩也不分。
#
# 隔离的出口要齐（control-server#407，照 cs#406 M1 的教训）：FieldOperatorRoles 给一个 R-11（人工清桩出口），VehicleFaultRecovery 打开
# Host 的放宽类入口（桩与车的恢复都走它）。缺一样，服务端只告警、不隔离，这个场景就测不到隔离。
@{
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
    VehicleFaultRecovery = $true
}
