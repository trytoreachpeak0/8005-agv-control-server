# 批次 4（control-server#73），规格 8.3 批次 4 场景②：所需分组暂时空仓不足。
#
# 握手种子把 5～8 号仓报成 OCCUPIED，N1-3 指 REAR。这两个键只能写字面值；场景开头先按库内车型核对这四个仓正是本车的
# REAR 组，车型一变就在前置判据上红，而不是悄悄证明另一件事。场景中途让 REAR 组最小的两个仓变空时，占用哪些仓从库内
# 车型算。
@{
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'REAR' }
    )
    SlotStates = @(
        @{ SlotNo = 5; physicalState = 'OCCUPIED' }
        @{ SlotNo = 6; physicalState = 'OCCUPIED' }
        @{ SlotNo = 7; physicalState = 'OCCUPIED' }
        @{ SlotNo = 8; physicalState = 'OCCUPIED' }
    )
}
