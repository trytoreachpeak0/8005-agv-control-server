# `G3`（主 runner）：`FP-IS-00` / `FP-IS-06` / `FP-IS-14` / `FP-IS-15` 四片 **全部 `PASS`**（`6dc4bc8`）

`STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT`，`assuranceLevel` 为 `STAGED_REBUILD`，30 条断言全绿，
四份 `gate-result.json` 的 `formalSlicePass` 都是 `true`。2026-09-10 15:37:36 → 15:39:23（本地时间）。

## 为什么要重跑

同日主 runner 最后一份绿的是 `../20260910-fp-is-14-pending-result-replay-corrected/`，被测服务端是
`5ec2657`。之后 `cd379a7`／`6b75141`／`6dc4bc8` 改了 `CapabilitySnapshot` 的处理与就绪判定——指纹不符
不再拒绝会话、新会话清空上报指纹、原因码进映射表——而这个 runner 证的完整握手、恢复重连不重发快照、
逐字节补报、两端指纹一致，走的正是这两段代码。进程重启那一轮（`../20260910-fp-is-14-fingerprint-mismatch-mapped/`）
已在 `6dc4bc8` 上绿，但它替这个 runner 的断言作不了证。

那一份原样保留、未改一字；它对 `5ec2657` 仍然成立，只是不再是现行产品代码的 `G3`。

## 绑定

| | commit |
| --- | --- |
| `controlServer` | `6dc4bc8f50472301027bb603d99b8c386bbe0c72`（param 块默认值，未传 `-ControlServerCommit`） |
| `onboard` | `afba86e07116192bde386937c559cc7a70a00a7f`（`origin/w2g/b3-on-v2` 的顶） |
| `slotsSimulator` | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| `protocol` | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b`，`manifestSha256` `84f984ea…`，`tagExists: false` |
| `harness` | `eeb1828`，`harnessWorktreeCleanAtStart: true` |

与同日 `CONTROL_SERVER_G2`（`../../g2/20260910-fp-is-14-15-v2-message-plane-6dc4bc8/`）、进程重启 `G3`
绑的是**同一个服务端 commit**。`ONBOARD_HMI_G2` 绑 `d9ac1a1`，它到 `afba86e` 之间车载端只增加了证据文件。
`G1` 绑协议 `f6ee75d`，候选分支未动。至此 `FP-IS-14`／`FP-IS-15` 的四道门禁没有一道绑在过期的代码上。

装置与之前一样：车载端、模拟器、协议仓都从本地仓克隆（协议仓为 `fp/v2-candidate` 的本地克隆
`C:\Users\szy\8005-b3\proto-v2c`），`w2g/b3-on-v2` 已在远端，`afba86e0` 就是它的顶。

## 实读

**激活与补报（`FP-IS-14`）。**

| | |
| --- | --- |
| 下发 | `PENDING_RESULT`，`recoveryRole: SLOT_CONFIGURATION`，入口无 HTTP 错误 |
| `commandsSent` | `2`，跨连接 `[2, 3]` |
| `commandMessageIds` | 一个 |
| `commandPayloadSha256` | 一个 |
| `resultsReported` | `1` |
| 激活行 | `ACTIVATED`，version 2，`de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` |
| 生效配置行 | 同一个 `activationId`、同一个指纹 |

在途被丢一次，重连后补发逐字节相同的同一行，车只回答一次；两端算出同一个指纹。**这是指纹口径改动之后
第一次在车与服务端指纹一致时跑完整条激活路径**——改动没有把「一致时正常收敛」这条路弄坏。

**告警快照（`FP-IS-15`）。**

```
clientConnectionIds        [1, 2, 3]
fullHandshakeConnectionIds [1, 3]
snapshotConnectionIds      [1, 3]
snapshotSessionGenerations [1, 3]
appliedAcks                2
projectionRowsForThisVehicle 1   (sessionGeneration 3, snapshotSequence 2)
```

带告警快照的连接集合恰好等于带 `CapabilitySnapshot` 的连接集合，恢复重连（连接 2）一份不发；两份快照
都被 ack，投影只留后到的那一份。

## 30 条断言

`FP-IS-00` 与 `FP-IS-06` 的恢复、幂等、授权边界 17 条；`FP-IS-14` 5 条
（`CarriesOneMessageIdOnly`、`ReplayedByteForByteAfterAMidFlightDrop`、`PersistedBeforeItWasSent`、
`ResultReportedByTheVehicle`、`bothEndsComputedTheSameSlotConfigurationFingerprint`）；`FP-IS-15` 6 条
（`PublishedOnTheFullHandshake`、`AppliedAckOnEverySnapshot`、`NotRepublishedOnRecoveryResume`、
`KeptOnlyTheLatestOfSeveralSnapshots`、`IsASingletonPerVehicle`、`CarriesTheGenerationItArrivedIn`）；
加 `noMovementOrExternalSideEffects` 与 `secretScan`。全部 `PASS`。

## 未在本轮证明的

- **`fullG3` 与 `releaseCandidate` 都是 `INCONCLUSIVE`。**这个 runner 只认领 `FP-IS-00`／`06`／`14`／`15`，
  `FP-IS-01`／`02`／`03`／`07` 在本批次没有面。四片 `PASS` 不等于整体 `G3` 通过。
- **指纹不符那条路不在这个 runner 里**，由进程重启那一轮在 `6dc4bc8` 上证。
- **告警内容仍然是空的**（`projectionAlarmCount: 0`）。`OnboardAlarmBoard.Raise` 在车载端产品代码里没有
  调用者，要先决定什么条件算一条告警。
- **篡改过的车没有被修回来**：`ApprovedSlotHardwareFacts` 写死，服务端发不出与篡改后车上一致的版本。
- `protocol-v1.0.0` 这个 tag **尚未打**，`approvalStatus` 是 `SUPERSEDING_CANDIDATE`。本次绑的是 commit。
