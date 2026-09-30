# 批次9-06（control-server#404）：名册被置空时退化到人工充电等待，重新启用后要先经「充电后返回服务」（REQ-0171，规格 8.6；
# 用户 2026-09-29 定的并行期隔离方式）。
#
# 两台车：主车 BROKERX-L2-0001（A）与 BROKERX-L2-0002（B），各一个合成对端。两个充电桩站：211「充电点1」在假地图的节点 6（从关卡所在的
# 节点 5 走一条边就到），213「充电点2」在节点 4（沿单向环绕一圈才到）。开窗时名册先只登记 211；重新启用时登记两个，这样 B 在返回服务之后
# 有一个空着的桩可以取得——A 的那一个一直被它自己的周期预占着（充满、离桩与释放是批次9-07 的）。
#
# 两个桩不能放在同一个节点上：服务端的路网每个节点只认一个站，同节点的另一个站会被读成不可达（L2Chargers.psm1 会拒绝这种写法）。
#
# 路网引擎要开着：没有它，充电分配算不出哪一个桩可达。空闲返回不打开。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    Chargers = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 },
        @{ StationId = 213; StationName = '充电点2'; Node = 4 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
