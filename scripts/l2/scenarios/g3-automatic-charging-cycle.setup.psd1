# G3 FP-IS-13（批次9-07，control-server#405）：自动充电周期。真车载端 WPF + 真 slots-simulator。
#
# 充电桩是站 211「充电点1」，放在假地图的节点 6：从关卡（节点 5，车起步的地方）一条边就到，从 211 去机台 12（节点 3）经 6 → 1 → 2 → 3
# 也走得通。路网引擎要开着：没有它充电分配算不出桩可不可达，一个也不分。单车；空闲返回不打开（不登记等待点），充满的车由一条需求派走。
#
# 站点等待 30 秒写明（与真装置默认值相同）：最后一段在 12 号站装一次，确认充满离桩之后录入照常开放。
@{
    Onboard                     = 'Real'
    Chargers                    = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 }
    )
    RouteGraph                  = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    StationDepartureWaitTimeout = '00:00:30'
}
