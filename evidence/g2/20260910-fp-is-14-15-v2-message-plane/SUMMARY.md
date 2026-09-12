# `CONTROL_SERVER_G2`：`FP-IS-14`／`FP-IS-15`（`8c0e293`）

批次 3 搬到协议 v2 线、并把消息 7／8／9 与 `CapabilitySnapshot` 指纹补齐之后，这两个切片第一次
具备被 G2 证明的条件。**这是第一次证明，不是沿用任何既有结论。**v0.3.0 线上批次 3 的十三票从来没有
线上消息面，`FP-IS-14`／`FP-IS-15` 在那条线上没有、也不可能有 G2 证据。

## 运行类型

纯 tier 1 / G2。**不动车、不建单、不使用任何现场凭据、未触碰已安装服务。**
2026-09-10 运行，`.NET SDK` 由 `global.json` 钉死。

## 结论

| 项 | 结果 |
| --- | --- |
| 全量测试（Release，`ControlServer.Tests`） | `682 passed / 0 failed / 0 skipped` |
| 两片 `CONTROL_SERVER_G2` | **两份 `gate-result.json` 全部 `PASS`** |
| 绑定 | `8c0e2932d25b45a4a8056eca3298a7e2eaa6bc69` ＋ `protocol-v1.0.0@f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |

| 片 | `selectedTestCount` | `testExitCode` | 向量 |
| --- | --- | --- | --- |
| `FP-IS-14` | 17 | 0 | `CV-SLOT-CONFIGURATION-ACTIVATION` |
| `FP-IS-15` | 11 | 0 | `CV-ONBOARD-ALARM-SNAPSHOT` |

## 这两片这一轮到底被证了什么

`CONTROL_SERVER_G2` 证的是**服务端这一半**在协议 v2 身份下交得出这两个切片的证据，不是两端合起来
的行为。合起来那一半是 `G3`，本轮**没有跑**。

`FP-IS-14`（17 条）覆盖：激活命令按 `RELIABLE` 出去且落库在上线之前；`UNKNOWN` 不被记成成功且生效
配置一个字段不动；同一次激活补报两次只收敛一次；重连时未结的激活按 `SLOT_CONFIGURATION` 补发而已结
的不补发；`CapabilitySnapshot` 报上来的指纹与服务端认定的那一版核对，不一致时拒收快照并回
`SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`，能力修订号因此不被采纳，那台车取不到业务就绪。

`FP-IS-15`（11 条）覆盖：告警快照按 `(会话代, 序号)` 采纳，车重启后序号回到 1 的那一份仍被采纳，
同一代内序号不前进仍被忽略；后一份整体取代前一份；`subjectType` 认不出来的告警落到看板而不是消失；
告警码全程不经过协议那本封闭错误码注册表。

## 未在本轮证明的

- **`G1`、`G3` 均未运行。**本轮范围是两道 G2，用户 2026-09-10 决定。
- 两端指纹算法一致这件事，本仓这一侧只能钉一个固定值
  （`SlotConfigurationActivationTests.TheCanonicalFingerprintOfTheSharedExampleIsTheValueTheOnboardSideAlsoComputes`）。
  两个仓互相看不见，真正的两端一致要等 `G3`。
- `protocolApprovalStatus` 是 `SUPERSEDING_CANDIDATE`，`protocol-v1.0.0` 这个 tag **尚未打**
  （`tagExists: false`）。规格 6.6 第 6 条要两名产品负责人 attestation ＋ 注释 tag，两件都没发生。
  这两份 G2 绑的是 commit，不是 tag。
