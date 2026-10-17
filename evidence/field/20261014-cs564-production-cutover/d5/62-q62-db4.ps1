$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### now $(Get-Date -Format o)"
"### JourneyRuntimes for 多仓位3 (latest 5)"; & $R -Sql "SELECT JourneyId, Stage, BlockReasonCode, BlockReasonSince, DispatchZone, CreatedAt, UpdatedAt FROM JourneyRuntimes WHERE AgvId='老厂前线新多仓位3' ORDER BY CreatedAt DESC LIMIT 5"
"### ProtocolInbox last 3 per vehicle (by RequestJson match)"
foreach ($k in 'agv01','agv02','agv03','多仓位1','多仓位2','多仓位3') { "-- $k"; & $R -Sql "SELECT MessageType, ReceivedAt FROM ProtocolInbox WHERE RequestJson LIKE '%$k%' ORDER BY ReceivedAtUtcTicks DESC LIMIT 2" }
"### sessions API"
& 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sessions.ps1'
