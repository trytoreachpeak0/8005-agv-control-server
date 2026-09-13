@{
    # 真车载端 + 真 slots-simulator：取消请求由车载端出厂代码在重连之后的那条连接上发出，合成对端没有重连。
    Onboard            = 'Real'

    # 驱动脚本（scripts/field/FieldOperator.psm1）经车载端自动化面按扫码前「取消装货」，与 real-onboard-field-window2-rehearsal 相同。
    # 恢复窗口不开：扫码前取消只要会话 Ready 与操作员编号。
    OnboardAutomation  = $true

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发。场景在车到站之前用 POST /control/v1/disconnect
    # 断开一次，不丢 ack，让握手在 stop 还是 LoadRound 0 的时候发生。
    ProtocolFaultProxy = $true
}
