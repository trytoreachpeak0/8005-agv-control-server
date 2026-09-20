# cs#199 审计表在数据库层不可改写——L1 证据

票：<https://github.com/trytoreachpeak0/8005-agv-control-server/issues/199>
分支：`b7-199/audit-db-immutability`，从 `origin/fp/v2-impl@15807555` 开。

| 提交 | 内容 |
| --- | --- |
| `5d9dd482` | 测试先行。此时实现不存在，迁移也还没生成 |
| `2acff3dc` | 实现：迁移 `20260920001500_AuditImmutabilityTriggers` 与四个触发器 |

## 01-red-tests — 测试提交上的红

在 `5d9dd482` 的代码状态上跑（工作区取自该提交的 `tests/`，`src/` 下本票的两个迁移文件
不存在）：**9 red / 18 passed / 27 total**。

```
Failed AuditDatabaseImmutabilityTests.BothAuditTablesGetTheirPairOfTriggersAndNoOtherTableGetsOne
Failed AuditDatabaseImmutabilityTests.MigratingDownDropsTheTriggersAndMigratingUpAgainBringsThemBack
Failed AuditDatabaseImmutabilityTests.NoPathDeletesAnAuditRecordInsideTheRetentionFloorOnAMigratedDatabase
Failed AuditDatabaseImmutabilityTests.NoPathRewritesAnAuditRecordOnAMigratedDatabase
Failed AuditDatabaseImmutabilityTests.TheMigrationAddsNothingButTriggersToADatabaseThatHasNeverSeenIt
Failed AuditDatabaseImmutabilityTests.TheTriggersComeInOneMigrationStraightAfterTheAreaEndAdmissionMigration
Failed Batch3MigrationDisciplineTests.Batch3AddsExactlyOneMigrationAndItIsTheLastOne
Failed Batch6MigrationDisciplineTests.Batch6AddsExactlyOneMigrationAndItComesStraightAfterTheBatch5Migration
Failed Batch7MigrationDisciplineTests.Batch7AddsExactlyOneMigrationStraightAfterBatch6AndOnlyNamedOnesFollowIt
```

票面要的那条「原始 SQL 改写成功」的红，原文在 `run.txt`：

```
  Failed ControlServer.Tests.AuditDatabaseImmutabilityTests.NoPathRewritesAnAuditRecordOnAMigratedDatabase
  Error Message:
   Assert.Throws() Failure: No exception was thrown
Expected: typeof(Microsoft.Data.Sqlite.SqliteException)
```

即：迁移过的库上，一条 `UPDATE "BusinessAuditRecords" SET Action = 'TAMPERED' WHERE ...`
在没有触发器时**执行成功**，EF 层那个守卫根本看不到它。

## 02-green — 实现提交上的绿

`dotnet test --filter Governance|Audit|TaskTypeStation|SlotConfiguration|AgvRestoration|MigrationDiscipline|IntegrationSliceTrait`，
**341 passed / 0 failed / 341 total**（2 分 32 秒）。范围覆盖所有写审计的测试类，以及全部迁移纪律测试与切片台账测试。

## 03-fault-injection — 注入故障让「迁下去再迁回」变红

两条迁移各注入一次，跑完即还原（`git checkout -- src/`）：

| 文件 | 注入 | 结果 |
| --- | --- | --- |
| `batch6-down-missing-droptable.txt` | 删掉批次 6 `Down()` 里 `DropTable("TaskTypeStationHolds")` | `Batch6MigrationDisciplineTests.MigratingDownToBatch5...` 红：`Assert.All() Failure: 1 out of 8 items ... Assert.DoesNotContain() Failure: Item found in set` |
| `cs199-down-missing-drop-trigger.txt` | 删掉本票 `Down()` 里 `DROP TRIGGER "TR_{table}_NoUpdate"` | `AuditDatabaseImmutabilityTests.MigratingDownDrops...` 红：`Assert.Empty() Failure: Collection was not empty` |

## 04-format

`dotnet format --verify-no-changes` 对本票改动的七个文件，退出码 0，无差异。

## 不在本机跑的

服务端全量测试与 `l2` 全清单走 CI（`test.yml` / `l2.yml`），本票不占真装置时段。
