# 缺陷：状态未知的结果重启后无法对账——车载端补发会被收件箱当成内容冲突拒收

Status: fixed（服务端）；车载端一半在车载端仓 `docs/W2G_UNKNOWN_RESULT_RECONCILE.md`
Owner repository: `8005-agv-control-server`（`src/ControlServer.Host/Transport/OnboardMessageProcessor.cs`、`src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs`）
Found by: 2026-09-13 为 `FP-IS-03` 设计 G3 场景时，对照协议向量 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE` 的代码走查（无门禁红证据：G3 此前没有这个切片的面）；随后由一条单元测试在修复前的代码上复现为红
Product at discovery: `fp/b2-close@cb5aecea`
Fixed in: 见提交记录（本单与修复同一提交）
Peers: 车载端 `w2g/b3-on-v2`（同日车载端改动，见上）

**红在产品。两端各自的 G2 都绿；这条向量的第二条 `OperationResult` 两端都从没发过、也收不下。**

## 向量与决定

`CV-OPERATION-RESULT-UNKNOWN-RECONCILE` 的顺序：`OperationResult` → `DurableAck` → `RecoveryStateReport` → `OperationResult` → `DurableAck`；
服务端 `NEVER_TREAT_UNKNOWN_AS_SUCCESS`、`RECONCILE_FROM_REPORTED_JOURNAL`，车载端 `REPORT_UNKNOWN_AS_UNKNOWN`、`REPLAY_RESULT_ON_RECONNECT`；
禁止 `ready-before-reconciliation`、`unknown-as-success`、`duplicate-business-commit`。

2026-09-13 用户裁定按向量两端都补齐：

- 车载端：没能让操作结清的结果（`UNKNOWN`、`FAILED` 且已进日志）记进 `RecoveryStateReport.pendingResults`；报告被确认后，把这些已确认过的结果按新会话代号原样再发一次。
- 服务端：收下这条补发，按业务身份判为重放，从会话的待结清列表里划掉，重新判就绪；结果本身的裁决不变，`UNKNOWN` 始终不当成功。

## 现象（服务端）

两处各自挡死了这条补发，第三处让列表一旦非空就永远非空：

1. **收件箱。**`CaptureFirstResponseAsync` 按 `messageId` 去重，线上内容哈希不同即抛 `ProtocolContentConflictException`。
   车载端补发时只把 `sessionGeneration` 改成新会话代号（`RebindSessionGeneration`），整行哈希因此不同；
   只有 `RecoveryStateReport` 带了忽略会话代号的等价哈希，`OperationResult` 没有。
2. **结果表。**即便收件箱放行，`ApplyOperationResultAsync` 判重放时还要求 `ContentHash`（整行哈希）相等，同样会抛内容冲突。
3. **待结清列表。**`ApplyRecoveryReportAsync` 把报告里的 `pendingResults` 存进 `PendingResultIdsJson`，此后只有下一份报告或下一次握手会改写它；
   收到对应结果并不会把它划掉。`DecideReadinessAsync` 要求列表为空，所以车载端一旦报了非空列表，本会话就再也回不到 `READY`。

此前没人发现，是因为车载端的 `pendingResults` 永远是空的（见车载端文档）。

## 修复

- `OnboardMessageProcessor`：`OperationResult` 与 `RecoveryStateReport` 共用 `GenerationRebindReplayHash`（原 `RecoveryReplayIdentityHash`，只把 `sessionGeneration` 置零后求哈希）。
  只差会话代号的补发走等价重放：重新执行处理、按新行回 `DurableAck`；其他任何字段不同仍是内容冲突。
- `WireToGateStore.ApplyOperationResultAsync`：判重放不再比整行哈希，只比尝试号、车号、强制恢复代与 `resultContentSha256`（业务内容哈希）。
  结果行不重写，`OperationResultDisposition.Replay` 不改任何业务状态。
- `WireToGateStore.ReconcileReportedPendingResultAsync`：处理 `OperationResult` 时，把它的 `messageId` 从当前会话的 `PendingResultIdsJson` 里划掉，随后照常 `DecideReadinessAsync`。
  不论裁决是什么都划掉：列表记的是「服务端还没见到的结果」，裁决留在操作上。

补发的结果对应的操作仍在车载端日志里未结清（报告带 `unsettledSlotOperationAttemptId`），服务端侧操作也仍是 `RecoveryRequired`，所以划掉之后会话仍是 `RECOVERY_REQUIRED`，不会提前就绪。

## 证据

| 项 | 结果 |
| --- | --- |
| 新增 `OnboardMessageProcessorTests.AnUnknownResultReportedAsPendingIsReplayedInTheNextSessionAndReconciledWithoutSuccess` | 修复前红：补发那一步抛 `ProtocolContentConflictException: MessageId was replayed with different normalized content.`；修复后绿：补发回 `DurableAck`（新会话代号、新行哈希），`PendingResultIdsJson` 为 `[]`，会话仍 `RecoveryRequired`，结果行仍一条，操作仍 `RecoveryRequired`，需求未成功；改了 `overallOutcome` 的同号补发仍抛内容冲突 |
| `OnboardMessageProcessorTests` | 12 passed |
| 全量 | 720 passed |

真车载端上的完整顺序在 G3 `FP-IS-03` 里核对，那份证据出来后补进本表。
