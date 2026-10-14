# control-server#703 审查 M3：站点超时放回、重新派回同一车同一站，真车载端上走得通。
# 不设 StationTimeoutRedispatchLimit：要证的正是生产的默认上限（2）下的这一串。不开协议故障代理：报文一条不动。
@{
    # 真车载端 + 真 slots-simulator。车上撤没撤旧的那一站、新一趟能不能扫码，只有真车载端自己的录入框说得清。
    Onboard                     = 'Real'

    # 四十五秒：够车收下录入请求、UI Automation 读到录入框，又不至于跑太久（与 real-onboard-stale-stop-after-station-timeout 同值）。
    StationDepartureWaitTimeout = '00:00:45'
}
