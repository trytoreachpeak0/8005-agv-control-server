# 批次 4（control-server#74），规格 8.3 批次 4 场景③a、8.5：超大需求的结构性派车阻断，现场取不到，由 L2 构造。
#
# 只给 N1-3 一行、指 FRONT：需求要的花篮数由场景按库内车型 FRONT 组的物理仓位数算（多一个），不在这里写死。
# 入库已批准八仓事实与绑车走默认前置。
@{
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
    )
}
