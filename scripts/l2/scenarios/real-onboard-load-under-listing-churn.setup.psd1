# control-server#573：与 real-onboard-departure-under-listing-churn 同一套装置（同一个脚本，清单搅动从车到取货站就打开）。
# 离站等待 20 秒的理由见那份 setup。
@{
    # 真车载端 WPF + 真 slots-simulator：只有真车载端拉 vehicle-safety 投影，合成车载端永远报安全。
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:20'
}
