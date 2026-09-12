@{
    # 真车载端 + 真 slots-simulator：重放进新会话的报文由车载端出厂代码处理，合成对端证不到。
    Onboard            = 'Real'

    # 驱动脚本（scripts/field/FieldOperator.psm1）经车载端自动化面扫码、按「补偿清空」，与 real-onboard-field-operator-compensate 相同。
    OnboardAutomation  = $true

    # 补偿清空要恢复入口开着、两端凭据配齐。
    RecoveryResume     = $true

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发。场景在补偿对账之后用 POST /control/v1/disconnect
    # 断开一次，不丢 ack。
    ProtocolFaultProxy = $true
}
