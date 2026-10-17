$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### CreateGateAudit 07:20-08:20Z"; & $R -Sql "SELECT EvaluatedAt, TransportDemandKey, AgvId, TargetStationId, Verdict, ConflictDetail FROM CreateGateAudit WHERE EvaluatedAt >= '2026-10-17 07:20' AND EvaluatedAt < '2026-10-17 08:20' ORDER BY EvaluatedAt"
"### OrderIntents agv03 07:20-08:20Z"; & $R -Sql "SELECT CreatedAt, Purpose, DemandId, TargetStationId, Status FROM OrderIntents WHERE VehicleKey LIKE '%7daca4ee%' AND CreatedAt >= '2026-10-17 07:20' ORDER BY CreatedAt"
