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

## 05-architecture

`IntegrationSliceTraitArchitectureTests` 单跑：**5 passed / 0 failed**。本票加了新测试类
`AuditDatabaseImmutabilityTests`，切片豁免表必须登记一行，登记在测试提交 `5d9dd482` 里。

一个测量陷阱，记在这里：`dotnet test` 只在**失败**时打印类名，通过的不打印。所以在
`02-green/run.txt` 里 grep `IntegrationSliceTraitArchitectureTests` 得到 0，**不能**推出
「这个类没跑到」——那一轮的 filter 里带着它。单跑才是干净的确认。

## CI

一轮，两条都 success，都在 `a07c2898` 上（结论用 `gh run view` 读，不看 `gh run watch` 的退出码）：

| workflow | run | 结论 |
| --- | --- | --- |
| `test`（草稿自检，手动 dispatch） | 35479156051 | success |
| `test`（ready 触发，正式一轮） | 35479540215 | success |
| `l2`（ready 触发，正式一轮） | 35479540235 | success（`scenarios` success，`real-rig` skipped） |

`l2` 起的是真 ControlServer，服务端启动走 `MigrateAsync()`，所以那一轮跑在带触发器的库上——
这正是票面对 L2 的全部要求（不加判据）。本票不占真装置时段。

## 有两处结论靠推理，测试覆盖不到

| 结论 | 为什么测试覆盖不到 |
| --- | --- |
| 180 天下限两端都以 UTC 为原点，不差 8 小时 | 种子的两个点（10 天前、200 天前）离边界有 170 天以上余量，差 8 小时照样全绿 |
| `Down()` 的 `DROP TRIGGER` 不需要 `IF EXISTS` | 测试只走正常的 up → down → up，没有「`Up()` 没跑过就 `Down()`」这条路 |

写下来是因为一片绿最危险的用法，是被当成一句它没说过的话。
