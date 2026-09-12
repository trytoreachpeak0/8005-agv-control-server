# `CONTROL_SERVER_G2`：`FP-IS-14`／`FP-IS-15`（`6369616`，协议 `16e2567`）

**它取代以下两份，作为这两片服务端这一半的现行证据：**

- `FP-IS-14`：`../20260910-fp-is-14-15-v2-message-plane-6dc4bc8/FP-IS-14/`
- `FP-IS-15`：`../20260910-fp-is-15-alarm-liveness-eefb3a8/FP-IS-15/`

那两份原样保留、一字未改——它们对各自绑定的服务端 commit 与协议 `f6ee75d` 仍然成立，只是两端已经不再绑定那个协议候选。

## 为什么要重跑

两个原因叠在一起，产品负责人 2026-09-12 批准一次重跑：

1. **协议候选换了。**`fp/v2-candidate` 补上了单人签名规则（协议仓 `16e2567`），content manifest 从
   `84f984ea…` 变为 `25fd6689…`，`schemaBundleSha256` 从 `71146c88…` 变为 `225a8334…`。服务端每条报文都带着
   这组身份，所以所有绑旧 manifest 的 G2 对现行服务端都不再成立。服务端跟上的提交是 `6369616`。
2. **`FP-IS-15` 的服务端代码改了。**`5f7a34e` 让看板集中显示车辆的全部告警（REQ-0270 原文），不再只显示
   与车当下无关的那一类。

## 结论

| 片 | 状态 | `selectedTestCount` | `testExitCode` | 向量 |
| --- | --- | --- | --- | --- |
| `FP-IS-14` | **`PASS`** | 25 | 0 | `CV-SLOT-CONFIGURATION-ACTIVATION` |
| `FP-IS-15` | **`PASS`** | 12 | 0 | `CV-ONBOARD-ALARM-SNAPSHOT` |

两份 `gate-result.json` 的身份逐字段一致：

| 字段 | 值 |
| --- | --- |
| `implementationCommit` | `63696161d036a4907a39f8597fd64cc6e4c755fd` |
| `protocolRepositoryCommit` | `16e2567a7033883f00fc999f7fa08f954dd13a26` |
| `protocolManifestSha256` | `25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0` |
| `protocolSchemaBundleSha256` | `225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff` |
| `protocolVectorsSha256` | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df`（不变） |
| `integrationSliceIndexSha256` | `71e0a63d…`（不变） |
| `protocolApprovalStatus` | `SUPERSEDING_CANDIDATE` |

`candidateManifestSha256` 与同日协议 `G1`（`../../g1/20260912-fp-is-14-15-protocol-g1-16e2567/`）一致。

## 装置

从 `fp-b3` 本地 `git clone -b fp/b3-on-v2` 出干净克隆 `C:\Users\szy\8005-b3\cs-g2-6369616`，HEAD 为 `6369616`，
跑前跑后 `git status` 都干净。`-ProtocolManifest` 指向 G1 用的那份协议克隆
`C:\Users\szy\8005-b3\proto-g1-16e2567\manifest\release.json`。同一克隆上 Release 全量测试 `699 passed / 0 failed`。

## 与上一份的差

- `FP-IS-14`：25 条对 25 条。`6369616` 相对 `6dc4bc8` 没有改 `FP-IS-14` 名下的代码，只换了协议身份。
- `FP-IS-15`：12 条对 12 条，条数相同，但其中两条的断言变了（`5f7a34e`）：
  `OnboardAlarmProjectionTests.EveryAlarmOfAVehicleReachesTheDashboardWhateverItsRelationToTheVehicle`
  （原来断言只剩 Fleet 那条）与 `OnboardAlarmSnapshotWireTests` 的路由测试，现在都要求四种 scope 的告警全部可见。

## 同日多出来的那个目录

`../20260912-fp-is-14-15-single-owner-6369616$s/` **是同一次操作的调用笔误，不是另一轮门禁**，与 2026-09-10 的
`6dc4bc8$s` 同一个原因：bash 调用里给路径写的反斜杠转义被工具参数先解码了一层，`$s` 成了字面文字。
`FP-IS-14` 跑进了那个名字（`PASS`，25 条，`6369616`，与本目录那份同 commit 同结论），`FP-IS-15` 因为输出目录
已存在被脚本拒绝、没有运行。随后改用一个 pwsh 脚本文件把两片重新跑进本目录，路径不再经过任何 shell 字符串转义。
那个目录原样保留，没有删、没有改名。

## 未在本轮证明的

- `CONTROL_SERVER_G2` 只证服务端这一半。车载端报上来的真实告警能不能在看板上看到，是 `G3` 与 L2 的事。
- `protocol-v1.0.0` 这个 tag **尚未打**，没有任何人签过 attestation。本次绑的是 commit。
- 票 17 的 `FP-IS-00`～`07` v2 重证绑的也是旧 manifest，同样不再对现行身份成立；本轮范围只有批次 3 的两片，
  那八片没有重跑。
