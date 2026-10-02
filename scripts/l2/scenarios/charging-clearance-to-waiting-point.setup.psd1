# 批次9-11（control-server#409）：清桩中的车自己开往等待点，到点即完成清桩并放桩；与空闲返回共用一个等待点集合，同一轮争最后一个点只有一个成功。
#
# 两台车：主车 A（BROKERX-L2-0001）低电去充、充不上；B（BROKERX-L2-0002）电量 80，空闲返回。唯一的充电桩是站 211，放在假地图节点 6。
# 等待点 214 放在节点 1（从 211 一条边 6→1 就到），215 不在路网上（不可达）。两个点都登记，所以启动校验的二分图匹配成立。
#
# 214 一开始只对 B 开放（VehicleScope）：开场时两辆车都空闲，没有它 A 会抢在低电之前先空闲返回到 214。场景在 A 进清桩中之后
# 经正式的 import-waiting-points 把 214 改成对全部车辆开放。
#
# ClearanceToWaitingPointEnabled 打开（默认关）：编排器把它透传成 JourneyRuntime__ClearanceToWaitingPointEnabled。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    IdleReturn = $true
    ClearanceToWaitingPointEnabled = $true
    WaitingPoints = @(
        @{ StationId = 214; StationName = '等待点1'; Node = 1; VehicleScope = @('BROKERX-L2-0002') },
        @{ StationId = 215; StationName = '等待点2' }
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
}
