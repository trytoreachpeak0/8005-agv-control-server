### control-server#230（cs#36 后续）：恢复会话已经开成、车载端也记下了会话 id 时断电，重启之后恢复入口还在不在、补偿走不走得到对账。
### 真车载端 WPF + 真 slots-simulator + 恢复入口 + 协议故障代理。不属于任何 G3 片：不进 scripts/run-journey-g3.ps1，也不进 scripts/g3-slice-evidence.ps1。
#
# 到站期限 30 秒，同 real-onboard-restart-with-open-recovery-session：批次 5（control-server#79）起，这个值也是
# 「车到站后多久没人录入子批号，服务端就结束本站、取消需求」。真装置上「服务端采信到站 → UIA 录入并提交」实测
# 4.7–5.4 秒，装置默认的 5 秒正卡在这条线上；30 秒给足余量，也远小于本场景装货提交之后那几条判据的预算。
@{
    Onboard                     = 'Real'

    # 「补偿清空」在恢复开关后面，两端的恢复凭据由编排器一起配。
    RecoveryResume              = $true

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发。场景在按「补偿清空」之前布下
    # 「丢一条 RecoveryActionAccepted、链路不断」：服务端开会话、受理动作，车载端落了盘却等不到受理，
    # 于是断电时会话开着、车也记着它（control-server#230）。
    ProtocolFaultProxy          = $true

    StationDepartureWaitTimeout = '00:00:30'
}
