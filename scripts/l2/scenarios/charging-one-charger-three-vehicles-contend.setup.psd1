# 批次9-06（control-server#404）：一个充电桩、三台车（规格 8.3、8.5 的「1 桩 3 车争用」）。
#
# 主车 BROKERX-L2-0001 之外再列两台；车载端按 Fleet 逐车派生，一台车一个合成对端。充电桩是站 211「充电点1」，放在假地图的节点 6
# （从关卡所在的节点 5 走一条边就到）。路网引擎要开着：没有它，充电分配算不出哪一个桩可达、哪一个最近，一个也不分。
#
# Chargers 只把站放到假地图与路网上。把它登记成假 RIoT 的充电桩、经 FieldOps 导入名册，是场景自己在合适的时刻做的
# （scripts/l2/L2Chargers.psm1）。空闲返回不打开：三台车的等待点照 Fleet 的默认登记（214～216），但没有一台会被派去。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' },
        @{ AgvId = 'AGV-L2-003'; VehicleKey = 'BROKERX-L2-0003' }
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
}
