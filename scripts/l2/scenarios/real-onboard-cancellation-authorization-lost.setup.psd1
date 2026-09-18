### control-server#88（program#61 ③）：出厂配置下在途装货按取消，授权应答丢了，再按一次沿用首发内容，取消完成。
### 真车载端 WPF + 真 slots-simulator + 协议故障代理。不属于任何 G3 片，不进 l2.yml（真装置不上 CI）。
@{
    # 出厂配置：不开 RecoveryResume。在途装货的取消入口从 onboard-hmi#78 起走站点操作员权限，与恢复开关解绑，
    # 出厂就得有——program#55 定了「放弃装货的唯一出口是操作员按取消」。开着开关跑就证不到这一点。
    Onboard                     = 'Real'

    # 车载端的 wireToGate 连接经 tools/ControlServer.ProtocolFaultProxy 转发。场景在运行时布下
    # 「丢一条 LoadCancellationAuthorization、链路不断」。
    ProtocolFaultProxy          = $true

    # 两分钟而不是装置默认的 30 秒。本场景在装货进行中按两次取消，中间隔着车载端 3 秒的 messageTimeoutMs 与一次关弹框，
    # 到站后 30 秒会碰上装货中的站点期限（control-server#81：门开着就挂 STATION_TIMEOUT_DOOR_NOT_CLOSED）。期限到了之后
    # 怎么收是 control-server#86 的场景要证的事，混进这里只会让本场景的判据多一个与取消无关的变量。
    StationDepartureWaitTimeout = '00:02:00'
}
