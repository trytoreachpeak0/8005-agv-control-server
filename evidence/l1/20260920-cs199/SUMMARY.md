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

## 06-review-s1-s2 — 独立审查的两条

**S2 是真口子。** `REPLACE INTO` / `INSERT OR REPLACE` 能整行改写任何年纪的审计，原来那两个触发器
一个都不响：`REPLACE` 是 INSERT 语句，`BEFORE UPDATE` 看不见；它为解决主键冲突做的隐式删除，在
`PRAGMA recursive_triggers` 为 OFF（SQLite 默认，本服务端没有任何地方打开）时不触发 `BEFORE DELETE`。
每张表加第三个触发器 `TR_*_NoReplace`（`BEFORE INSERT`，主键已存在即 ABORT）。

**S1：180 天边界原来一个测试都碰不到。** 原有种子是 10 天前与 200 天前，离边界有 170 天和 20 天余量，
算错边界也测不出来。新增 `TheFloorSitsExactlyAtOneHundredAndEightyDaysNotTwoHoursEitherSideOfIt`，
边界两侧各放一条（差两小时）。

| 文件 | 注入／变异 | 结果 |
| --- | --- | --- |
| `red-s2-without-noreplace-trigger.txt` | 去掉 `TR_*_NoReplace` | `ReplaceIntoCannotRewriteAnExistingAuditRecordAtAnyAge` 红：`Assert.Throws() Failure: No exception was thrown` |
| `red-s1-localtime-mutation.txt` | `julianday('now')` → `julianday('now','localtime')` | 新边界测试单独红，**其余 12 条全绿** |
| `red-s1-epoch-mutation.txt` | 纪元 `1721425.5` → 儒略历 `1721423.5` | 新边界测试单独红，**其余 12 条全绿** |
| `green-after-s1-s2.txt` | 修完 | 71 passed / 0 failed |

后两行正是 S1 要说的事：原有那些测试对这条线是盲的。

## CI

结论用 `gh run view` 读，不看 `gh run watch` 的退出码。

| 提交 | 那一版是什么 | `test` | `l2` |
| --- | --- | --- | --- |
| （草稿，手动 dispatch） | 转 ready 前自检 | 35479156051 success | — |
| `a07c2898` | 转 ready 的第一版 | 35479540215 success | 35479540235 success |
| `f7750994` | merge 集成分支 `13a1db75`（cs#232） | 35480178156 success | 35480178158 success |
| `a34d6eb8` | 独立审查 S1／S2 修完 | 35481641234 success | 35481641224 success |

`l2` 的 `real-rig` 作业按预期 skipped，本票不占真装置时段。`l2` 起的是真 ControlServer，
服务端启动走 `MigrateAsync()`，所以每一轮都跑在带触发器的库上——这正是票面对 L2 的全部要求（不加判据）。

**跑了三轮而不是约定的一轮。** 第二轮是补证据提交触发的，那一版代码一字未动，是纯浪费——
根因是证据目录该在开 PR 之前就备齐，我补晚了。第三轮是审查后的修复，必要。

## 哪些结论本票没测

一片绿最危险的用法是被当成一句它没说过的话。**注意「没测」和「测不了」是两句不同的话**——
这份 SUMMARY 早先一版把 180 天边界写成「测试覆盖不到」，独立审查指出那不成立：它是可测的，
只要把种子挪到边界附近。现在测了（见 06）。留下的是这两条：

| 没测到的 | 为什么 | 现在靠什么 |
| --- | --- | --- |
| `Down()` 在触发器已经不存在时能跑完 | 测试只走正常的 up → down → up | `DROP TRIGGER IF EXISTS` |
| EF 层与触发器**反方向**不打架（应用时钟跑在数据库时钟前面：EF 放行、触发器拒绝） | 生产上两者同机同进程，`TimeProvider.System` 读的就是数据库机器那个时钟 | 推理，写在测试类的 `<remarks>` 里 |

第二条的用处：将来第一个写「在迁移过的库上把 `AuditClock` 往前拨过 180 天再清理」测试的人会撞上它，
拿到 `SqliteException` 而不是 `AuditRecordImmutabilityException`。那不是产品缺陷，是两层各按各的时钟判的
必然结果。
