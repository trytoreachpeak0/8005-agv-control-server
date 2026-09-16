# 缺陷：IO 绑定发布与激活、回滚会取到同一个版本号，绑定因此挂到别人的快照上

Status: fixed
Owner repository: `8005-agv-control-server`（`src/ControlServer.Infrastructure/Persistence/SlotConfigurationAuthorityStore.cs`、`SlotConfigurationActivationCoordinator.cs`、`GovernedActivationStore.cs`、`GovernanceStore.cs`）
Found by: 2026-09-16 对 [#95](https://github.com/trytoreachpeak0/8005-agv-control-server/pull/95) 的复核（该 PR 已合入 `fp/v2-impl`，合并提交 `db6f16d3`）；
复核确认 #95 在**串行**场景下是完整的，同时指出它没有关上并发那一半。复现证据是本仓单元测试的一次红跑（四条新测试全红，未入库：单元测试红跑），见下面「红证据」
Product at discovery: `fp/v2-impl@628f0693`
Fixed in: 见提交记录（本单与修复同一提交）

无 EF 迁移，无协议改动。

## 一句话

一台车在一版车型下的仓位配置只有一条版本线，绑定发布、激活与回滚三方都在上面取号；绑定发布是「先写
DRAFT 绑定行、再冻结快照」两步，而激活与回滚取号时只数快照，于是挤在这两步之间的激活会拿到同一个号并
先冻结——`GovernanceStore.FreezeAsync` 遇到已存在的版本原样返回既有快照，绑定行与它的发布审计就指向了
一份装着激活内容的快照，而唯一索引一次都没被碰到，**没有任何东西记下这件事发生过**。

## 机制

版本线的对象是 `GovernedObjectKind.ActiveSlotConfiguration`，objectId 是 `{agvId}:{slotModelVersionId}`。
修复前三处各写一份取号查询，而三份数的东西并不一样：

| 写入方 | 位置（修复前） | 取号时数了什么 |
| --- | --- | --- |
| 绑定发布 | `SlotConfigurationAuthorityStore.cs:160` | 绑定行 **与** 快照（#95 补上的） |
| 激活下发 | `SlotConfigurationActivationCoordinator.cs:297-303` | 只数快照 |
| 回滚 | `GovernedActivationStore.cs:238` | 只数快照 |

绑定发布的两步之间有一个真实的窗口：

```
SlotConfigurationAuthorityStore.PublishIoBindingsAsync
  :186  await _context.SaveChangesAsync(...)          ← DRAFT 绑定行落地，版本号已被占用
        ← 此刻另一个写入方取号：只数快照的话，这一版看起来还是空的
  :188  await _publisher.PublishVersionAsync(...)     ← 这才冻结快照
```

而 `GovernanceStore.FreezeAsync`（修复前 `:80-83`）对已存在的版本是这样处理的：

```csharp
if (existing is not null)
{
    return Project(existing);
}
```

所以后冻结的一方拿回的是先冻结那一方的快照。唯一索引 `(ObjectKind, ObjectId, Version)` 本可以挡下重复，
但它只在真的 INSERT 时才起作用——这条路根本没有 INSERT。

**窗口不是假想的。**`ControlServer.FieldOps` 是另一个进程（`bind-io` 走的就是 `PublishIoBindingsAsync`），
写的是同一个 SQLite 库；两个进程可以在任何一方落地之前各自读到同一个最大值。

## 后果

绑定行的 `SnapshotId` 与 `SLOT_IO_BINDING_VERSION_PUBLISHED` 审计指向一份不是这批绑定的内容。治理快照
存在的意义就是「这一版到底是什么，事后可查」，这一条被破坏之后：

- 按版本回滚会回到一份不是那次发布的接线；
- 审计导出把两件事记成同一件；
- 而且它是**静默**的——没有异常、没有审计、没有约束冲突，只有事后逐字段比对才看得出来。

## 红证据

修复前跑本仓四条新测试（`dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release`
加 `--filter`），四条全红：

```
Failed ControlServer.Tests.SlotConfigurationVersionLineTests.AnActivationIssuedBetweenTheBindingRowAndItsFreezeDoesNotTakeTheBindingsVersion
  Assert.NotEqual() Failure: Values are equal
  Expected: Not 2
  Actual:       2

Failed ControlServer.Tests.SlotConfigurationVersionLineTests.AnActivationSkipsAVersionThatAHalfWrittenBindingPublicationAlreadyHolds
  Assert.Equal() Failure: Values differ
  Expected: 3
  Actual:   2

Failed ControlServer.Tests.GovernanceSnapshotAndAuditTests.RefreezingAVersionWithDifferentContentFailsLoudlyInsteadOfReturningTheExistingSnapshot
  Assert.Throws() Failure: No exception was thrown
  Expected: typeof(ControlServer.Application.GovernedSnapshotVersionConflictException)

Failed ControlServer.Tests.SlotConfigurationVersionLineTests.APublicationWhoseVersionWasTakenRetriesInsteadOfAttachingToTheOtherWritersSnapshot
  ControlServer.Application.GovernedSnapshotVersionConflictException : Version 2 of ActiveSlotConfiguration
  'AGV-02:77eb146cd3e647df9e4c5d8d8a20dac9' is already frozen as snapshot another-writers-snapshot ...

Failed!  - Failed:     4, Passed:    10, Skipped:     0, Total:    14
```

四条分别是：激活挤在绑定行与冻结之间时两方拿到同一个号（`Not 2` / `2`）；已经落地的半版绑定行没被取号
数进去（该拿 3 却拿了 2）；同一版用不同内容再冻一次不报错、原样返回既有快照；撞号之后不重试，异常直接
穿出去。

## 修法

### 1. 取号只留一处：`SlotConfigurationVersionLine`

新增 `src/ControlServer.Infrastructure/Persistence/SlotConfigurationVersionLine.cs`。objectId 怎么拼、
下一版是几、取到号之后怎么落地，三件事都在这里，三个写入方共用。取号数的是**快照与绑定行（DRAFT 与
PUBLISHED 都算）的最大值加一**——DRAFT 行代表的正是「发布到一半、号已占、快照未冻」的那一版。

### 2. 取号与落地包进一个事务，撞号就回滚重来

`PublishNextVersionAsync` 在一个数据库事务里取号并完成整次发布（绑定行、快照、激活行、审计），撞号时
整次尝试回滚、重新取号，最多五次。失败的那一次因此**一行都不留**，不会像修复前那样留下半批 DRAFT 行。
调用方自己已经开着事务时不再开第二个，也不重试：那时候「回滚」会连调用方的写入一起扔掉，这个决定不该
由这里替它做，冲突原样抛给那个更大的工作单元。

### 3. `FreezeAsync` 对「同一版、不同内容」明着报错

新增 `GovernedSnapshotVersionConflictException`（`ControlServer.Application`），带上对象、版本、既有
快照 id 与两边的 SHA-256。**同一版、同一份内容仍然原样返回既有那一份**——重投与上面的重试都靠这一条，
`GovernanceSnapshotAndAuditTests` 里守着它的那条测试保留，只是把「不同内容」那一半拆出来成了新的一条。
读与写之间另一个进程抢先冻上的情况由唯一索引挡下，这里把 `DbUpdateException`（SQLite
`SQLITE_CONSTRAINT`，19）翻译成同一个冲突。

### 这算一次行为改变

修复前「冻结一个已存在的版本」在任何情况下都不会失败。现在同一版不同内容会抛异常。受影响的只有撞号这
一种情形，而它此前的表现是悄悄挂到别人的内容上——把它变成一次响亮的失败是本票的目的，不是副作用。

## 已经写坏的行怎么办

取号修好之后新写入不再产生这种行，**修复之前写下的不会自己变好**。新增只读体检
`SlotConfigurationBindingSnapshotAudit`，以及 `ControlServer.FieldOps` 的
`check-binding-snapshots` 命令：

```
ControlServer.FieldOps check-binding-snapshots --database <path>
```

它把已发布的绑定行按「车 + 车型版本 + 版本号」成组，与各自 `SnapshotId` 指着的快照逐字段比对，输出
差异；退出码 0 是干净，1 是查出了东西（不是工具出错），2 是用法错误。

**它一行不写**：连库都用 SQLite 的 `Mode=ReadOnly` 打开，只读由驱动保证，不靠代码自觉。它也**不修**任何
东西，而且不该修：一版已冻结的快照不可改写，绑定行也已经发布，「修」意味着重新发布一版并作废旧的那一
版，那是一次有人负责的运维决定。

体检至今**没有对任何生产库或现场库跑过**。要跑，先把那个库复制一份出来，对副本跑。

## 未改动的东西

- 表结构、`objectId` 形状、对象种类：没动，因此**没有 EF 迁移**。
- 协议：没动。
- 模板与整车模型两条版本线各自的取号：没动——它们不与别人共用版本线，本票的问题不存在于那里。
