# G2：跨趟快照 revision 与两处 settle 的回归缺口（`127b137`）

## 运行类型

纯 tier 1 / G2。**不动车、不建单、不使用任何现场凭据、未触碰已安装服务**。
八片 G2 全部在提交 `127b137` 之后运行，`implementationCommit` 由脚本从工作树读回而非重述。

## 结论

| 项 | 结果 |
| --- | --- |
| Release 构建 | 0 warning / 0 error |
| `dotnet format --verify-no-changes` | 干净 |
| 全量测试（Release） | 230 passed / 0 failed / **0 skipped** |
| 八片 G2 | 全部 PASS，绑 `127b137` + `protocol-v0.1.1@1531489e` |

`protocolManifestSha256 = a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`，
八份 `gate-result.json` 逐一回读一致。

## 缺陷一：快照 revision 不跨趟持久化

### 待确认的未知数已由只读检查解掉

先前的复核把「车载端是否在会话间重置已采用 revision」列为动手前必须解开的未知数。
车载端仓对 agent 只读，但**只读检查与 fetch 是允许的**，因此直接读了
`OnboardHmi_MVP@304e6ad`（王昆 2026-08-29 的 `Fix snapshot revision replay across sessions`）：

| 事实 | 位置（车载端仓，只读） |
| --- | --- |
| 已采用快照持久在 SQLite，键**只有 MessageType** | `SqliteWireToGateJournal.cs` `ON CONFLICT(MessageType)` |
| 全仓无任何 DELETE，该表永不清空 | `git grep WireToGateAppliedJourneySnapshots` 仅 CREATE/SELECT/INSERT |
| 更低 revision → `SNAPSHOT_REVISION_REGRESSION` | `SqliteWireToGateJournal.cs`、`WireToGateSessionClient.ApplyJourneyRevision` |
| 相同 revision 且 payload 规范化哈希不同 → `SNAPSHOT_REVISION_CONTENT_CONFLICT` | 同上 |
| 两种拒绝都先发 protocol problem **再 rethrow**（连接被拆） | `WireToGateSessionClient.ApplyJourneySnapshotAsync` 的 catch |
| 断连时 `ResetJourneyProjection()` 只清内存 | `CloseConnectionAsync`、接收循环的 catch |
| **每次重连在 SessionHello 之前从 journal 还原** | `RestorePersistedJourneyProjectionAsync` |

结论：**车载端不在会话间重置已采用 revision**，也不按 demand 重置。因此第二趟不是「侥幸绕过」，
而是**必然被拒**，且形态是 `SNAPSHOT_REVISION_REGRESSION`（1 < 2），不是先前推测的内容冲突。

### 服务端侧缺陷范围比复核所述更宽

`ToRuntimeRow` 把**三个** revision 都写死为 1：`VehicleBusinessRevision`、`WorklistRevision`、
`PlanRevision`（`WireToGateStore.cs`）。三者分别落在
`VehicleBusinessStateSnapshot`／`CurrentStopWorklistSnapshot`／`UpcomingStopPlanSnapshot`，
而车载端按 MessageType 分别记账，所以三条流在第二趟同时回退。复核只点名了 worklist 一条。

runtime 行按 `DemandId` 建、且全仓无删除点；车辆租约在完整安全卸货后释放，
**同一台车接第二趟 demand 是设计内路径**。现场没暴露，只因授权闭环只跑了一趟。

### 修复

新 runtime 行的三个 revision 从该 `AgvId` 已存储的最高值 **+2** 起算
（`WireToGateStore.SeedSnapshotRevisionsAsync`）。+2 的依据：一趟旅程在取货站发出其存储值、
在关卡站发出该值 +1，故下一趟必须从 +2 开始。幂等重放走的是早退分支，`Matches` 不比较 revision，
不受影响；同一 `VehicleKey` 的并发受租约排他保护。

不采用「per (AgvId, StationId) 计数器」那条候选：车载端按 MessageType 记账，
按站点各自计数会让同一趟的取货与关卡都落在 1，正是 `3d8b00c` 修掉的那次冲突。

### 可证伪性

`ASecondJourneyOnTheSameVehicleNeverRepublishesAnAdoptedRevision` 断言三条快照流各自的
首次发布 revision 序列非递减且恰有三个不同值（第一趟两站 + 第二趟取货站）。
去掉 `SeedSnapshotRevisionsAsync` 调用后该断言变红，实际序列为 **`[1, 2, 1]`**——
取货 1、关卡 2、第二趟又回到 1，即缺陷本体。

断言按 messageId 去重后再判断：fixture 的 peer 从不 ACK，未确认的 outbox 行每轮迭代都被重发，
只有首次发送对应一次 revision 分配。

## 缺陷二：四处 settle 只有两处有回归保护

`JourneyRuntimeEngine` 的四处 `SettleAnsweredCommandAsync` 中，既有测试只覆盖 sublot request 与
load command 两处；安全检查与卸货命令的 settle 发生在旅程离开关卡阶段之后，越过了所有既有测试的
观测终点。

新增 `CommandsAnsweredAfterTheGateDepartureAreSettledToo`，跑到 `Completed` 后断言
`PreDepartureSafetyCheckMessageId` 与 `UnloadCommandMessageId` 都不在未确认 outbox 中。

### 可证伪性

两处 settle 各自单独删除，各让**对应的那一条**断言变红，失败信息给出的 messageId 不同
（`8a850d01-…` 对安全检查，`bda91f23-…` 对卸货命令），因此两条断言互相独立，不是一条覆盖两处。

## 已知范围外

- 若某条快照的 ACK 丢失，它会在更高 revision 的快照之后被重发，真实对端同样会判为
  `SNAPSHOT_REVISION_REGRESSION`。本轮未处理，也不在票 22 范围内——它属于可靠重发顺序，
  不属于 revision 分配。
- 车载端把更低 revision 处理为抛异常并拆连接，而 ADR-cross-0048 原文是「更低 revision 丢弃并
  返回当前已采用版本」。这是车载端实现与 ADR 的偏差，归 `8005-agv-onboard-hmi`（对 agent 只读），
  本仓不修、不写入该仓。
