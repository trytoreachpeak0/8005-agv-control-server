# 批次9-08（control-server#406）：一台车到桩充不上（RIoT 返回 407802、单停在 HANG）——桩暂停、车留在原地，人工清桩之后才放桩。
#
# 单车（主车 BROKERX-L2-0001），从关卡 210（节点 5）起步。唯一的充电桩是站 211「充电点1」，放在假地图的节点 6。路网引擎要开着：
# 没有它，充电分配一个桩也不分。
#
# FieldOperatorRoles 是服务端的 R-11／R-13 名单（control-server#406，编排器写成文件、经 FieldOperatorRoles__Path 交给服务端）：
# 合成车载端以 L2-R11 的身份发人工清桩确认。
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
}
