# `CONTROL_SERVER_G2`：`FP-IS-14`／`FP-IS-15`（`6dc4bc8`）

**它取代 `../20260910-fp-is-14-15-v2-message-plane/`（`8c0e293`）作为这两片服务端这一半的现行证据。**
那一份原样保留、一字未改——它当时是真的 `PASS`，只是它证的行为后来被改掉了。

## 为什么要重跑

`8c0e293` 那份 `FP-IS-14` 证的是：`CapabilitySnapshot` 报上来的指纹与服务端认定的不一致时**拒收快照**、
回 `SLOT_CONFIGURATION_FINGERPRINT_MISMATCH`。之后按用户 2026-09-10 定的口径，这条行为被改掉了：

| 提交 | 改了什么 |
| --- | --- |
| `b2452d3` | 激活发起入口 `POST /api/governance/v1/slot-configuration-activations` |
| `cd379a7` | 指纹不符不再拒绝会话：回 ack、会话照建、就绪降为不就绪；车离线时下发回 202 |
| `6b75141` | 新会话清空上报指纹；车还没报不算指纹不符 |
| `6dc4bc8` | 指纹不符的原因码加进就绪映射表，原样上线 |

所以那份证据证的是一个 HEAD 上已经不存在的行为，不能再当作 `FP-IS-14` 的 G2。`FP-IS-15` 选中的测试没有
被这四个提交改动，但它绑的实现 commit 同样不是现行产品代码，一并重跑，让四道门禁绑在同一个服务端 commit
上——同日进程重启那一轮 `G3`（`../../g3/20260910-fp-is-14-fingerprint-mismatch-mapped/`）绑的也是
`6dc4bc8`。

## 结论

| 片 | 状态 | `selectedTestCount` | `testExitCode` | 向量 |
| --- | --- | --- | --- | --- |
| `FP-IS-14` | **`PASS`** | 25 | 0 | `CV-SLOT-CONFIGURATION-ACTIVATION` |
| `FP-IS-15` | **`PASS`** | 11 | 0 | `CV-ONBOARD-ALARM-SNAPSHOT` |

绑定 `6dc4bc8f50472301027bb603d99b8c386bbe0c72` ＋ `protocol-v1.0.0@f6ee75defe6e2d18f63f4082bee445dbb678ab1b`，
`protocolManifestSha256` 为 `84f984ea…`，与 `G1`、两轮 `G3` 逐字相同。

从 `fp-b3` 本地克隆出 `6dc4bc8` 的干净检出运行（`implementationCommit` 读的是被测检出自己的
`rev-parse HEAD`，所以不能在 HEAD 为 `7253bac` 的工作树里跑）；`-ProtocolManifest` 指向从协议仓
`fp/v2-candidate` 本地克隆出的 `f6ee75d`。同一 commit 上全量测试 `690 passed / 0 failed`
（在 `7253bac` 上跑的，它与 `6dc4bc8` 之间没有 `src`／`tests` 改动）。

## 被选中的测试与 `8c0e293` 那份的差

`FP-IS-14` 从 17 条变成 25 条：

- **去掉 1 条**：`CapabilitySnapshotFingerprintTests.AReportedFingerprintThatDisagreesIsRefusedWithTheStableCodeAndLeavesTheVehicleUnready`
  ——正是被改掉的那条行为。
- **新增 9 条**：
  - `CapabilitySnapshotFingerprintTests.AReportedFingerprintThatDisagreesLeavesTheVehicleUnreadyButStillReachable`
  - `CapabilitySnapshotFingerprintTests.AVehicleThatHasNotReportedYetIsNotNamedAsAFingerprintMismatch`
  - `SessionReadinessReasonCodesTests.AFingerprintMismatchGoesOnTheWireAsItself`
  - `SlotConfigurationActivationEndpointsTests` 的 6 条（503／401／422／409／202 待结果／离线 202）

`FP-IS-15` 的 11 条与上一份逐条相同。

## 同日多出来的那个目录

`../20260910-fp-is-14-15-v2-message-plane-6dc4bc8$s/` **是同一次操作的调用笔误，不是另一轮门禁。**
第一次调用在 bash 里把 `\$s` 写成了字面的 `$s`：`FP-IS-14` 跑进了那个名字（`PASS`，25 条，
`6dc4bc8`，与本目录那份同 commit 同结论），`FP-IS-15` 因为输出目录已存在被脚本拒绝、没有运行。
随后改用 pwsh 把两片重新跑进本目录。那个目录原样保留，没有删、没有改名——它是一次真实运行的产物。

## 未在本轮证明的

- `CONTROL_SERVER_G2` 只证服务端这一半，两端合起来的行为是 `G3`。
- `protocolApprovalStatus` 是 `SUPERSEDING_CANDIDATE`，`protocol-v1.0.0` 这个 tag **尚未打**。发布需要
  attestation 加产品负责人本人签名（2026-09-08 起为单人签；`8c0e293` 那份写的「两名」是过时的表述），
  AI 与 CI 不能批准。本次绑的是 commit，不是 tag。
