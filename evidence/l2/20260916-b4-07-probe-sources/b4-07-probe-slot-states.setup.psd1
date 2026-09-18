# control-server#71 验收探针（不入库）：SlotStates 把 1 号报成 OCCUPIED、2 号报成 DISABLED。
@{
    SlotStates = @(
        @{ SlotNo = 1; physicalState = 'OCCUPIED' }
        @{ SlotNo = 2; administrativeAvailability = 'DISABLED' }
    )
}
