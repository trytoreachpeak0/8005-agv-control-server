$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### now $(Get-Date -Format o)"
& $R -Sql "SELECT JourneyId, Stage, WaitingBatteryPercent, WaitingBatteryObservedAt, PublishedBatteryState, ChargingPolicyVersion FROM JourneyRuntimes WHERE AgvId='老厂前线新多仓位3' AND Stage <> 'Completed'"
"### new journey stops"; & $R -Sql "SELECT js.Sequence, js.StopRole, js.StationId, js.Status, js.CreatedAt FROM JourneyStops js WHERE js.JourneyId='journey:3283fc0b-e4b0-4b73-a672-8929af4dea3d|g101' ORDER BY js.Sequence"
