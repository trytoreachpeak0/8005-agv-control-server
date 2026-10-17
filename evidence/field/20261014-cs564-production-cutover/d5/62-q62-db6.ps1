$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### JourneyStops of agv03 journey"; & $R -Sql "SELECT Sequence, StopRole, StationId, StationRiotId, Status, CreatedAt, OperatorDepartedAt FROM JourneyStops WHERE JourneyId='journey:dadde6f1-9e2b-4ef1-9141-ca7f7b5ef328' ORDER BY Sequence"
"### JourneyDemands status/removal"; & $R -Sql "SELECT jd.DemandId, ad.Sublot, jd.Status, jd.AddedAt, jd.RemovedAt, jd.RemovalReason FROM JourneyDemands jd LEFT JOIN AcceptedDemands ad ON ad.DemandId=jd.DemandId WHERE jd.JourneyId='journey:dadde6f1-9e2b-4ef1-9141-ca7f7b5ef328' ORDER BY jd.AddedAt"
"### OrderIntents cols"; & $R -Sql "SELECT group_concat(name, ',') AS c FROM pragma_table_info('OrderIntents')"
