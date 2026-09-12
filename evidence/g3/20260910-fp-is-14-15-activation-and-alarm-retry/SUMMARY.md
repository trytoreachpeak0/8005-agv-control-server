# `G3`：`FP-IS-00` / `FP-IS-06` / `FP-IS-14` / `FP-IS-15` 四片 **全部 `PASS`**

`STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT`，`assuranceLevel` 为 `STAGED_REBUILD`，
28 条断言全绿，四份 `gate-result.json` 的 `formalSlicePass` 都是 `true`。
真实三进程：`ControlServer.Host`、`SQCD_8005AGV_Simulator`、`SQCD.Agv.Wpf`，全部从各自 commit
的一次性克隆重建。

## 这一轮真正新增的那一条

```
bothEndsComputedTheSameSlotConfigurationFingerprint   PASS
```

**「两端指纹算法一致」第一次被端到端证明。**在这之前，两个仓各自钉一个字面量
（`de93ca3d9eda…`），那只能说各自没有偏离一个写下来的值，说不了两边一致——两个仓互相看不见。

这一次说得了，理由在协议本身：**63 条消息里没有任何一条携带仓位 IO 绑定**（查过，
`unlockOutput*` 的命中全是运行时状态）。所以消息 7 带的是版本名与指纹，车必须**自己算**手上那份
配置的摘要来比，相等才切换。而 `ActiveSlotConfigurations` 这张表只在一次激活收敛于「车报成功」
时才写。

本次实读：

| | |
| --- | --- |
| `slotConfigurationActivations` 那一行 | `state: ACTIVATED`，`fingerprint: de93ca3d9eda7b619dd3ea2e8824f8592a3471b11ff723eba3dbc12ea6f69da9` |
| `activeSlotConfigurations` 那一行 | 同一个 `activationId`，同一个 `fingerprint` |
| 命令 | 1 条 `SlotConfigurationActivationCommand`（server → client） |
| 结果 | 1 条 `SlotConfigurationActivationResult`（client → server） |

那个 `de93ca3d…` 正是两个仓各自钉着的那个字面量。**两个实现、两个进程、各自的副本，算出了同一个
摘要。**

有一点值得单说：车载端在这个装置里用的是它**自己仓库里的默认** `slotIoMapping`
（`doChannel` 0..7、`lockFeedbackDiChannel` 0..7、`lightCurtainDiChannel` 8..15），经它自己的
`DO{n+1}`／`DI{n+1}` 渲染之后恰好等于服务端 `ApprovedSlotHardwareFacts` 的
`DO1..DO8`／`DI1..DI8`／`DI9..DI16`。**runner 没有为了让它们相等而改过车的配置**——这次比对是真
的比，不是调出来的。

## 四片各自被证了什么

`FP-IS-14`（4 条）：命令按 `RELIABLE` 在活着的那条会话上发出去且**只发一次**；那条命令的
`messageId` 早就写在数据库里那一行的 `commandMessageId` 上（`durableBeforeSend`，读出来的，不是
信的）；车报回了结果（REQ-0264：没有结果就没有结论，绝不假定成功）；两端指纹一致。

`FP-IS-15`（5 条）：完整握手发一份告警快照并被一对一 ack；**恢复重连一份都不重发**；一车一行；
那一行带着它到达时的会话代。

`FP-IS-00`（9 条）与 `FP-IS-06`（7 条）：与 2026-09-09 那一轮同样的恢复与幂等断言，这一次在
带上批次 3 与激活入口之后重证一遍，未见回归。

## 被保留的那一份失败

**`../20260910-fp-is-14-15-activation-and-alarm/` 是 `INCONCLUSIVE_RUNNER_ERROR`，原样保留。**

那一次不是断言红，是这台机器内存不够：

```
field-ops-seed-approved-facts exited with code -532462766
logs/field-ops-seed-approved-facts.log:  Out of memory.
```

15.6 GB 的机器当时只剩 996 MB——publish 阶段留下的十几个 MSBuild node 各占 100 MB 上下。
重跑前 `dotnet build-server shutdown` 并设 `MSBUILDDISABLENODEREUSE=1`，命令其余部分一字未改。

它的 `slices/` 下四片都写了 `gate-result.json`，状态不是 `PASS`——**一次跑不出结论的运行仍然要
留下它跑不出结论这件事**，这正是 `INCONCLUSIVE_RUNNER_ERROR` 与「没有证据」的区别。

## 装置里两处与既往不同

**一、车载端与协议仓都是从本地仓克隆的，不是 GitHub。**`-OnboardRepository` 指向
`C:\Users\szy\8005-b3\hmi-b3`，`-OnboardRemoteRef origin/w2g/b3-on-v2`；协议仓是
`C:\Users\szy\8005-b3\proto-g1`（`f6ee75d` 的普通克隆）。`w2g/b3-on-v2` 当时尚未推送到远端。

`RemoteRef` 断言没有被取消——克隆源仍然必须把 `afba86e0` 认作某个分支的顶，所以证据绑不上一个只
以游离对象形式递给 runner 的 commit。**但「远端」在这一次指的是本地那个仓。**

**二、激活入口是这一轮才有的。**服务端环境变量里 `SlotConfigurationActivation__enabled` 打开、
另给一把独立凭据；产品默认是关的。治理前置由 `ControlServer.FieldOps` 的
`seed-approved-facts` 与 `bind-io` 完成——现场 W1 窗口里人做的正是这两件事。

## 未在本轮证明的

- **「车重启后告警序号回到 1 仍被采纳」没有证。**这个 runner 不重启车载端进程，它归
  `run-staged-g3-restart.ps1`，该 runner 已在同日认领了这一条但尚未运行。
- **这一次的告警集是空的**（`projectionAlarmCount: 0`，只到达一份快照），所以带非空内容的投影、
  以及「后一份整体取代前一份」都没有在 `G3` 这一层证到。
- **激活的失败路径没有证。**这一次车接受了指纹。指纹不匹配时车报
  `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`、服务端不写生效配置——那条路只有单元测试与
  `CONTROL_SERVER_G2` 覆盖。
- **补报（`PENDING_RESULT_REPLAY`）没有证。**这一次命令一发即达。断线后未结激活按
  `SLOT_CONFIGURATION` 补发那条路，同样只有单元测试与 G2。
- `protocol-v1.0.0` 这个 tag **尚未打**，attestation 是 `PENDING` / `TRACKED_TEMPLATE`。
  本次绑的是 commit，不是 tag。

## 与同日 `G1` 的关系

协议 `G1` 是 `PASS`，记录在 `../../g1/20260910-fp-is-14-15-protocol-g1/`；这个 runner 内部也自己
跑了一次 G1（`logs/protocol-g1.log`）。`protocolManifestSha256` 是 `84f984ea…`，与那份 `G1` 的
`candidateManifestSha256`、以及两份 `CONTROL_SERVER_G2` 的同名字段逐字相同。
