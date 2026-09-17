# control-server#72：分区归属表是唯一的 AREA 执行白名单。
#
# 覆盖默认表：默认表会把默认站点 `N1-3_N1-7` 里的 N1-3、N1-7 都归进分区，而场景要证的正是 N1-7 不在表里。
# 这里只给 N1-3，外加一个 T 开头、地图上没有站点的 AREA（证明它过了白名单，挡住它的是后面的判据）。
# 入库与绑定走默认前置。
@{
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
        @{ Area = 'T5-2'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
}
