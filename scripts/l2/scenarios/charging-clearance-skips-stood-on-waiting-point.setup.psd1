# control-server#798：清桩选点看点上有没有车，不只看占用记录。
#
# 两台车：主车 A（BROKERX-L2-0001）低电去充、充不上、进清桩中；B（BROKERX-L2-0002）电量 80，接一条搬运需求，在途中停在等待点 214 上
# （例如在 RIoT 的检查点前等放行）。214 上没有占用行：B 不是去那里的，它只是停在那里。
# 唯一的充电桩是站 211（假地图节点 6）；等待点 214 在节点 1（从 211 一条边 6→1 就到），215 不在路网上（不可达）。两个点都登记，
# 多车启动校验的二分图匹配成立。
#
# 空闲返回关着：这里测的是清桩选点，B 也不该在接单之前先空闲返回到 214。
# ClearanceToWaitingPointEnabled 打开（默认关）：编排器把它透传成 JourneyRuntime__ClearanceToWaitingPointEnabled。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    IdleReturn = $false
    ClearanceToWaitingPointEnabled = $true
    WaitingPoints = @(
        @{ StationId = 214; StationName = '等待点1'; Node = 1 },
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
    # Without the manual station clearance exit a vehicle that cannot charge is left on ORDER_HANG, never paused for clearing.
    FieldOperatorRoles = @(
        @{ OperatorId = 'L2-R11'; Roles = @('R-11') }
    )
}
