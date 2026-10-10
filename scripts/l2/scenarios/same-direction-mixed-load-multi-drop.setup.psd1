# 批次10-02（control-server#546）：一车混装 WIRE_TO_GATE、WIRE_TO_OPTICAL、WIRE_TO_NITROGEN，停站时途中追加，依次到三个终点卸货
# （用户 2026-10-10 要求，经 Coordinator 10 转达）。
#
# 单车：编排器不写车队名册，服务端按 JourneyRuntime.allowedWorkTypes 推出这辆车允许的类型，出厂是六类全开
# （VehicleDispatchPolicyAccess.cs:63-75、appsettings.json:67）。六类是三类的超集，不影响「三类混装、多终点卸货」这件事。
#
# 站表整张替换，五个站都放在假 RIoT 路网的节点上：追加的延迟门按路网引擎给的计划路径代价判（EnRouteAppendCriterion.cs:72-74），
# 不在路网上的站算不出代价，追加以 EN_ROUTE_APPEND_DELAY_UNCOMPUTABLE 被拒。假 RIoT 的节点表（FakeRiotSeed.StationNodes）只给
# 11、12、13、210、305 五个站号配了节点，setup 没有给新站号配节点的键（RouteCosts 是 getRouteCostsBy 的应答，只管可达性，
# 不进路网引擎），所以本场景只能用这五个号，不能与别的场景错开：
#   11 号站（节点 1，(0,0)）      机台站 C15-13_C15-14
#   305 号站（节点 2，(0,10000)）  氮气柜，WIRE_TO_NITROGEN 的卸货站
#   12 号站（节点 3，(0,20000)）  机台站 N1-3_N1-7_N2-5
#   13 号站（节点 4，(10000,20000)）三光，WIRE_TO_OPTICAL 的卸货站
#   210 号站（节点 5，(20000,20000)）关卡，WIRE_TO_GATE 的卸货站
# 有向边 1→2→3→4→5→6→1 与 2→1、3→2，代价是两节点的欧氏距离（毫米）。
#
# 途中追加上限 50000 毫米，理由（按上面的路网手算）：
#   - 甲（WIRE_TO_GATE，12 取、210 卸）在 12 号站装完，车停着。追加乙（WIRE_TO_OPTICAL，11 取、13 卸）最省的插法是
#     12→11→13→210，计划代价由 20000 变 60000：乙的边际增量 40000，甲到自己卸货站的增量也是 40000；
#   - 车在 11 号站装完乙、停着。追加丙（WIRE_TO_NITROGEN，12 取、305 卸）最省的插法是 11→12→305→13→210，计划代价由
#     40000 变 60000：边际增量 20000，乙、甲各 +20000。
#   所以上限至少 40000；取 50000，比需要的大一档，又远小于既有场景的 100000，让「上限够容纳多终点绕路」这件事有数可查。
#   负向第二段场景里改导入 30000：介于 20000 与 40000 之间，第二单（同乙的形状，要 40000）被延迟门拒。
#
# 持货 30 秒：允许追加的分区里车在最后一个装货站装完会持货等单（ADR-cross-0057），乙、丙都在这段时间里追加进来；最后一站
# 装完没有新单，30 秒到期离站。站点等待 10 秒，照 cargo-holding-timeout。
@{
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7_N2-5'
        '11'  = 'C15-13_C15-14'
        '13'  = '三光'
        '305' = '氮气柜'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'WIRE_TO_OPTICAL', 'WIRE_TO_NITROGEN')
        Bindings          = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'WIRE_TO_OPTICAL'; StationRiotId = 13; StationName = '三光'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'WIRE_TO_NITROGEN'; StationRiotId = 305; StationName = '氮气柜'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'N1-7'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'N2-5'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'C15-13'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
        @{ Area = 'C15-14'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
    DispatchZoneParameters = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 50000 }
    }
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    CargoHoldingTimeout         = '00:00:30'
    StationDepartureWaitTimeout = '00:00:10'
}
