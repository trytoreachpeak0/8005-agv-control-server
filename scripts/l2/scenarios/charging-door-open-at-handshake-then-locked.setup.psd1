# control-server#865：会话在 1 号仓门开着时建立，随后门锁上，车要能去充电。
#
# 单车（主车 BROKERX-L2-0001），从关卡 210（节点 5）起步。充电桩是站 211「充电点1」，放在假地图的节点 6，与 charging-full-cycle 同一个摆法；
# 路网引擎要开着，没有它充电分配一个桩也不分。
#
# SlotStates 让合成车载端握手那条 SafetyStateSnapshot 报 1 号仓 UNLOCKED（10-23 agv02 第 50 代会话 14:38:12 握手时的样子）。之后的锁上由场景经
# PUT /control/v1/safety 的 slotLocks 报：那一条 SafetyStateChanged 不带逐仓状态，只有服务端要来的快照带。
@{
    Chargers = @(
        @{ StationId = 211; StationName = '充电点1'; Node = 6 }
    )
    RouteGraph = @{
        Enabled              = $true
        DesignStateTtl       = '00:10:00'
        RuntimeRefreshPeriod = '00:00:10'
        RuntimeStateMaxAge   = '00:00:45'
    }
    SlotStates = @(
        @{ SlotNo = 1; lockState = 'UNLOCKED' }
    )
}
