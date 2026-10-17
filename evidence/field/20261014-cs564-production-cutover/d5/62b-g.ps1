$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
foreach ($t in 'RouteGraphSnapshots','RouteGraphEdges','RouteGraphStations','RouteGraphRemovedEdges','RouteGraphEdgeGroups') { "### $t"; & $R -Sql "SELECT group_concat(name, ',') AS c FROM pragma_table_info('$t')"; & $R -Sql "SELECT COUNT(*) AS n FROM $t" }
"### snapshots"; & $R -Sql "SELECT * FROM RouteGraphSnapshots ORDER BY rowid DESC LIMIT 3"
