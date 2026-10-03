# 批次9-06（control-server#404）：名册被置空时退化到人工充电等待，重新启用后要先经「充电后返回服务」（REQ-0171，规格 8.6；
# 用户 2026-09-29 定的并行期隔离方式）。
#
# 两台车：主车 BROKERX-L2-0001（A）与 BROKERX-L2-0002（B），各一个合成对端。两个充电桩站：211「充电点1」与 213「充电点2」都在假地图的节点 6
# （从关卡所在的节点 5 走一条边就到）。开窗时名册先只登记 211；重新启用时登记两个，这样 B 在返回服务之后
# 有一个空着的桩可以取得——A 的那一个一直被它自己的周期预占着（充满、离桩与释放是批次9-07 的）。
#
# 两个桩故意放在同一个节点上：这正是 control-server#404 撞到的布局（当时路网每个节点只认一个站，211 被判 CHARGER_UNREACHABLE），
# control-server#431 修好之后同节点的每个站都按该节点的代价可达。这个场景因此也是那次修复的端到端证明。
#
# 路网引擎要开着：没有它，充电分配算不出哪一个桩可达。空闲返回不打开。
@{
    Fleet = @(
        @{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
    )
    Chargers = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 },
        @{ StationId = 213; StationName = '充电点2'; Node = 6 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
