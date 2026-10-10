# 批次9-09（control-server#407）：一台车在桩上一直报 CHARGING、电量不涨（假 RIoT 的「不涨」注入）——观察窗口满后桩的分配与车的充电资格
# 同时暂停。
#
# 单车（主车 BROKERX-L2-0001），从关卡 210（节点 5）起步。唯一的充电桩是站 211「充电点1」，放在假地图的节点 6。路网引擎要开着。
#
# ChargingPolicy.Progress 把无进展观察缩短到稳定期 5 秒、窗口 20 秒、最小增量 3%（现场推荐值是 180 秒／600 秒／3%，跑满要十几分钟）。
# 其余取值同默认测试策略。隔离的出口配齐：FieldOperatorRoles 一个 R-11，VehicleFaultRecovery 打开 Host 的放宽类入口。
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
    ChargingPolicy = @{
        Progress = @{ StabilizationSeconds = 5; ObservationWindowSeconds = 20; MinimumIncreasePercent = 3 }
    }
    FieldOperatorRoles = @(
        @{ OperatorId = 'L2-R11'; Roles = @('R-11') }
    )
    VehicleFaultRecovery = $true
}
