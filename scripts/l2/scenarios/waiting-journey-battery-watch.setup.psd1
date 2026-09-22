# control-server#273：旅程等人期间的电量监看。
#
# 门槛缩到五秒、重复间隔十秒，否则要等十分钟；取货站离站等待也缩到五秒（最小值），省掉三十秒的装置默认。
# 多开一个看板进程：端点是判据，看板那一页用来确认卡片渲染出「需要人工挪车充电」。
@{
    Dashboard                   = $true
    StationDepartureWaitTimeout = '00:00:05'
    WaitingJourneyWarningAfter  = '00:00:05'
    WaitingJourneyWarningRepeat = '00:00:10'
}
