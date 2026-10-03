# 批次9-07（control-server#405）：一台车走完一整个自动充电周期——低电、分配、去桩、充电、充满、派走、离桩之后桩才释放。
#
# 单车（主车 BROKERX-L2-0001），从关卡 210（节点 5）起步。充电桩是站 211「充电点1」，放在假地图的节点 6（从关卡一条边就到）。
# 路网引擎要开着：没有它，充电分配算不出桩可不可达，一个也不分。
#
# Chargers 只把站放到假地图与路网上。把它登记成假 RIoT 的充电桩（带进出点 212、离桩单经过进出点 expandDeparture=true——调度 09-29
# 要求本票的 L2 至少用一次）、经 FieldOps 导入名册，是场景自己做的（scripts/l2/L2Chargers.psm1）。空闲返回不打开：单车不登记等待点，
# 充满之后车一直停在桩上，直到场景发一条需求把它派走。
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
