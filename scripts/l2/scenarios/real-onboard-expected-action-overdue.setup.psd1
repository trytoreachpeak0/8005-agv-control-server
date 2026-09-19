### control-server#167（REQ-0358，CP-0005 实现票 1、2 的真装置联调）：期待动作超时上报到看板卡片。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理 + 看板进程。不属于任何 G3 片，不进 l2.yml（真装置不上 CI）。
#
# 期待动作超时门槛压到 20 秒（出厂车上 3 × operationTimeoutMs = 6 分钟、服务端 00:06:00），一个键同时设两端
# （L2ExpectedActionOverdue.psm1）。它是投运标定的现场参数，只决定何时上报、不改执行器时序，所以不违反 README 第 11 条；
# operationTimeoutMs 不动，recoveryResumeEnabled 保持出厂的 false。
#
# 站点期限 60 秒，从到站起算（录入约 5 秒、开锁约 1 秒，第一次开锁约在到站后 6 秒）。约束：门槛越过（第一次开锁 + 20 秒）
# 在期限之前；从第一次开锁到放货收尾不超过 operationTimeoutMs（120 秒），免得车载端的提示节拍混进判据。
#
# 走协议故障代理，因为服务端向车要中途快照的 SafetyStateSnapshotRequested 只在线上（它追加在应答之后，不进服务端库），
# 只有代理的流量日志看得到；场景还用代理断一次链路，看重连之后卡片还对不对。
@{
    Onboard                        = 'Real'
    ProtocolFaultProxy             = $true
    Dashboard                      = $true
    ExpectedActionOverdueThreshold = '00:00:20'
    StationDepartureWaitTimeout    = '00:01:00'
}
