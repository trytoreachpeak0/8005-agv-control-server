# staged G3 的四条内容冲突判据仍要求「断开连接」，没有跟上 cs#478

Found by: [`evidence/g3/20261010-protocol-v3.0.0-staged-5f3adc42/`](../../evidence/g3/20261010-protocol-v3.0.0-staged-5f3adc42/)（control-server#393 出口第一轮 G3，runId `20261009T161926941Z`）

## 现象

批次 8 与 v3 合并出口的第一轮 G3 里，`run-staged-g3.ps1` 判 `STAGED_SLICE_FAIL`。这一轮的绑定是 ControlServer `5f3adc42`、Onboard `b9e67a53`、Simulator `fb5f7c59`、Protocol `3f091cb2`，`runnerSource COMMITTED_RUNNER`。

- `FP-IS-00`、`FP-IS-14`：PASS。
- `FP-IS-06`、`FP-IS-07`：FAIL。红在下面四条判据，其余判据全部 PASS。

| 切片 | 判据 | 红的那一格 |
| --- | --- | --- |
| `FP-IS-06` | `sameMessageIdDifferentContentStableConflict` | Heartbeat 同一 messageId、内容不同：`firstSameConnectionClosed false`、`repeatConnectionClosed false` |
| `FP-IS-06` | `businessMessageSameMessageIdDifferentContentStableConflict` | `SublotSubmitted`、`OperationProgress`、`PreDepartureSafetyCheckResult`、`SlotOperationCommandRejected` 四类，同上 |
| `FP-IS-07` | `recoverySessionAuthorisationBoundary` | 六格里只有 `recovery-session-requestid-content-conflict` 红（`connectionClosed false`），其余五格 PASS |
| `FP-IS-07` | `forcedRecoveryGenerationAdvancesMonotonically` | 只有 `resultContentConflictClosedConnection false` 不满足；第二次提交被拒、当代结果被确认、结果逐字节重放，三项都满足 |

## 原因（读到的）

四条判据的写法都是：同一 messageId、内容不同，服务端必须断开连接。例如 `run-staged-g3.ps1:630-645` 的 `conflictPass = firstConflictClosed && secondConflictClosed`。

服务端自 control-server#478 起不再断开。PR #479（提交 `e26c81286`，入站内容冲突回 ProtocolProblem，不再断连接）于 2026-10-05 02:26 合入 `fp/v2-impl`，此后服务端回 `ProtocolProblem`、码为 `MESSAGE_ID_CONTENT_CONFLICT`，保持连接，不采用冲突的那条报文。本轮服务端日志 `logs/control.out.log` 原文：

```
Refused Heartbeat cf325988-abf5-045c-88be-1e9bcc3e6f96 from AGV-8005-STAGED-G3-PROBE with ProtocolProblem MESSAGE_ID_CONTENT_CONFLICT; the connection stays and nothing of it was kept: MessageId was replayed with different normalized content.
```

意思是：服务端拒收了这条 Heartbeat，回了冲突码，连接保持，这条报文的内容一点都没记下。

协议向量 `CV-RELIABLE-RETRY-DIFFERENT-CONTENT` 要求的是回 `ProtocolProblem`、码为 `MESSAGE_ID_CONTENT_CONFLICT`、不采用冲突那条，**没有要求断开连接**。这个向量在 `protocol-v2.0.0` 与 `v3.0.0` 里一字不差（调度实读）。所以服务端符合向量，过时的是 runner 的判据。

## 不是 v3 引入的

最后一次 staged G3 是 2026-10-02 跑的（`8d0a644e`，PASS），早于 cs#478。cs#478 合入后，`fp/v2-impl` 上没人再跑过 staged G3，所以即便还是 `protocol-v2.0.0` 身份，这四条也会红。

## 去向

control-server#541：只改 G3 runner 的判据，不改 `src/`。调度裁定作为出口冻结期间的例外，合入 `fp/v2-impl`。合入后本票把它 merge 进出口分支，重移 G3 绑定，四个 runner 从头重跑。本轮的红证据原样保留。
