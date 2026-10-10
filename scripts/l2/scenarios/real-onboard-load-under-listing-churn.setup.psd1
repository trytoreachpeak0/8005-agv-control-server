# control-server#573：与 real-onboard-departure-under-listing-churn 同一个脚本，清单搅动从车到取货站就打开、每 3 次读搅第 3 次
# （装货只有十几秒，每 40 次打不中）。离站等待 20 秒、场景给离站留 80 秒：这个强度下修复后靠当场重读即可，不需要长等待来取红
# （修复前的装货红以 run 38067577157 为证，本场景只跑修复后）。
@{
    # 真车载端 WPF + 真 slots-simulator：只有真车载端拉 vehicle-safety 投影，合成车载端永远报安全。
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:20'
}
