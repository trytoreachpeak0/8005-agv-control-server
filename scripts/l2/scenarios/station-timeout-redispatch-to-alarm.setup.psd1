# control-server#703：站点超时放回待派、放回到上限转人工搬运告警。
#
# 服务端用默认上限（StationTimeoutRedispatchLimit = 2），这里不设：这条场景要证的正是出厂值下的端到端。
# 离站期限十秒：同一条需求要走满三趟，每趟都要等满一个期限，出厂五分钟跑不动。
#
# control-server#709 起它也是机台取货的回归：待送点规则打开（服务端出厂是关的），这条 WIRE_TO_GATE 需求在机台取货，
# 期限、放回、上限告警必须与规则关着时逐条相同。待送点那一半是 staging-pickup-no-station-timeout。
@{
    StationDepartureWaitTimeout  = '00:00:10'
    StagingDepartureRulesEnabled = $true
}
