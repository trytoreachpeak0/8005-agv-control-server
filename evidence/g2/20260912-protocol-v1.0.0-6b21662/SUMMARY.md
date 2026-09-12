# `CONTROL_SERVER_G2`：已发布的 `protocol-v1.0.0` 上十片全部 **`PASS`**（`6b21662`）

**它取代以下两份，作为这十片服务端这一半的现行证据：**

- `FP-IS-00`～`07`：`../20260912-fp-is-00-07-single-owner-6369616/`
- `FP-IS-14`／`15`：`../20260912-fp-is-14-15-single-owner-6369616/`

那两份绑的是协议候选 `16e2567`，原样保留、一字未改。

## 为什么要重跑

2026-09-12，产品负责人决定：发布批准也可以由他授权的 AI agent 给出，并据此改了协议治理规则。协议候选随之变为 `9f22db8`，
attestation schema 新增了 `approverKind`／`authorizedBy`，content manifest 由 `25fd6689…` 变为 `a0e1deed…`。同日 `9f22db8` 正式发布为
`protocol-v1.0.0`，批准由 AI 给出，授权人为产品负责人，详见 `../../g1/20260912-protocol-v1.0.0-release-9f22db8/`。服务端在 `6b21662`
上绑定这个已发布身份，`approvalStatus` 由 `SUPERSEDING_CANDIDATE` 改为 `APPROVED_RELEASE`。

## 结论

| 片 | 状态 | `selectedTestCount` | `testExitCode` |
| --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 59 | 0 |
| `FP-IS-01` | **`PASS`** | 69 | 0 |
| `FP-IS-02` | **`PASS`** | 18 | 0 |
| `FP-IS-03` | **`PASS`** | 27 | 0 |
| `FP-IS-04` | **`PASS`** | 10 | 0 |
| `FP-IS-05` | **`PASS`** | 12 | 0 |
| `FP-IS-06` | **`PASS`** | 38 | 0 |
| `FP-IS-07` | **`PASS`** | 19 | 0 |
| `FP-IS-14` | **`PASS`** | 25 | 0 |
| `FP-IS-15` | **`PASS`** | 12 | 0 |

十份 `gate-result.json` 的身份逐字段一致：

| 字段 | 值 |
| --- | --- |
| `implementationCommit` | `6b21662c60a2e13aaf86043b146d3d886cf91dd1` |
| `protocolTag` | `protocol-v1.0.0` |
| `protocolRepositoryCommit` | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| `protocolManifestSha256` | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| `protocolSchemaBundleSha256` | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| `protocolApprovalStatus` | **`APPROVED_RELEASE`** |

**这是第一次有服务端 G2 证据绑定已批准的协议发布**，此前所有 v2 线证据的 `protocolApprovalStatus` 都是 `SUPERSEDING_CANDIDATE`。
十片的测试条数与 `16e2567` 那一轮逐片相同：这次改动只涉及身份常量、vendor 副本，以及一条改名的架构测试，没有进入任何切片。

## 装置

从 `fp-b3` 本地 `git clone -b fp/b3-on-v2` 出干净克隆 `C:\Users\szy\8005-b3\control-g2-6b21662`，HEAD 为 `6b21662`，跑完 `git status` 仍然干净；
同一克隆上 Release 全量测试 `699 passed / 0 failed`。`-ProtocolManifest` 指向协议普通克隆 `C:\Users\szy\8005-b3\proto-rel-9f22db8`，
该克隆带着 `protocol-v1.0.0` 这个 tag，跑完同样干净。十片由一个 pwsh 脚本文件依次调用 `scripts/test-wire-to-gate.ps1 -Gate G2`。

## 未在本轮证明的

- `CONTROL_SERVER_G2` 只证服务端这一半。车载端十片在车载端仓同日的 `evidence/g2/20260912-protocol-v1.0.0-<commit>/`。
- 发布批准本身不在本证据里复核，见同日 G1 证据。
