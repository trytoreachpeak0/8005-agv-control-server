$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### plans"; & $R -Sql "SELECT CreatedAt, AcknowledgedAt, FencedAt, PayloadJson FROM ProtocolOutbox WHERE MessageType = 'UpcomingStopPlanSnapshot' AND PayloadJson LIKE '%fd092be5%' ORDER BY CreatedAt"
