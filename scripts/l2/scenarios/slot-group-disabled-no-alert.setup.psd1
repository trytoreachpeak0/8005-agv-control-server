# 批次 4（control-server#74），规格 8.3 批次 4 场景③b：仓位临时禁用造成的不足不是结构性派车阻断（REQ-0352）。
#
# 握手种子把 1、2 号仓的管理可用性报成 DISABLED（物理状态仍是 EMPTY），N1-3 指 FRONT。这两个键只能写字面值；场景开头
# 先按库内车型核对 1、2 号仓正是本车 FRONT 组的，车型一变就在前置判据上红，而不是悄悄证明另一件事。
@{
    AreaAssignments = @(
        @{ Area = 'N1-3'; DispatchZone = 'MAP-25-WIRE_TO_GATE'; SlotPosition = 'FRONT' }
    )
    SlotStates = @(
        @{ SlotNo = 1; administrativeAvailability = 'DISABLED' }
        @{ SlotNo = 2; administrativeAvailability = 'DISABLED' }
    )
}
