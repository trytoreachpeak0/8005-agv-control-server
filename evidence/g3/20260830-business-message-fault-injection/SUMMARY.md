# staged G3 业务消息面故障注入：`3d8b00c` + `304e6ad`

## 运行类型

`STAGED_G3_REAL_PEERS_DETERMINISTIC_TLS`。loopback 隔离，**不动车、不建单、不使用任何现场凭据**。
用户明确授权安装唯一临时测试根（`CurrentUser/Root`）。

## 结论

十二条断言全部 PASS（原七条不回归，新增五条）：

| 断言 | 结果 | 新增 |
| --- | --- | --- |
| `identityRejections` | PASS | |
| `sameConnectionSameMessageIdSameContent` | PASS | |
| `sameMessageIdDifferentContentStableConflict` | PASS | |
| `recoveryStateReportFirstAckDropReplay` | PASS | |
| `recoveryStateReportFirstAckDropReplayOverTls` | PASS | |
| `businessMessageSameMessageIdSameContentReplay` | PASS | ✅ |
| `businessMessageSameMessageIdDifferentContentStableConflict` | PASS | ✅ |
| `businessMessageAckDropInSessionReplay` | PASS | ✅ |
| `businessMessageDelayedDeliveryAccepted` | PASS | ✅ |
| `businessMessageReorderedDeliveryAccepted` | PASS | ✅ |
| `noMovementOrExternalSideEffects` | PASS | |
| `secretScan` | PASS | |

`status = STAGED_G3_TLS_RECOVERY_REPLAY_PASS`，`formalSlicePass = false`。
运行 `20260829T161019608Z`，耗时 4 分 48 秒，`configurationSha256`
`ce0264b4254cf7ed7869e1fa9876cbb82bfab941a758afe351becae5bb457516`。

## 绑定身份

| 组件 | commit |
| --- | --- |
| ControlServer | `3d8b00c7558ae700358f1f995a5ac75d12a3250c` |
| OnboardHmi | `304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6` |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| protocol | `1531489e42e328f28bfe0c51ed3f8c56e5ce0279`（`protocol-v0.1.1`，G1 `PASS`）|
| runner／harness | `5456451`，运行开始时工作树干净 |

manifest `a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f`，
ControlServer `/version` 回读 `approvalStatus = APPROVED_RELEASE`。

三个对端来自绑定 commit 的一次性克隆；runner 与其内嵌 harness 从工作树执行，因此身份是
`git rev-parse HEAD` 读回的，不是写死的。该字段在任何证据落盘前采集——否则仓内的 EvidenceRoot
会以未跟踪内容出现，把每一次运行都报成脏树。

## 本次新增了什么

原先的故障注入是 `PumpAsync` 里三行硬编码判定，只认 `RecoveryStateReport` 的 `DurableAck`，
别的消息类型注入不了，延迟与乱序也无处安放。现在换成运行时装配的规则表，动作四种：

| 动作 | 语义 |
| --- | --- |
| `drop-and-close` | 丢弃并关闭连接（原有的 `RecoveryStateReport` 向量，行为不变）|
| `drop` | 丢弃但保持连接，用于同会话内重放 |
| `delay` | 延迟指定毫秒后转发 |
| `hold-until-next` | 扣住本行，先放行下一行，再补发，构造乱序 |

业务消息由一个合成对端手工构造，**不驱动真实旅程**（`journeyRuntimeEnabled = false`）。
覆盖四种消息：`SublotSubmitted`、`OperationProgress`、`PreDepartureSafetyCheckResult`、
`SlotOperationCommandRejected`——它们的服务端处理就是一条 `DurableAck`，不需要 demand、
不需要 station operation、不需要车。

### 结果重放的判据

`PreDepartureSafetyCheckResult` 的首个 `DurableAck` 被代理吞掉（连接保留），合成对端在**同一
会话代**内原字节重发，服务端返回的是入库的首个响应：

