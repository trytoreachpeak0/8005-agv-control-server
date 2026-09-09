# 2026-09-09 协议 v2 身份下的按片 G3：三个 runner 全过，六份 `gate-result.json`

## 结论

| runner | 运行 ID | 结果 | 断言 |
| --- | --- | --- | --- |
| `run-staged-g3.ps1` | `20260909T101735630Z` | `STAGED_G3_RECOVERY_REPLAY_PASS` | 19 / 19 |
| `run-staged-g3-restart.ps1` | `20260909T102015738Z` | `STAGED_G3_PROCESS_RESTART_PASS` | 20 / 20 |
| `run-demand-bearing-g3-vectors.ps1` | `20260909T101613312Z` | `DEMAND_BEARING_G3_VECTORS_PASS` | 20 / 20 |

三个 `status` 与全部断言值都是 runner **自身发射**的机器可读结果，落在各自的
`run-result.json` 里，本文档只是转述，不是判定来源。三次 `secretLeakFiles` 均为空数组。

按片结论，六份 `gate-result.json`（`schemaVersion` `1.3.0`）：

| 片 | 出证的 runner | `status` | `assuranceLevel` | `formalSlicePass` |
| --- | --- | --- | --- | --- |
| `FP-IS-00` | staged ＋ process-restart | `PASS` | `STAGED_REBUILD` | `true` |
| `FP-IS-06` | staged ＋ process-restart | `PASS` | `STAGED_REBUILD` | `true` |
| `FP-IS-04` | demand-bearing | `PASS` | `DEMAND_BEARING_RESTORE` | `true` |
| `FP-IS-05` | demand-bearing | `PASS` | `DEMAND_BEARING_RESTORE` | `true` |

`FP-IS-01`／`02`／`03`／`07` **本批次没有 G3 面**：四个 G3 runner 里一次都不出现，
按 2026-09-09 的用户裁定**如实记录、不发证据**，见每份 `run-result.json` 的
`classification.slicesWithoutSurfaceThisBatch`。**缺的是一份不存在的证据，不是一份
`INCONCLUSIVE` 的证据**——与票 14「选不中任何测试就拒绝出证，而不是写成绿的」同一条理由。

## 🔴 这次证据里有一条已知豁免，范围必须照下面这样读

`run-demand-bearing-g3-vectors.ps1` 的 `protocolAndBuildIdentityBoundToTheSharedBinding`
在本轮之前是**七项合取**，最后一项问的是被恢复的现场库里记的历史 `protocolCommit`。
用户 2026-09-09 裁定该项为**已知豁免**，票 24 据此把它从合取里拆出来。

**豁免的是这一项，不是这条断言的名字：**

- **豁免**：`baseline.sessionRecoveryRows[0].protocolCommit` 是否等于本次绑定的
  `$ProtocolCommit`。它现在**不再是断言**，改为如实记录在两份 demand-bearing
  `gate-result.json` 与该 runner `run-result.json` 的 `fieldStoreProvenance` 节里。
- **不豁免**：同一条断言余下的六项——运行中服务端报的 `protocolCommit` 与 `protocolTag`、
  被测构建的 `serverBuildCommit`，以及三个非空守卫。**它们仍然是断言，本轮全部通过**。

本轮实测记录：

```json
"fieldStoreProvenance": {
  "protocolCommit": "1531489e42e328f28bfe0c51ed3f8c56e5ce0279",
  "matchesBoundProtocolCommit": false,
  "exemption": "TICKET_17_KNOWN_EXEMPTION_FIELD_STORE_HISTORY"
}
```

`1531489e42e328f28bfe0c51ed3f8c56e5ce0279` 是 `protocol-v0.1.1` 指向的 commit。
恢复的现场库是 2026-08-29 那次已授权现场运行写下的真实状态，它当时说的就是 v0.1.1，
**所以任何 v2 身份的运行对这一项都不可能相等**，除非重新采一次 v2 下的已授权现场运行。
`matchesBoundProtocolCommit` **照实写 `false`**：豁免的意思是这条事实不参与切片判定，
不是证据不再陈述它。

⚠️ **上一轮（`../20260909-v2-identity/`）那次 `DEMAND_BEARING_SLICE_FAIL` 因此不是被「修绿」的。**
那一轮七项揉成一个布尔值，看不出挂的是哪一项；本轮把第七项移出断言之后，
**前六项作为断言全部通过**，同时第七项被量出来仍是 `false`。
换句话说：**上一轮「挂的只是现场库那一项」是推断，本轮才是实测。**
那一轮的红证据保留不动。

## 冻结身份

