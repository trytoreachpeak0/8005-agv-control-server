# 缺陷：指纹不符的车被锁在自己的修复通道之外，以及顺着这条路挖出来的另外三处

Status: fixed
Owner repository: `8005-agv-control-server`
Found by: [`FP-IS-14` 拒绝路径 G3 第二次运行](../../evidence/g3/20260910-fp-is-14-fingerprint-mismatch-corrected/)（D-1、D-2）；[第三次运行](../../evidence/g3/20260910-fp-is-14-fingerprint-mismatch-unready/)（D-3）；第二、三次之间的自查（D-4，无红证据）
Product at discovery: `fp/b3-on-v2@b6064690799aaf374d00f1bbde43228d8c3c60bd`（D-1、D-2）；`fp/b3-on-v2@6b751414fb515d334ff190df4e31cf7c90f486ec`（D-3）
Peers: `w2g/b3-on-v2@afba86e07116192bde386937c559cc7a70a00a7f`；`slots-simulator@fb5f7c593742bf98bc3957b8729a38aad5321f28`；`fp/v2-candidate@f6ee75defe6e2d18f63f4082bee445dbb678ab1b`（协议 v2 候选，尚未打 tag）

`FP-IS-14` 的拒绝路径由 `scripts/run-staged-g3-restart.ps1` 证：phase 1 之后先做一次被接受的激活，phase 2
之前篡改车上生效配置文档，phase 2 之后再激活一次，要求车报 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`、
服务端不动生效配置。这条路跑了四次才绿。第一次红在 runner 自己（篡改点选错，见
[`20260910-g3-runner-red-runs-on-fp-is-14-and-fp-is-15.md`](20260910-g3-runner-red-runs-on-fp-is-14-and-fp-is-15.md)
的 R-3），**后两次红在产品**，一共四处。四处都是单元测试全绿时存在的。

按后果排序。

---

## D-1：握手时指纹不符就拒绝会话——不一致本身堵死了修复不一致的那条路

### 现象

篡改生效之后（文档哈希 `aa4f843b…` → `0b46a931…`），车重启，车载端日志从那一刻到 run 结束每两秒一次：

```
Warning WireToGateSessionService  上层会话被拒绝：SLOT_CONFIGURATION_FINGERPRINT_MISMATCH，2 秒后重试
```

`CapabilitySnapshot` 报上来的 `activeSlotConfigurationFingerprint` 与服务端认定的那一版不符，服务端回
`ProtocolProblem`，会话不建立。十二条断言红，十条是被连带的。

**这是一个死锁。**指纹核验有两道：握手时核一次，激活时车自己再核一次（消息 7 带
`targetSlotConfigurationFingerprint`）。第一道把第二道永远屏蔽了——一台配置被动过的车上不了线，而唯一能
把它改回来的手段，下发一次激活，要走会话。现实后果是那台车必须有人到车前处理，远程无解。

### 为什么没被发现

第一道核验是 2026-09-10 接消息面时加的，理由是 fail-closed：服务端不该在自己不确定车装着什么的时候
还采纳它的能力快照。当时的单元测试 `…IsRefusedWithTheStableCodeAndLeavesTheVehicleUnready` 断言的正是
「回 `ProtocolProblem`」——测试把设计钉住了，而设计本身漏想了恢复通道。没有任何一层在 G3 之前让一台
「指纹不符的车」真的去重连、再被下发一次激活。

### 处置

`cd379a7`。口径由用户 2026-09-10 定：**降为不就绪，会话照建。**

- `SessionRecoveryRow.ReportedSlotConfigurationFingerprint`：能力快照到达时如实存下，核验照做，不一致
  照样写不可改写的治理审计。迁移 `20260910063725_ReportedSlotConfigurationFingerprint`，已登记进
  `MigrationsAfterBatch3`。
- `DecideReadinessAsync`：服务端有生效版本且与车报的不等 → 不就绪，原因码
  `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`，排在其它原因之前。
- 协议里没有「收到但不采纳」的中间态（`SnapshotAppliedAck` 的 payload 是封闭四字段，车载端严格要求
  `appliedRevision` 等于它发的那个），所以拒绝只能在就绪判定上表达，不能在 ack 上表达。

fail-closed 的实质保住了：不一致的车拿不到业务就绪、不会被派活。关掉的只是「连都不让连」那一层。

### 回归守卫

- `CapabilitySnapshotFingerprintTests.AReportedFingerprintThatDisagreesLeavesTheVehicleUnreadyButStillReachable`：
  回 ack、上报列已存、判定不就绪且原因码正确、生效配置没动，**最后一条断言是改动的理由本身**——不一致的
  车仍能被 `IssueAsync` 下发激活。
- `run-staged-g3-restart.ps1` 的三条断言：`slotConfigurationActivationAcceptedWhileTheVehicleMatched`、
  `slotConfigurationActivationRefusedAfterTheVehicleConfigurationChanged`、
  `aRefusedActivationLeftTheActiveConfigurationUntouched`。

---

## D-2：车不在线时下发激活回 500，库里却已经留下一行 `PENDING_RESULT`

### 现象

同一次运行，phase 2 之后的那次激活：

```
afterTamper → 500 Internal Server Error
库里：activationId 1a430053…，state PENDING_RESULT，commandMessageId 48b69755…
服务端日志：System.IO.IOException: No recovered Onboard peer is connected for '…'
```

`IssueAsync` 里 `SendPersistedAsync` 抛 `IOException`，冒泡成 500。

### 为什么没被发现

落库那一半从来就是对的——命令进了发件箱，车回来按 `SLOT_CONFIGURATION` 补发正是为这种情况准备的。
错的只是把它报成服务器错误。入口的单元测试用的 peer 永远在线，没有一条走「发不出去」这一支。
D-1 让车连不上，这一支才第一次被走到。

### 处置

`cd379a7`：捕获 `IOException`，查回那一行 `PENDING_RESULT` 返回 202。查回时在内存里按 `IssuedAt` 取最新，
不在 SQL 里排序——SQLite provider 不接受 `DateTimeOffset` 出现在 `ORDER BY`。

### 回归守卫

`SlotConfigurationActivationEndpointsTests.AnActivationForAnOfflineVehicleIsAcceptedAndLeftForReplayNotReportedAsAServerError`，
用一个抛 `IOException` 的 peer 驱动。

---

## D-3：新原因码没有协议 `ErrorCode` 映射，发 `SessionReadiness` 时抛异常断连，会话代从 2 涨到 16

### 现象

D-1 修好之后重跑。202 回来了，生效配置没动，但车重启后仍进入重连循环。车载端每两秒一次：

```
上层会话不可用：HANDSHAKE_SEQUENCE_INVALID
上层会话不可用：ControlServer在会话恢复期间关闭了连接。
```

服务端日志：

```
System.IO.InvalidDataException: Session reason code 'SLOT_CONFIGURATION_FINGERPRINT_MISMATCH'
has no protocol ErrorCode mapping.
```

就绪结果发给车之前要经过 `ProtocolErrorCodes.ToSessionReadinessReasonCode`，switch 里没有这一支，走到兜底
throw。十一条断言红，十条是被连带的。

### 为什么没被发现

两层原因叠在一起：

- D-1 的单元测试直接调 `DecideReadinessAsync`，断言原因码在协议注册表里。那一条是对的，但它没走「把就绪
  结果序列化发出去」这一步，所以没看见那张映射表。
- `SessionReadinessReasonCodesTests` 那张表是手抄的「`GetRecoveryReason` 能返回的码」，而这个码不从
  `GetRecoveryReason` 出来，是 `DecideReadinessAsync` 直接给的，手抄的表天然漏它。

### 处置

`6dc4bc8`：映射成它自身。`SessionReadiness.reasonCodes` 的元素类型就是协议 `ErrorCode`，这个码在枚举里。
也必须是它自身而不是并进 `SESSION_RECOVERY_REQUIRED`——车上的人要知道该做的是一次激活，不是开恢复会话。

### 回归守卫

- `SessionReadinessReasonCodesTests.AFingerprintMismatchGoesOnTheWireAsItself`。
- `…LeavesTheVehicleUnreadyButStillReachable` 改为对存储层真实给出的 `decision.ReasonCode` 调一次
  `ToSessionReadinessReasonCode`——跟着真实产出的码走映射，才是能抓住这一类漏洞的形状。

---

## D-4：新会话不清空上报指纹；清空之后「车还没报」又会被报成指纹不符

### 现象

D-1 修完、D-3 暴露之前自查发现，**没有对应的红证据**：

- 新会话会清空 `CapabilityRevision`，却没清空 `ReportedSlotConfigurationFingerprint`。上一代的上报会被带进
  下一代，替一台这一代还没报的车说话——而重启过的车恰恰是配置可能在中间变过的那一台。
- 如果只是跟着清空，服务端一旦有生效版本，每一台还在握手、能力快照没到的车都会先被报成指纹不符
  （`null` 不等于任何指纹，而这个原因码排在最前面），运维会去追一台唯一的问题只是还在握手的车。

### 处置

`6b75141`：新会话清空这一列；上报为 `null` 时不算不一致，那一刻真实的原因是 `CapabilityRevision` 为
`null` 带来的握手未完成。

### 回归守卫

`CapabilitySnapshotFingerprintTests.AVehicleThatHasNotReportedYetIsNotNamedAsAFingerprintMismatch`：激活过、能力
快照还没到时不报 mismatch；报了一致的那一版之后也不报。

---

## 验证

四处修完之后两个 runner 都在 `6dc4bc8` 上绿：

- 进程重启：[`20260910-fp-is-14-fingerprint-mismatch-mapped`](../../evidence/g3/20260910-fp-is-14-fingerprint-mismatch-mapped/)，
  25 条断言，会话代 1／2／3 无重连循环，第二次激活 `FAILED` 且 `ReasonCode` 为
  `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`，生效配置一个字段没动。
- 主 runner：[`20260910-fp-is-14-15-staged-6dc4bc8`](../../evidence/g3/20260910-fp-is-14-15-staged-6dc4bc8/)，30 条断言，
  证明改动没有弄坏「指纹一致时正常收敛」那条路。
- `CONTROL_SERVER_G2`：[`20260910-fp-is-14-15-v2-message-plane-6dc4bc8`](../../evidence/g2/20260910-fp-is-14-15-v2-message-plane-6dc4bc8/)。
  `8c0e293` 那份 G2 证的正是 D-1 的旧行为，已被这一份取代。

三份红证据原样保留，没有被重跑覆盖。

## 仍然开着的

**被篡改的车怎么修回来，没有证。**断言证的是「拒绝、不动生效版本、车仍在线」。「下发一次与车上一致的
版本让它重新就绪」需要服务端能发出一个 600 ms 的版本，而 `ApprovedSlotHardwareFacts` 是写死的。这是
口径问题，不是本文件四处缺陷的遗留。
