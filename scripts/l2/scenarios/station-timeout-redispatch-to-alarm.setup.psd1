# control-server#703：站点超时放回待派、放回到上限转人工搬运告警。
#
# 服务端用默认上限（StationTimeoutRedispatchLimit = 2），这里不设：这条场景要证的正是出厂值下的端到端。
# 离站期限十秒：同一条需求要走满三趟，每趟都要等满一个期限，出厂五分钟跑不动。
@{
    StationDepartureWaitTimeout = '00:00:10'
}
