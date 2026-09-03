@{
    # 真车载端 WPF + 真 slots-simulator，条码走 UI Automation。
    # 两者绑在一起：没有模拟器供 Modbus，车载端八个仓位全报 UNKNOWN，departureSafe 恒为 false，
    # 服务端永远不会给出会话就绪。
    Onboard = 'Real'
}
