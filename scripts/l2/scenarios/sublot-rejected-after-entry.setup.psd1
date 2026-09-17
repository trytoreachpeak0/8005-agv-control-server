# 批次 5（control-server#82）：录入后按 BR-013 重算，不符时回 SublotRejected。
#
# 派车用 control-server#71 的默认前置（仓位模型入库、绑定、默认分区归属表），这里不退出。站点离站期限拉到
# 十分钟，远超本场景时长：本站必须靠录入成功走出去，而不是期限到了。
@{
    StationDepartureWaitTimeout = '00:10:00'
}
