# 批次9-07（control-server#405）：一个充电桩、三台车的「充满让桩」——规格 8.3、8.5「1 桩 3 车争用、充满让桩」的后一半
# （「争用」一半是批次9-06 的 charging-one-charger-three-vehicles-contend）。
#
# 主车 BROKERX-L2-0001（A）之外再列两台（B、C）；车载端按 Fleet 逐车派生，一台车一个合成对端。充电桩是站 211「充电点1」，放在假地图的
# 节点 6。路网引擎要开着。空闲返回不打开：充满的 A 靠一条搬运需求离桩——B、C 都低于强制充电线，接不了搬运，所以这条需求只可能派给 A
# （合成 RIoT 的路线代价不分车，靠「谁能接」而不是「谁更近」定赢家）。三台车的等待点照 Fleet 的默认登记（214～216），不在路网上。
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
