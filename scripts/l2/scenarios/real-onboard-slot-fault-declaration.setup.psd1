### control-server#383（REQ-0359）：人工判故障在真装置上走完整条链。车载端这一半是 onboard-hmi#215。
### 真车载端 WPF + 真 slots-simulator。onboard_ref 要含 onboard-hmi#215：之前的车载端不认识 SlotFaultDeclarationCommand，
### 收到会断开会话，服务端重连后又补发，场景会在断开—重连里超时。
#
# 期待动作超时门槛压到 20 秒，照 real-onboard-expected-action-overdue：它是投运标定的现场参数，只决定何时上报、不改执行器
# 时序，所以不违反 README 第 11 条；operationTimeoutMs 不动。
#
# 站点期限 120 秒，比那个场景长：这里要在门槛之后判定、等车载端中止并出结果、再空关一次看它不重开，全程约 45 秒，
# 期限留足余量，免得 STATION_TIMEOUT_DOOR_NOT_CLOSED 混进判据。
@{
    Onboard                        = 'Real'
    SlotFaultDeclaration           = $true
    ExpectedActionOverdueThreshold = '00:00:20'
    StationDepartureWaitTimeout    = '00:02:00'
}
