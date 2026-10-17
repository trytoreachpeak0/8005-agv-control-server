$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
"### JourneyRuntimes cols"; & $R -Sql "SELECT group_concat(name, ',') AS c FROM pragma_table_info('JourneyRuntimes')"
"### ProtocolInbox cols"; & $R -Sql "SELECT group_concat(name, ',') AS c FROM pragma_table_info('ProtocolInbox')"
