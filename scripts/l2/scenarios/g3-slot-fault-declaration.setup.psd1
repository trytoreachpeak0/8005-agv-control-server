### G3 FP-IS-07：人工判故障（REQ-0359，control-server#383）的两条向量。真车载端 WPF + 真 slots-simulator + 协议故障代理。
### 车载端要含 onboard-hmi#215，否则它不认识 SlotFaultDeclarationCommand。
#
# 期待动作超时门槛 20 秒，照 real-onboard-expected-action-overdue：投运标定的现场参数，只决定何时上报、不改执行器时序。
# 站点期限 120 秒：装货站要等门槛、判定、放货关门、断开重连与补发，卸货站要等门槛与判定，各约 40 秒，留足余量。
# 走协议故障代理：NOT_APPLICABLE 那一半靠它吞掉判定命令一次（drop-message），再断开一次让服务端补发。
@{
    Onboard                        = 'Real'
    ProtocolFaultProxy             = $true
    SlotFaultDeclaration           = $true
    ExpectedActionOverdueThreshold = '00:00:20'
    StationDepartureWaitTimeout    = '00:02:00'
}
