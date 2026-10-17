# cs#712：RIoT 对「目的站 = 车当前所在站」订单的处理（10-17，只读取证，未动车）

来源：库 C OrderIntents（agv02 DestinationStationId=217，198 条，d4/40）；车位读数 d2/34-trip-watch.txt（30 s 一次 RIoT 读数）对照建单时刻（d4/41，建单前最近一次读数在 60 s 内且 agv02@217 的共 176 条）；RIoT GET /api/order/v1/orderRecord/detailByOrderId 原始响应（d4/42，抽 5 张，跨 10-15 10:41 至 10-16 20:28）。

结论：立即 SUCCESS。5 张抽样全部 orderState=5、failReason 空、taskState=1，startStationNo=endStationNo=217；createTime→doneTime 耗时 1～5 s（10:41:14→10:41:15、10:46:24→10:46:28、15:23:15→15:23:18、14:44:17→14:44:19、20:28:39→20:28:44）。建单前后车位读数都是 agv02@217 IDLE/MT_FINISHED，车未移动，无故障、无急停。v2 侧 176 条 OrderIntents 全部 Status=CONFIRMED、LastCreateOutcome=CreateResponseAccepted。B 段实测不需要做。
