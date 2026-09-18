# G3 FP-IS-02（批次 5，control-server#87）：录入后按 BR-013 重算不符，服务端 SublotRejected，车载端显示原因；
# 容量对照恢复后重扫，正常装货。真车载端 WPF + 真 slots-simulator。
#
# 离站期限拉到十分钟，远超本场景时长：本站必须靠重扫成功走出去，而不是期限到了（同 sublot-rejected-after-entry）。
# 装货提交之后服务端停在等离站期限，场景在那里结束，不跑去关卡那一段。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:10:00'
}