| 分量 | 值 |
| --- | --- |
| 控制端 | `b46b0727de5aad74e9ffd56709219dd76e75e0b2` |
| 车载端 | `153b70594f75ce945e717afd80be9f6279423080`（`w2g/fp-v2-impl`） |
| slots-simulator | `fb5f7c593742bf98bc3957b8729a38aad5321f28`（`main`） |
| 协议 | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b`（`fp/v2-candidate`） |
| runner／harness | `947b07a5871e501047a2accf3ecd52bc8677fe8a` |
| `protocolManifestSha256` | `84f984eabf17106e92666c415b63100d404e9ec69a9a710dfddf17683cc42788` |
| `integrationSliceIndexSha256` | `71e0a63d49d1973653e1f70addc19c334faff5e53e8597733c1423a7307bd82f` |

`protocolTag` 记 `protocol-v1.0.0`、`protocolApprovalStatus` 记 `SUPERSEDING_CANDIDATE`。
**那个 tag 至今没打**——规格 6.6 要两名产品负责人的 attestation，两件都没发生，
所以身份绑的是候选 commit 而不是 tag。协议 G1 在 staged 那次运行里 `"status": "PASS"`。

开跑前四条 commit 绑定都对着各自的远端核过：车载端 `153b705` = `origin/w2g/fp-v2-impl`、
simulator `fb5f7c5` = `origin/main`、协议 `f6ee75d` = `origin/fp/v2-candidate`。
控制端绑定本轮从 `e3ea250` 移到 `b46b072`（`947b07a`）：两者之间 `src/`、`tools/`、
`Directory.Packages.props`、`global.json` **逐字节相同**，移动不改变被测的字节，
改变的是证据说它证的是哪个 commit。

## ⚠️ `harnessWorktreeCleanAtStart` 在后两份里是 `false`，原因不是代码

三个 runner 是连着跑的，而它们把证据写在工作树里。第一个跑的
（demand-bearing，`runnerWorktreeCleanAtStart: true`）开跑时工作树干净；
后两个开跑时，**前面 runner 刚写出的这个证据目录已经在工作树里了**，
`git status --porcelain` 报 `?? evidence/g3/20260909-graded-per-slice/`，于是记 `false`。

**三次运行期间没有任何未提交的代码改动**：控制端 `b46b072`（票 24 实现）与
`947b07a`（绑定移动）都在开跑前提交完毕。这一位记的是「工作树干净否」，
而 runner 自己的产物也在工作树里，所以连跑时它从第二个起必然为 `false`。
**这是脚本的一个已知形状，本轮不改**（不在票 24 范围）。

## ⚠️ `evidenceFiles` 的 SHA-256 与库里的字节对不上，这批也一样

三份 `run-result.json` 各带一张 `evidenceFiles` 的 SHA-256 表。那张表是**在落库前对盘上的
CRLF 版本**算的，而本仓 `.gitattributes` 的 `* text=auto eol=lf` 在落库时把这些文本文件
规范成 LF，**于是库里的 blob 字节与表里的摘要不相等**。表本身没算错——它与盘上的文件逐位相同，
是落库改了字节。

**这不是本轮引入的**，`../20260904-after-journey-fix/` 与 `../20260909-v2-identity/` 同病，
详见 `8005-agv-program/.scratch/8005-batch-2/issues/17-answer.md` 第八节第 2 条。
用户 2026-09-09 裁定先不处理，**所以这批证据如实带着这个问题入库**，
而不是悄悄改表或改行尾。要核这些摘要，请对**签出到工作树的文件**核，不要对 blob 核。

## 本次不是什么

- **不是「八个切片通过 G3」。** 四片有 G3 面且通过，四片本批次没有 G3 面。
- **不是切片通过。** 一个切片要四道门禁齐全才算通过；本目录只是其中 `G3` 这一道。
- **不是发布候选。** 三份 `run-result.json` 的 `classification` 里
  `fullG3` 与 `releaseCandidate` 都仍是 `INCONCLUSIVE`。
- **不含可沿用的通过结论。** `FP-IS-00`～`07` 是 v2 下的重证，
  `W2G-IS-00`～`07` 的任何旧结论都不能搬过来，也没有旧结论可搬。
- **`assuranceLevel` 的天花板不是 `CANDIDATE_ARTEFACT`。** 本轮两级都是重建或恢复出来的运行，
  **没有任何一次跑的是候选发布产物本身**——那一级至今无人占据。
- **不证明真实硬件。** 三个 runner 都是合成外设、loopback 明文、零位移。

## 同一批的其他材料

- 票 23（分级状态与按片出证）：`8005-agv-program/.scratch/8005-batch-2/issues/23-answer.md`
- 票 24（本轮那次断言拆分）：同目录 `24-demand-bearing-field-store-assertion-split.md`
- 上一轮 G3（改造前的形态，含那次 `DEMAND_BEARING_SLICE_FAIL`）：`../20260909-v2-identity/`
