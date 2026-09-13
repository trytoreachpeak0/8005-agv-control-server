### G3 FP-IS-03：出发前安全检查在答复之后因安全状态变化而过期。真车载端 WPF + 真 slots-simulator。
#
# 离站等待 15 秒：场景要在检查发出之前把去关卡的路线改成不可达，让服务端拿着安全答复却发不了车。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:15'
}
