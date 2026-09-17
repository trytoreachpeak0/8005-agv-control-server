# 批次 5（control-server#81）：装货中期限。
#
# 派车前置用 control-server#71 起的装置默认值，只覆盖站点期限。出厂五分钟一趟 L2 跑不完；装置默认三十秒也嫌长：
# 两段各要等一次期限走完，还要在期限之前先读一次「什么都没发生」。二十秒与 station-deadline-sublot-timeout 同理，
# 让两段都能在一次运行里穿过期限的两侧。
#
# 一台车先后两单。合成对端按请求缓存录入应答（control-server#121）之后，同一台车能正确扫第二单。
@{
    StationDepartureWaitTimeout = '00:00:20'
}
