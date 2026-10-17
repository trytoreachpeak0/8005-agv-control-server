$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
$d = '1fdeba456b284ef69620bd90e087a7f3'
$tables = 'AcceptedDemands','JourneyBacklog','JourneyDemands','JourneyRuntimes','OrderIntents','CreateGateAudit','StructuralDispatchBlocks','TransportDemandSuppressions','TransportDemandCompletions','FrozenDemandStations','RiotDispatchAuditEvents','StationOperations'
$u = ($tables | ForEach-Object { "SELECT '$_' AS t, COUNT(*) AS n FROM $_ WHERE DemandId LIKE '%$d%'" }) -join ' UNION ALL '
"### counts by demandId"; & $R -Sql $u
"### by sublot text"
& $R -Sql "SELECT 'AcceptedDemands' AS t, COUNT(*) AS n FROM AcceptedDemands WHERE Sublot LIKE 'Q26101962%' OR TransportDemandKey LIKE '%Q26101962%' UNION ALL SELECT 'JourneyBacklog', COUNT(*) FROM JourneyBacklog WHERE TransportDemandKey LIKE '%Q26101962%' UNION ALL SELECT 'StructuralDispatchBlocks', COUNT(*) FROM StructuralDispatchBlocks WHERE TransportDemandKey LIKE '%Q26101962%' UNION ALL SELECT 'CreateGateAudit', COUNT(*) FROM CreateGateAudit WHERE TransportDemandKey LIKE '%Q26101962%' UNION ALL SELECT 'TransportDemandSuppressions', COUNT(*) FROM TransportDemandSuppressions WHERE TransportDemandKey LIKE '%Q26101962%'"
"### AcceptedDemands rows for Q26101962*"; & $R -Sql "SELECT * FROM AcceptedDemands WHERE Sublot LIKE 'Q26101962%' OR TransportDemandKey LIKE '%Q26101962%'"
"### JourneyBacklog rows"; & $R -Sql "SELECT * FROM JourneyBacklog WHERE TransportDemandKey LIKE '%Q26101962%'"
"### StructuralDispatchBlocks rows"; & $R -Sql "SELECT * FROM StructuralDispatchBlocks WHERE TransportDemandKey LIKE '%Q26101962%'"
"### AcceptedDemands overall"; & $R -Sql "SELECT COUNT(*) AS n, MIN(rowid) AS lo, MAX(rowid) AS hi FROM AcceptedDemands"
"### AcceptedDemands columns"; & $R -Sql "SELECT name FROM pragma_table_info('AcceptedDemands')"
"### JourneyBacklog columns"; & $R -Sql "SELECT name FROM pragma_table_info('JourneyBacklog')"
