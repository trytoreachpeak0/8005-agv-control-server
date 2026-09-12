# `CONTROL_SERVER_G2`：`FP-IS-00`～`07` 在协议 `16e2567` 上重证（`6369616`）

**它取代以下两份，作为这八片服务端这一半的现行证据：**

- `../20260909-fp-is-00-07-v2-recertification/`：八片，绑 `a143c9c`，协议 `f6ee75d`
- `../20260909-fp-is-02-reconcile-empty-final-state/`：只有 `FP-IS-02`，绑 `3f62647`，协议 `f6ee75d`

两份都原样保留、一字未改，它们对各自绑的 commit 与协议 `f6ee75d` 仍然成立。

## 为什么要重跑

产品负责人 2026-09-12 批准把单人签名规则移植到协议候选，并重跑全部门禁。移植后协议候选是 `16e2567`，content manifest 从 `84f984ea…`
变为 `25fd6689…`，服务端在 `6369616` 跟上了这个身份。票 17 这八片的 G2 绑的是旧 manifest，对现行服务端不再成立，所以在
签 `protocol-v1.0.0` 之前统一重跑一次。

## 结论

| 片 | 状态 | `selectedTestCount` | `testExitCode` | 与 09-09 那份的差 |
| --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 59 | 0 | 相同 |
| `FP-IS-01` | **`PASS`** | 69 | 0 | 相同 |
| `FP-IS-02` | **`PASS`** | 18 | 0 | 17 → 18，多出 `3f62647` 补的 `AuthorizedLoadCancellationReconcilesOnlyWhenEverySlotIsProvenEmpty` |
| `FP-IS-03` | **`PASS`** | 27 | 0 | 相同 |
| `FP-IS-04` | **`PASS`** | 10 | 0 | 相同 |
| `FP-IS-05` | **`PASS`** | 12 | 0 | 相同 |
| `FP-IS-06` | **`PASS`** | 38 | 0 | 相同 |
| `FP-IS-07` | **`PASS`** | 19 | 0 | 相同 |

八份 `gate-result.json` 的身份逐字段一致：`implementationCommit` 为 `63696161d036a4907a39f8597fd64cc6e4c755fd`，`protocolRepositoryCommit` 为
`16e2567a7033883f00fc999f7fa08f954dd13a26`，`protocolManifestSha256` 为 `25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0`，
`protocolApprovalStatus` 为 `SUPERSEDING_CANDIDATE`。与同日 `FP-IS-14`／`15` 那份
（`../20260912-fp-is-14-15-single-owner-6369616/`）绑的是同一个服务端 commit。

## 装置

与同日 `FP-IS-14`／`15` 那份用的是同一份干净克隆 `C:\Users\szy\8005-b3\cs-g2-6369616`（HEAD 为 `6369616`），跑完 `git status` 仍然干净；
同一克隆上 Release 全量测试 `699 passed / 0 failed`。`-ProtocolManifest` 指向协议普通克隆 `C:\Users\szy\8005-b3\proto-g1-16e2567`，
跑完同样干净。八片由一个 pwsh 脚本文件依次调用 `scripts/test-wire-to-gate.ps1 -Gate G2`，路径不经过任何 shell 字符串转义。

`6369616` 在 `fp/b3-on-v2` 上，包含 `fp/v2-impl` 的全部内容与批次 3 的改动。批次 3 没有改这八片名下的代码，所以 `FP-IS-02` 之外
各片的测试条数都与 09-09 相同。

## 未在本轮证明的

- `CONTROL_SERVER_G2` 只证服务端这一半。车载端八片在车载端仓同日的 `evidence/g2/20260912-fp-is-00-07-single-owner-ad0e507/`。
- G3：`FP-IS-00`／`06` 由同日两个 G3 runner 重跑；`FP-IS-04`／`05` 由需求线路 G3 runner 重跑；`FP-IS-01`／`02`／`03`／`07`
  在任何 G3 runner 里都没有断言，按 2026-09-09 的裁定不出证据。
- `protocol-v1.0.0` 这个 tag **尚未打**。本次绑的是 commit。
