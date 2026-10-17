$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### now $(Get-Date -Format o)"
"### agv03 journeys"; & $R -Sql "SELECT JourneyId, Stage, BlockReasonCode, BlockReasonSince, CreatedAt, UpdatedAt FROM JourneyRuntimes WHERE AgvId='老厂前线新多仓位3' ORDER BY CreatedAt DESC LIMIT 3"
"### held by non-completed agv03 journeys"; & $R -Sql "SELECT jd.JourneyId, ad.Sublot, jd.TargetSlotsJson, jd.Status, ad.Status AS dstatus, jd.RemovedAt, json_extract(ad.LiveMesFieldsJson,'$.Area') AS area FROM JourneyDemands jd JOIN AcceptedDemands ad ON ad.DemandId=jd.DemandId JOIN JourneyRuntimes jr ON jr.JourneyId=jd.JourneyId WHERE jr.AgvId='老厂前线新多仓位3' AND jr.Stage <> 'Completed'"
"### Q26101962 backlog now"; & $R -Sql "SELECT TransportDemandKey, ReasonCode, LastSeenAt, AcceptedAt FROM JourneyBacklog WHERE TransportDemandKey LIKE 'Q26101962%'"
