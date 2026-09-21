# G3 FP-IS-08（批次 7，control-server#218）：多停靠计划。真车载端 WPF + 真 slots-simulator。
#
# 途中追加要有本区上限、要有路网引擎（与 multi-stop-append-same-zone 同一个前置，理由见那份 setup）：上限为空或为零时服务端对
# 在途车一律不追加，路网引擎没刷新过时一律以 ROUTE_GRAPH_NEVER_REFRESHED 拒绝。站表用默认三站，区域归属用默认表（全部前侧）。
#
# 允许追加就会持货等单（ADR-cross-0057）：车装完最后一个装货停靠不走，等到装满、持货超时或让站。这条场景不证持货，只要它
# 走得完，所以持货超时缩到 90 秒——起算点是整趟第一笔装货落定（甲在 12 号站），要盖过「甲装完后的 30 秒修正窗口 + 开到 11 号站
# + 乙录入装货」，否则车在 11 号站装完之前期限已过，阶段关得比装货早，场景照样走得完但看不到持货。车队只有一辆车，不会让站。
#
# 站点等待 30 秒写明（与真装置默认值相同）：它同时是到站后的录入期限与装完后的修正窗口，两站各录一次，够用。
@{
    Onboard                     = 'Real'
    DispatchZoneParameters      = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 100000 }
    }
    RouteGraph                  = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    CargoHoldingTimeout         = '00:01:30'
    StationDepartureWaitTimeout = '00:00:30'
}
