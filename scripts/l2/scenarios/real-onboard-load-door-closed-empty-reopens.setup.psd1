# 批次5-30（control-server#86）：ADR-cross-0058 决策 1（2026-09-18 按 program#55 改写），装货时空关仓门。
#
# 站点期限压到 40 秒（出厂 5 分钟）。从到站起算，期限之前要走完录入（实测 4.7–5.4 秒）、开锁、第一次空关与车载端
# 重开，场景在期限前读一次「重开发生在期限之前」；期限之后再空关两次、按取消。40 秒让期限前那一轮不必和录入抢时间。
#
# 车载端用出厂配置：不写 RecoveryResume，wireToGate.recoveryResumeEnabled 保持出厂的 false、服务端不配恢复凭据。
# 在途装货的「取消装货」入口自 onboard-hmi#78 起与这个开关解绑，是站点操作员的普通一步；本条要证的正是出厂配置下
# 操作员能按取消收口。
@{
    Onboard                     = 'Real'
    StationDepartureWaitTimeout = '00:00:40'
}
