# `CONTROL_SERVER_G2`：`FP-IS-15`（`eefb3a8`）

**它取代 `../20260910-fp-is-14-15-v2-message-plane-6dc4bc8/FP-IS-15/` 作为 `FP-IS-15` 服务端这一半的现行证据。**
那一份原样保留、一字未改——它对 `6dc4bc8` 仍然成立。同目录下 `FP-IS-14` 那份**不受影响、仍是现行证据**：
`eefb3a8` 没有改 `FP-IS-14` 名下的任何代码或测试。

## 为什么要重跑

L2 场景 `onboard-alarm-snapshot-dashboard` 第一次运行抓到一个产品缺陷：车断开会话之后，看板一直显示它失联前的
最后一批告警（`../../l2/20260910-batch3-onboard-alarm-snapshot-dashboard-001/`）。修复是 `eefb3a8`，改的是
`OnboardAlarmProjectionStore`——`FP-IS-15` 的服务端代码。缺陷单：
`docs/defects/20260910-dashboard-kept-showing-a-dead-vehicles-last-alarms.md`。用户 2026-09-10 批准在 `eefb3a8`
上重跑 `FP-IS-15` 的门禁。

## 结论

| 片 | 状态 | `selectedTestCount` | `testExitCode` | 向量 |
| --- | --- | --- | --- | --- |
| `FP-IS-15` | **`PASS`** | 12 | 0 | `CV-ONBOARD-ALARM-SNAPSHOT` |

绑定 `eefb3a8802664623fdde9dd7bdc759ea5b61a5b0` ＋ `protocol-v1.0.0@f6ee75defe6e2d18f63f4082bee445dbb678ab1b`，
`protocolManifestSha256` 为 `84f984ea…`。从 `fp-b3` 本地克隆出的 `eefb3a8` 干净检出运行，`-ProtocolManifest`
指向协议仓 `fp/v2-candidate` 的本地克隆。同一 commit 上全量测试 `697 passed / 0 failed`。

## 与 `6dc4bc8` 那份的差

11 条 → 12 条，只多一条，一条没少：

- `OnboardAlarmProjectionTests.AReadyRowWhoseSessionHasGoneQuietShowsTheReasonRatherThanItsLastKnownAlarms`
  ——行还在、还是 Ready、只是这一代会话安静了；另含上一代心跳不替新一代说话。

## 未在本轮证明的

- `CONTROL_SERVER_G2` 只证服务端这一半。「断线 → 看板显示失联 → 重连恢复」跨进程那一段是 L2
  （`../../l2/20260910-batch3-onboard-alarm-snapshot-dashboard-002/` 本地单次 PASS，CI 三连跑另计）。
- 车队会话卡片同一类问题没有修，见缺陷单「仍然开着的」。
- `protocol-v1.0.0` 这个 tag **尚未打**，`protocolApprovalStatus` 是 `SUPERSEDING_CANDIDATE`。本次绑的是 commit。
