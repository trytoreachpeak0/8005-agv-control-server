# `CONTROL_SERVER_G2`：`06b65688` 上十片全部 **`PASS`**（已发布的 `protocol-v2.0.0`）

批次 5 出口（control-server#90）第 2 步。**它取代 `../20260914-protocol-v1.0.0-052759bc/`，作为这十片服务端这一半的现行证据。**
那一份绑的是 `protocol-v1.0.0`，按规格第 6.5 节在 `protocol-v2.0.0` 发布后不再是现行证据，原样保留、一字未改。

按用户 2026-09-17 决定，门禁不再逐次询问；本轮在调度会话放行的真装置时段内跑。

## 结论

| 片 | 状态 | `selectedTestCount` | `testExitCode` | 出站 schema 校验行数 | 未登记违约 | 与 `052759bc` 相比 |
| --- | --- | --- | --- | --- | --- | --- |
| `FP-IS-00` | **`PASS`** | 84 | 0 | 301 | 0 | 59 → 84 |
| `FP-IS-01` | **`PASS`** | 90 | 0 | 94 | 0 | 71 → 90 |
| `FP-IS-02` | **`PASS`** | 144 | 0 | 595 | 0 | 25 → 144 |
| `FP-IS-03` | **`PASS`** | 35 | 0 | 101 | 0 | 31 → 35 |
| `FP-IS-04` | **`PASS`** | 14 | 0 | 110 | 0 | 10 → 14 |
| `FP-IS-05` | **`PASS`** | 25 | 0 | 184 | 0 | 14 → 25 |
| `FP-IS-06` | **`PASS`** | 43 | 0 | 292 | 0 | 40 → 43 |
| `FP-IS-07` | **`PASS`** | 75 | 0 | 357 | 0 | 22 → 75 |
| `FP-IS-14` | **`PASS`** | 26 | 0 | 34 | 0 | 25 → 26 |
| `FP-IS-15` | **`PASS`** | 34 | 0 | 166 | 0 | 12 → 34 |

「出站 schema 校验」是 control-server#85（批次5-24）加的门禁：该片测试发出的每一行出站报文都按 `protocol-v2.0.0` 的 schema 校验，
摘要在每份 `gate-result.json` 的 `schemaConformance`，十片 `linesInViolation` 与 `knownViolationsMatched` 都是 0。
`FP-IS-02` 的 `schema-coverage.json` 里 `SublotRejected` 有 18 行被校验（`byMessageType.SublotRejected.product = 18`）；其余九片的测试不发这条消息。

十份 `gate-result.json` 的身份逐字段一致：

| 字段 | 值 |
| --- | --- |
| `implementationCommit` | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
| `protocolTag` | `protocol-v2.0.0` |
| `protocolRepositoryCommit` | `86575456c847041515b7b75e8851a00e0d939804` |
| `protocolManifestSha256` | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| `protocolSchemaBundleSha256` | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| `protocolApprovalStatus` | **`APPROVED_RELEASE`** |

成对写法：`(AGV_FULL_PRODUCT, 3)`，发布 `2.0.0`。`ProtocolVersion` 3 与 `WIRE_TO_GATE_MVP 0.3.0` 同数，身份比较一律用上表的完整 `ProtocolReleaseIdentity`。

`06b65688` 相对 `e0f26b37`（本票之前的 `fp/v2-impl` 顶端、G3 共享绑定的 `ControlServerCommit`）只改了 `scripts/run-staged-g3.ps1`
与 `scripts/run-demand-bearing-g3-vectors.ps1` 的绑定，`src/`、`tests/` 零差异。

## 测试条数的变化

按 `.trx` 测试名逐片对比 `052759bc` 那一轮：**按方法名没有测试被删。**若按完整名比较，`FP-IS-00`／`01`／`02`／`03`／`06`／`07` 会显示
`ControlServer.Tests.JourneyRuntimeWorkerTests.*` 的若干条「消失」，那是 control-server#130（PR #135）把这个测试类拆成多个类造成的类名变化，
方法都还在。增加的是批次 5 各票（以及同期合入的 control-server#131、#137、#138、#139、#141、#142 与 PR #62）新增的测试。

## 装置

`C:\Users\szy\Desktop\8005-workspace-v2\repos\8005-agv-control-server` detached 在 `06b65688`，跑完 `git status` 仍干净；
`-ProtocolManifest` 指向协议克隆 `repos\8005-agv-protocol\manifest\release.json`（HEAD 即 `protocol-v2.0.0` 指向的 `86575456`，跑完干净）。
十片依次调用 `scripts/test-wire-to-gate.ps1 -Gate G2 -Slice <id> -Output <本目录>/<id>`，每片的控制台输出在 `<id>.console.log`。
单片耗时 52～126 秒，合计约 15 分钟。

## 未在本轮证明的

- 只证服务端这一半。车载端十片在车载端仓 `w2g/fp-v2-impl` 的证据目录（经小 PR 合入）。
- 联合 G3 见同日 `../../g3/` 下 `06b65688` 的四个目录。
- 出站门禁只检查出站报文；入站普查与在 L2 里校验车辆发来的行都没有做（program#61 Q4 的范围）。
