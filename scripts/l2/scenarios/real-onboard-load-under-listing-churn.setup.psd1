# control-server#573：与 real-onboard-departure-under-listing-churn 同一个脚本，清单搅动从车到取货站就打开、每 3 次读搅第 3 次
# （装货只有十几秒，每 40 次打不中），操作员收到 WAITING_OPERATOR 后停 10 秒再放货。离站等待 60 秒：它从到站起算、同时是本站的
# 站点期限，装货中途不重置；录入约 5 秒加停的 10 秒，装货要到第 16～18 秒才落定，20 秒太近。场景给离站留 120 秒。
# 修复前的装货红以 run 38067577157 为证，本场景只跑修复后。
@{
    # 真车载端 WPF + 真 slots-simulator：只有真车载端拉 vehicle-safety 投影，合成车载端永远报安全。
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:01:00'
}
