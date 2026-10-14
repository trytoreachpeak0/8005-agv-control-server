# 批次7-05（control-server#210）：按业务键抑制。
#
# 第一步用站点期限做本地取消（CANCELLED_BY_STATION_TIMEOUT），因为它不需要车载端发起任何东西：合成对端不答录入请求，
# 期限到了服务端自己结束本站。十秒是为了让这一步不占满整趟运行；它只作用于到站之后的等待，默认前置与发布需求都在它之前。
# 后面 D3 那一单车载端照常自动答录入，远在十秒之内装完，不会被这个期限截断。
# control-server#703 起站点期限先把需求放回待派，放回次数用完才终结并抑制。这条场景要的是抑制，上限设 0，第一次期限就终结。
@{
    StationTimeoutRedispatchLimit = 0
    StationDepartureWaitTimeout = '00:00:10'
}
