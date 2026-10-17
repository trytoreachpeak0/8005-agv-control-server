# control-server#751（REQ-0365）：站点期限第一次到点即终结、抑制并告警人工搬运，不放回。
#
# 服务端用出厂上限（StationTimeoutRedispatchLimit = 0），这里不设：这条场景要证的正是出厂值下的端到端。
# 离站期限十秒：出厂五分钟太久。
#
# control-server#709 起它也是机台取货的回归：待送点规则打开（服务端出厂是关的），这条 WIRE_TO_GATE 需求在机台取货，
# 期限、终结与告警必须与规则关着时逐条相同。待送点那一半是 staging-pickup-no-station-timeout。
@{
    StationDepartureWaitTimeout  = '00:00:10'
    StagingDepartureRulesEnabled = $true
}
