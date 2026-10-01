# 批次9-08（control-server#406）的负向：到桩后单独的 HANG（不带 407802）、带 407802 却不在桩上，都不暂停桩（REQ-0175）。
#
# 单车（主车 BROKERX-L2-0001），从关卡 210（节点 5）起步；唯一的充电桩是站 211「充电点1」，放在假地图的节点 6。路网引擎要开着。
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
}
