# control-server#737：一车混装 agv01 的四类——WIRE_TO_GATE、WIRE_TO_OPTICAL、WIRE_TO_NITROGEN、WIRE_TO_FULL_INSPECTION——停站时途中
# 追加，依次到四个终点卸货（program#195 决定 1、5：agv01 跑四类，一趟可以混装）。
#
# 单车：编排器不写车队名册，服务端按 JourneyRuntime.allowedWorkTypes 推出这辆车允许的类型，出厂是七类全开。七类是 agv01 四类的
# 超集，不影响「四类混装、多终点卸货」这件事；agv01 恰好四类由 L1 ParallelInstanceEffectiveConfigurationTests 钉住。
#
# 站表整张替换，六个站都放在假 RIoT 路网的节点上（理由同 same-direction-mixed-load-multi-drop：追加的延迟门按路网代价判，不在
# 路网上的站算不出代价）。前五个站与那个场景相同；406「全检区」是本票加的模拟全检站，放在空着的节点 6 上
# （FakeRiotSeed.StationNodes）。全检站还没上 RIoT 26 号图（program#195 决定 4），站号与站名只写在这里，不进生产配置。
#   11 号站（节点 1，(0,0)）          机台站 C15-13_C15-14
#   305 号站（节点 2，(0,10000)）      氮气柜，WIRE_TO_NITROGEN 的卸货站
#   12 号站（节点 3，(0,20000)）      机台站 N1-3_N1-7_N2-5
#   13 号站（节点 4，(10000,20000)）   三光，WIRE_TO_OPTICAL 的卸货站
#   210 号站（节点 5，(20000,20000)）  关卡，WIRE_TO_GATE 的卸货站
#   406 号站（节点 6，(20000,0)）      全检区（模拟），WIRE_TO_FULL_INSPECTION 的卸货站
# 有向边 1→2→3→4→5→6→1 与 2→1、3→2，代价是两节点的欧氏距离（毫米）。
#
# 途中追加上限 50000 毫米，按上面的路网手算：
#   - 甲（WIRE_TO_GATE，12 取、210 卸）装完停在 12 号站，追加乙（WIRE_TO_OPTICAL，11 取、13 卸）：12→11→13→210，增量 40000；
#   - 车在 11 号站装完乙、停着，追加丙（WIRE_TO_NITROGEN，12 取、305 卸）：11→12→305→13→210，增量 20000；
#   - 车在 12 号站装完丙、持货等单时，追加丁（WIRE_TO_FULL_INSPECTION，12 取、406 卸）：当前停靠不能并（REQ-0196），丁的取货
#     排在它后面成为同站的新停靠，代价 0；卸货接在 210 之后，210→406 是 20000。增量 20000，前面三单到终点的代价都不变。
#     这正是用户说的「一直在焊线键合区收料」：车在最后一个装货站等单时同站又来一条（EnRouteAppendPlanner 的注释）。
#   最大的是乙的 40000，所以取与原场景相同的 50000。
#
# 持货 60 秒、站点等待 10 秒，同原场景（理由见那个文件）：丁要在丙的持货期内追加进来。
@{
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7_N2-5'
        '11'  = 'C15-13_C15-14'
        '13'  = '三光'
        '305' = '氮气柜'
        '406' = '全检区'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'WIRE_TO_OPTICAL', 'WIRE_TO_NITROGEN', 'WIRE_TO_FULL_INSPECTION')
        Bindings          = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'WIRE_TO_OPTICAL'; StationRiotId = 13; StationName = '三光'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'WIRE_TO_NITROGEN'; StationRiotId = 305; StationName = '氮气柜'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'WIRE_TO_FULL_INSPECTION'; StationRiotId = 406; StationName = '全检区'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
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
    CargoHoldingTimeout         = '00:01:00'
    StationDepartureWaitTimeout = '00:00:10'
}
