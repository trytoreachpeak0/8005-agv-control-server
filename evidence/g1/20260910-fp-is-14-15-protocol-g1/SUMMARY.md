# 协议 `G1`：`fp/v2-candidate@f6ee75d` **`PASS`**

2026-09-10 在控制端跑的一次协议 `G1`，退出码 0，`failures` 为空。

## 它补正了什么

上一轮两份 G2 的 `SUMMARY.md` 都写着「`G1` 没有运行」，并把 `summary.json` 里
`protocol.g1Status: "SKIPPED"` 单独点了出来。**那句话在写下时是准确的，现在不再是。**被补正的两份是：

- `evidence/g2/20260910-fp-is-14-15-v2-message-plane/SUMMARY.md`（本仓）
- `8005-agv-onboard-hmi` 的 `evidence/g2/20260910-fp-is-14-15-v2-message-plane/SUMMARY.md`

那两份目录**未改动**——证据只增不改。这一份是新增的那一份。

## `G1` 到底是什么级别的门禁

**它是协议级的，不是分片的。**`tools/g1-validate.mjs` 一次校验整个候选：清单逐文件字节数与
SHA-256、69 份 schema、63 条消息、63 个有效样例、1548 个无效样例、31 条向量轨迹、16 行切片表。
它**没有 `-Slice` 这个概念**，所以「跑 FP-IS-14 的 G1」这个说法本身不成立。

它对这两片的意义是：**这两片所依赖的协议候选整体成立**——16 行切片表里有它们，消息 7／8／9 的
schema 与样例在那 63／1548 里被校验过。

## 绑定链在这里闭合

| 出处 | `manifestSha256` |
| --- | --- |
| 本次 `G1` 的 `candidateManifestSha256` | `84f984eabf17106e92666c415b63100d404e9ec69a9a710dfddf17683cc42788` |
| `FP-IS-14` 的 `gate-result.json` 的 `protocolManifestSha256` | 同上 |
| `FP-IS-15` 的 `gate-result.json` 的 `protocolManifestSha256` | 同上 |

两份 G2 绑的 `protocolRepositoryCommit` 是 `f6ee75de...`，正是本次 `G1` 的 `HEAD`。
**所以四份 G2 与这份 G1 说的是同一个协议候选**，不是三个碰巧同名的东西。

## 装置：这次用普通克隆，不是 linked worktree

上一轮 `ONBOARD_HMI_G2` 的 `FP-IS-14` 第一次跑出 `FAIL`，根因是协议仓当时以一个 linked worktree
提供，而 `g1-validate.mjs` 的排除规则写作 `p.startsWith(".git/")`，假定 `.git` 是目录；linked
worktree 的 `.git` 是文件，排不掉，于是恰好多出一个条目，`manifest file count` 报 FAIL。

**这次从本地协议仓 `git clone -b fp/v2-candidate` 出一份普通克隆**（`C:\Users\szy\8005-b3\proto-g1`），
`.git` 是目录，那条假失败不再出现。这一步就是上一份 SUMMARY 里说的「换一份普通克隆」那条出路。

顺带纠正一条陈述：工作区根 `CLAUDE.md` 与 `g1.yml` 的注释都写着「控制端没装 Node，`pnpm g1`
从未在这里执行过」。**控制端现在有 Node v24.20.0**（在 `C:\Program Files\nodejs`，不在默认 PATH 上），
pnpm 经 corepack 取得。那条记载是 2026-09-02 的事实，今天不成立。

## 结果确定性：本次运行复现了仓里那一份

`G1` 跑完之后 `git status -- evidence/g1-result.json` **没有输出**——门禁写出的文件与
`f6ee75d`（「协议 v2 候选由生成器一把产出，G1 通过」）提交进去的那一份**逐字节相同**。

`checkedAt` 是脚本里的常量 `2026-09-04T00:00:00Z`，不是运行时刻，所以这个比较是有意义的：
它比的是判定内容，不是时间戳。**因此协议仓无需新提交**，这一轮 `G1` 只增加本目录这份记录。

## 未在本轮证明的

- **`approvalAttestationStatus` 是 `PENDING`**，`approvalAttestationSource` 是 `TRACKED_TEMPLATE`
  ——`attestations/` 下只有 `release-approval.template.json` 这个模板，**没有任何人签过名**。
  `protocol-v1.0.0` 这个 tag 因此尚未打。规格要求产品负责人 attestation ＋ 注释 tag，
  **AI 和 CI 不能批准**，两件都没有发生。
- **`G1` 不看任何实现。**它证明的是协议文本自洽，与服务端／车载端代码无关。
  两端行为是 G2 与 G3 的事。
- **两端指纹算法一致这件事，`G1` 完全不涉及。**协议只写 `Sha256`，不规定怎么算——
  这正是消息面接通后暴露的第二个跨端缺陷的成因。那件事只有 `G3` 能证。
