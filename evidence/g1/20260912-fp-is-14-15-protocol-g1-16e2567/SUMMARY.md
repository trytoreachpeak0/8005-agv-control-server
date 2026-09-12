# 协议 `G1`：`fp/v2-candidate@16e2567` **`PASS`**

2026-09-12 在控制端跑的一次协议 `G1`，退出码 0，`failures` 为空。

**它取代 `../20260910-fp-is-14-15-protocol-g1/` 作为现行的协议 `G1` 证据。**那一份原样保留、一字未改——
它对 `f6ee75d` 仍然成立，只是 `f6ee75d` 已经不是两端绑定的协议候选。

## 为什么要重跑

协议候选 `fp/v2-candidate` 是在协议仓 `main` 改为单人签名（`dff1686`）之前分出的，带着两人签名的旧规则：
`tools/g1-validate.mjs` 要求 `size===2`，attestation schema 要求 `approvals` 恰好 2 项。照那个候选发布
`protocol-v1.0.0`，G1 会拒掉一份只有产品负责人一人签名的 attestation。产品负责人 2026-09-12 批准把单人规则
移植到候选、并重跑全部门禁，移植提交是协议仓 `16e2567`。

这三个文件连同治理文档、`CLAUDE.md` 都在 content manifest 里，所以 manifest 哈希变了：

| 项 | `f6ee75d` | `16e2567` |
| --- | --- | --- |
| content manifest（`manifest/release.json` 的 SHA-256） | `84f984ea…c42788` | `25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0` |
| `schemaBundleSha256` | `71146c88…` | `225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff` |
| `fileTableSha256` | `33803bc0…` | `5ecae021…` |
| `vectorsSha256` | `51c5aaca…` | 不变 |
| `examplesSha256`、`errorRegistrySha256` | — | 不变 |

manifest 用控制端 node v24.20.0 跑 `tools/finalize-manifest.mjs` 生成。**改动前先在 `f6ee75d` 的原内容上跑过一次，
产出与仓里那份逐字节一致（`84f984ea…`）**，以此排除环境差异。

## 结果

| 项 | 值 |
| --- | --- |
| `status` | `PASS` |
| `candidateManifestSha256` | `25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0` |
| schema／消息／valid 样例／invalid 样例 | 69／63／63／1548 |
| 向量轨迹／切片 | 31／16 |
| `approvalAttestationStatus` | `PENDING`（`TRACKED_TEMPLATE`） |

与 `f6ee75d` 那一次的计数逐项相同：移植没有增删任何消息、schema、样例或向量。

## 装置

从本地协议仓 `C:\Users\szy\8005-b3\proto-gov`（`fp/v2-candidate`，比 `origin` 多出 `16e2567`，**本次运行时尚未推送**）
`git clone -b fp/v2-candidate` 出普通克隆 `C:\Users\szy\8005-b3\proto-g1-16e2567`，`.git` 是目录，不是 linked worktree。
`corepack pnpm install --frozen-lockfile` 之后 `corepack pnpm g1`。完整记录在 `run.txt`。

## 结果确定性

跑完之后克隆里 `git status` **没有输出**：门禁写出的 `evidence/g1-result.json` 与 `16e2567` 提交进去的那一份逐字节相同。
`checkedAt` 是脚本常量，所以这比的是判定内容。本目录的 `g1-result.json` 是从克隆里拷出的那一份。

## 未在本轮证明的

- **没有任何人签过名。**`attestations/` 下只有模板，`protocol-v1.0.0` 尚未打 tag。这次移植只让 G1 能够接受
  一份单人签名的 attestation，**签名本身仍须产品负责人亲自完成，AI 和 CI 不能批准**。
- **`G1` 不看任何实现。**两端要跟上新身份，靠的是服务端 `6369616` 与车载端 `e30d421` 两个提交；
  两端的行为由同日重跑的 G2 与 G3 证明。
- **协议仓的候选生成器还没跟上。**`8005-agv-program` 里 `.scratch/wire-to-gate-ai-implementation-kit/` 的
  生成器模板仍是两人签名，用它重新生成候选会把这次改动冲掉。
