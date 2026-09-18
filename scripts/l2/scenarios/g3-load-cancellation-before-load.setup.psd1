# G3 FP-IS-02（批次 5，control-server#87）：到站没人扫码，操作员经车载端界面取消。真车载端 WPF + 真 slots-simulator。
#
# 扫码前取消不在 recoveryResumeEnabled 之后（车载端 CanRequestLoadCancellationBeforeSublot，onboard-hmi#76），
# 所以不开恢复开关：开着的话证不出这个入口不依赖它。
#
# 离站期限拉到十分钟，远超本场景时长：批次 5（control-server#79）起同一个值也是「到站后多久没人录入就结束本站」，
# 默认值下终结可能来自期限而不是取消，两条出口走同一段收尾代码，分不清是谁关的（同 load-cancelled-before-sublot）。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:10:00'
}
