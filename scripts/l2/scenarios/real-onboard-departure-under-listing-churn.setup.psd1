# control-server#573：全厂订单在变时车照样离站。离站等待 20 秒：它从装货提交起算，修复前车载端每几秒闪一次未就绪、
# 服务端每闪一次就把它清掉重计，20 秒一次都走不满；修复后等满就走。20 秒也远小于场景给离站留的 80 秒预算，
# 留出建单与出发前安全检查的往返。
@{
    # 真车载端 WPF + 真 slots-simulator：只有真车载端拉 vehicle-safety 投影，合成车载端永远报安全。
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:20'
}
