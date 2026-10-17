$R = 'D:/zhengyushao/control-server-v2-ops/prod-20261014/ro-sql.ps1'
foreach ($t in 'SlotModelSlots','SlotModelVersions','ActiveSlotConfigurations','PackageCapacityRules','SlotTemplates') { "### $t cols"; & $R -Sql "SELECT group_concat(name, ',') AS c FROM pragma_table_info('$t')" }
"### ActiveSlotConfigurations"; & $R -Sql "SELECT * FROM ActiveSlotConfigurations"
"### SlotModelSlots"; & $R -Sql "SELECT * FROM SlotModelSlots"
"### PackageCapacityRules TO-247"; & $R -Sql "SELECT * FROM PackageCapacityRules WHERE PackageName LIKE '%247%' OR rowid < 4"
