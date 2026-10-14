# 批次 5（control-server#79）：站点离站期限。
#
# 出厂值是五分钟，一趟 L2 跑不到那么久；装置默认值（原来五秒，control-server#71 起三十秒）也不合用：第二段要
# 在期限走到一半时断联、重连，还要在原期限过去之后确认本站仍在等。二十秒让两段都能在一次运行里穿过期限的两侧。
# control-server#703 起站点期限先把需求放回待派，放回次数用完才以 CANCELLED_BY_STATION_TIMEOUT 终结（并挂人工搬运告警）。
# 这条场景证的是期限的计时与终结尾巴，上限设 0，每一次期限都走终结。
@{
    StationTimeoutRedispatchLimit = 0
    StationDepartureWaitTimeout = '00:00:20'
}
