# 真装置（批次9-07，control-server#405；cs#404 审查 S6）：服务端持有的人工充电等待 → 真车载端显示 → 管理员点「充电后返回服务」→ 解除。
#
# 不导入名册（默认没有任何版本，服务端读作空名册），所以车掉到强制充电线以下时服务端置人工充电等待（ROSTER_EMPTY）。不放充电桩、不开路网。
# 开恢复入口：「充电后返回服务」与恢复入口共用同一道「已验证管理员」的门（操作员号 + 管理员凭据），同 g3-manual-charging-return。
@{
    Onboard                     = 'Real'
    RecoveryResume              = $true
    StationDepartureWaitTimeout = '00:00:30'
}