```
droppedAckWireSha256  97bee7fcd178a2552ac78a4f8fb58fa618f2db4edae48eea34211a5edd779241
replayedAckSha256     97bee7fcd178a2552ac78a4f8fb58fa618f2db4edae48eea34211a5edd779241
```

哈希取自代理记录里被吞掉的那一行，不是探针自报——探针看不见被吞的响应。`DurableAck` 内含
`durablyAcceptedAt`，若服务端重算则该字段必变，因此逐字节相等即证明走的是存储重放。

跨会话代重发不适用：业务消息没有 `RecoveryStateReport` 那样的 `replayEquivalenceHash`，
换代后信封字节变化即判为内容冲突并断连——这是设计行为，不是缺陷。

### 延迟与乱序

- 延迟：`SlotOperationCommandRejected` 客户端到服务端注入 900 ms，实测往返 913 ms，正常确认。
- 乱序：扣住 `SublotSubmitted`，先放行其后的 `OperationProgress`，服务端按各自消息身份分别确认，
  确认顺序为 `OperationProgress` 在前、`SublotSubmitted` 在后。

`ProtocolInbox` 留下 8 行业务消息（每种消息两个 messageId：重复／冲突一组，故障向量一组），
每行 `rowCount = 1`。

## 未覆盖的两类，及原因

`OperationResult` 与 `SlotOperationCommand` 在 staged 运行中**不可达**，已写进
`run-result.json` 的 `businessProbe.coverageLimits`，不以任何方式暗示已覆盖：

- `OperationResult`：`OnboardMessageProcessor` 用 `SingleAsync` 在 `StationOperations` 上解析
  forced recovery generation，因此一条指向捏造 attempt 的 `OperationResult` 在任何确认之前就会抛出。
  `StationOperations` 只由 `PrepareSlotOperationAsync` 写入，而它需要来自 MesIngest 与 RIoT 的
  已受理 demand。
- `SlotOperationCommand`：只由 `JourneyRuntimeEngine`（本运行已关闭）发布，或由
  `OnboardRecoveryCoordinator` 从同一条带 demand 路径创建的 outbox 行重放。

两者都需要一次带 demand 的运行才能取证，不是本 runner 能补的。**没有捏造任何状态**：
`orderIntentCount`、`acceptedDemandCount`、`stationOperationCount` 三项仍为 0。

## 首次运行为什么红

同目录的 `evidence/g3/20260829-business-message-fault-injection/` 保留着第一次运行的红证据，
`status = INCONCLUSIVE_RUNNER_ERROR`。

原因是 `OnboardTcpServer.ExecuteAsync` 的 accept 循环串行——循环体内 `await HandleClientAsync`，
服务端同一时刻只服务一个车载连接。当时业务代理与真实车载端并存，合成对端的 `SessionHello`
转发出去后 4 ms 连接即断，拿不到响应。

该判据是单独证出来的，不是从日志推断的：持有一条已完成握手、一个字节都不发的 TLS 连接，
第二个客户端的握手始终不完成；第一条连接一关闭，第二条立即通过。

修法是排序而非并发——先停车载端与其代理，等服务端退出 `HandleClientAsync`，再把服务端交给
业务面。本机冒烟当初能通过，正是因为那时没有车载端占着连接。

## 运行后独立复核

不采信 `temporaryTrustCleanupVerified` 的自报，三项均独立查过：

- `CurrentUser\Root` 中 `CN=8005 staged G3*` 证书数 = 0
- 58205／58207／58215／58216／1502／58006 均无 LISTEN
- 无 `SQCD*` 残留进程；`ControlServer.Host` PID 32544 是已安装服务，未占上述端口

## 分级

`formalSlicePass = false`，`W2G-IS-00` 与 `W2G-IS-06` 仍为 `INCONCLUSIVE`，
`fullG3` 与 `releaseCandidate` 仍为 `INCONCLUSIVE`。本运行只解决票据 18 要求的
「结果重放」与「重复／乱序／延迟」在业务消息面的取证，不构成正式切片通过。
