$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### backlog reason distribution (unaccepted, seen in last 10 min)"
& $R -Sql "SELECT ReasonCode, substr(TransportDemandKey, instr(TransportDemandKey,'|')+1) AS wt, COUNT(*) AS n, MAX(LastSeenAt) AS last FROM JourneyBacklog WHERE AcceptedAt IS NULL GROUP BY ReasonCode, wt ORDER BY last DESC"
"### CreateGateAudit columns"; & $R -Sql "SELECT group_concat(name, ',') AS cols FROM pragma_table_info('CreateGateAudit')"
"### CreateGateAudit rows Q26101962"; & $R -Sql "SELECT * FROM CreateGateAudit WHERE TransportDemandKey LIKE '%Q26101962%'"
"### DispatchZoneVehicles"; & $R -Sql "SELECT * FROM DispatchZoneVehicles"
"### AcceptedDemands DIE_TO_WIRE_STAGING"; & $R -Sql "SELECT Sublot, WorkType, Status, AcceptedAt, json_extract(LiveMesFieldsJson,'$.area') AS area FROM AcceptedDemands ORDER BY AcceptedAt"
"### AdmissionDecisionSnapshots columns"; & $R -Sql "SELECT group_concat(name, ',') AS cols FROM pragma_table_info('AdmissionDecisionSnapshots')"
"### VehicleTaskTypeAdmissions"; & $R -Sql "SELECT * FROM VehicleTaskTypeAdmissions"
