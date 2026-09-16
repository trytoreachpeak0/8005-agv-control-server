# control-server#71 验收探针（不入库）：SlotStates 与 Onboard = 'Real' 同给，编排器应在启动任何东西之前报错。
@{
    Onboard    = 'Real'
    SlotStates = @(@{ SlotNo = 1; physicalState = 'OCCUPIED' })
}
