# control-server#751（REQ-0365）：站点期限第一次到点即终结、不再派车，真车载端上走得通。
# 不设 StationTimeoutRedispatchLimit：要证的正是出厂上限（0）下的这一段。不开协议故障代理：报文一条不动。
@{
    # 真车载端 + 真 slots-simulator。车上撤没撤旧的那一站、新一趟能不能扫码，只有真车载端自己的录入框说得清。
    Onboard                     = 'Real'

    # 四十五秒：够车收下录入请求、UI Automation 读到录入框，又不至于跑太久（与 real-onboard-stale-stop-after-station-timeout 同值）。
    StationDepartureWaitTimeout = '00:00:45'
}
