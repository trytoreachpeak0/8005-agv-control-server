# control-server#770：本服务端自建单在 RIoT 被取消、重建到点时车低于强制充电线、车上没货——不重建，需求放回，车被派去充电。
#
# 单车（主车 BROKERX-L2-0001）、合成车载端。重建延迟照 in-transit-order-cancelled-rebuilt 调成 8 秒，省机时。充电桩与路网照
# charging-full-cycle：站 211「充电点1」放在假地图节点 6（从关卡一条边就到），路网引擎开着——没有它，充电分配算不出桩可不可达，一个也不分。
# 把 211 登记成假 RIoT 的充电桩、经 FieldOps 导入名册，是场景自己做的（scripts/l2/L2Chargers.psm1）。编排器的默认测试策略：强制充电线 30。
@{
    OwnOrderRebuildDelay = '00:00:08'
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
