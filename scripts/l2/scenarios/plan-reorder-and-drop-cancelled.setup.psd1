# 批次7-10（control-server#215）：取消一条需求之后，它留下的空停靠从计划里删掉（REQ-0197）。
#
# 用 STAGING_TO_WIRE：取货都在派工待送站 305，卸货在各自的机台站，所以每条需求有自己的卸货停靠——取消一条，
# 它的卸货停靠才会因为「没有剩余作业」被删。WIRE_TO_GATE 的卸货都并进关卡那一个停靠，删不出任何东西。
#
# 站表整份替换：默认的 210、12、11 原样抄在这里，加派工待送站 305 与第三个机台站 13（N1-5）。305 与 13 在
# 合成 RIoT 的 seed 里有路网节点（看板例外第 13 条），替换之后仍在路网上——途中追加要算路径代价，算不出就一律拒绝。
# 分区归属表由编排器按站名派生（N1-3、N1-7、C15-13、N1-5 都进默认分区）。
#
# 分区参数给很宽的上限，路网引擎开着：这条场景要证的是删停靠，不是延迟门禁的数值判定（那一半在 L1）。
@{
    Stations = @{
        '210' = '关卡'
        '12'  = 'N1-3_N1-7'
        '11'  = 'C15-13'
        '305' = '派工待送取货'
        '13'  = 'N1-5'
    }
    TaskTypeStations = @{
        RequiredTaskTypes = @('WIRE_TO_GATE', 'STAGING_TO_WIRE')
        Bindings = @(
            @{ TaskType = 'WIRE_TO_GATE'; StationRiotId = 210; StationName = '关卡'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
            @{ TaskType = 'STAGING_TO_WIRE'; StationRiotId = 305; StationName = '派工待送取货'; SiteVerificationRef = 'L2-SYNTHETIC-SITE-CHECK' }
        )
    }
    DispatchZoneParameters = @{
        'MAP-25-WIRE_TO_GATE' = @{ EnRouteAdditionMaxPathCostIncrease = 1000000 }
    }
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
}
