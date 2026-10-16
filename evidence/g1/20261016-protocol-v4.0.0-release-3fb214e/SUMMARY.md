# 协议 `G1`：`protocol-v4.0.0`（`3fb214e`）候选 G1 与发布 G1 **均 `PASS`**

**它取代 `../20261009-protocol-v3.0.0-release-3f091cb/`，作为现行的协议 G1 证据。**那一份原样保留，对 `protocol-v3.0.0` 仍然成立。

这次发布没有单开 program 票，由调度会话 Coordinator11（C11）直接派给一个工作会话执行；授权与核对的往来都在 C11 与该工作会话之间，下文照实记录。

## 这一版发布了什么

2026-10-16 全产品协议 `AGV_FULL_PRODUCT` 4.0.0（`protocolVersion` 5）正式发布为 `protocol-v4.0.0`：

| 项 | 值 |
| --- | --- |
| 注释 tag | `protocol-v4.0.0`（对象 `daeebc5c5af340ffa5118a8c58d6f9fca79836cf`）→ `3fb214e57d861a909d872511940d26133eb32dca` |
| 身份 | `(AGV_FULL_PRODUCT, protocolVersion 5)`，`releaseVersion` 4.0.0 |
| content manifest | `beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a`（与候选相同） |
| schema bundle | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| vectors | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| approval attestation | `release-approval.json`，SHA-256 `68f3516eaeb12d54cf5319e57a325362915f34ac47494712920f007cf25ad9e0`，作为 GitHub Release Asset 发布 |
| Release | https://github.com/trytoreachpeak0/8005-agv-protocol/releases/tag/protocol-v4.0.0 |

tag 直接打在 `fp/v2-candidate` 的顶端提交 `3fb214e5`（protocol PR #20 的合并提交）上，就是两端已经 vendor 的那份候选，没有重新生成，
也没有额外的「改成发布状态」的内容提交：manifest 仍是 `CONTENT_SNAPSHOT`，与 `protocol-v3.0.0` 的做法相同。协议仓没有改任何文件，
`fp/v2-candidate` 发布前后都是 `3fb214e5`，不需要快进。

## 批准

**这一版的批准由 AI 给出，attestation 上如实写明**：`ownerId` 为 `claude-opus-5-5 (Claude Code agent)`，`approverKind` 为 `AI_AGENT`，
`authorizedBy` 为 `Zhengyu Shao`，`decidedAt` 为 `2026-10-16T01:27:46Z`。

授权来源：产品负责人 Zhengyu Shao 于 2026-10-16 09:15:41 CST（`01:15:41Z`）在 C11 会话里说「你先通知原本你的会话，想让agv01，02，03跑起来吧，然后就发布协议」。
在此之前 C11 已向用户说明这次发布的内容与代价：候选 `3fb214e5`、manifest `beca46a7…`；发布后绑定 `protocol-v3.0.0` 的 G1／G2／G3 证据作废；
换装时三车与服务端要一起换。授权只管这一次发布。精确时刻由 C11 从其会话记录查得。

`decidedAt` 是执行会话做出批准决定的时刻：核完远端顶端、manifest 哈希、提交上的 `g1` check（`8005-agv-protocol` run 38740400944）与候选 G1 之后，
写 attestation 前一刻取的真实 UTC。批准说明由 `-StatementPath` 从文件读入，撇号与中文原话原样保留。

**有一份 attestation 被作废，从未离开本机。**第一次 Prepare（`01:26:04Z`）写出的那份（SHA-256 `60effaa8…`）把 `decidedAt` 记为 `01:15:00Z`、
说明里写 "at about 09:15 CST"——那是按「09:15 前后」取整的时刻，比授权本身早 41 秒，等于批准早于授权。C11 核对时指出后作废：那份 attestation
及其发布 G1 结果、tag 消息、Release 说明连同那次的日志都移出了本目录，没有打 tag、没有推送、没有上传。现在本目录里的是第二次 Prepare（`01:27:47Z`）的产物，
相对作废那份只改了 `decidedAt` 与说明里的授权时刻两处。

## 结果

| | 候选 G1（`candidate-g1-result.json`） | 发布 G1（`release-g1-result.json`） |
| --- | --- | --- |
| attestation | 仓里的空白模板，`PENDING`，`TRACKED_TEMPLATE` | 外置的 `release-approval.json`，`APPROVED`，`EXTERNAL_RELEASE_ASSET` |
| `status` | `PASS` | `PASS` |
| `candidateManifestSha256` | `beca46a7…` | `beca46a7…` |
| `approvalAttestationSha256` | 模板的哈希 `0269297f…` | `68f3516e…`，与 Release 上下载回来的附件一致 |
| `failures` | 空 | 空 |
| 计数 | 73 schema／67 消息／67 valid／1711 invalid／50 向量／16 切片 | 同左 |

候选 G1 在普通克隆 `F:\8005-scratch\p4c`（`fp/v2-candidate`，`3fb214e5`）上运行，输出见 `candidate-g1-stdout.log`；跑完 `git status --porcelain` 为空，
克隆里没有 `.pnpm-store`（`pnpm_config_store_dir`／`npm_config_store_dir` 设到 `%LOCALAPPDATA%\pnpm-store`）。

发布按调度要求拆成两段（见下节）：`publish-prepare-20261016T012747Z.log` 是写 attestation 与发布 G1，`publish-publish-20261016T013002Z.log` 是打 tag、推送、发 Release。
两次 G1 都不在 git worktree 里跑，均在 C11 放给本次发布的本机时段内完成（看板持有者 `protocol-v4`）。

## 发布后核对

在一个只 fetch 了这个 tag 的空仓库里独立核对，不依赖脚本自己的判断：

- `git cat-file -t protocol-v4.0.0` 为 `tag`（注释 tag，对象 `daeebc5c…`），`protocol-v4.0.0^{commit}` 为 `3fb214e57d861a909d872511940d26133eb32dca`；
- tag 消息含 `releaseVersion: 4.0.0`、`protocolVersion: 5`、`contentManifestSha256: beca46a7…`、`approvalAttestationSha256: 68f3516e…`，与本目录 `tag-message.txt` 一致；tagger 为 `Zhengyu Shao <trytoreachpeak0@gmail.com>`；
- `gh release download` 取回的 `release-approval.json` SHA-256 为 `68f3516e…`，与 `release-g1-result.json` 的 `approvalAttestationSha256` 一致；Release 不是 draft、不是 prerelease，附件只有这一个；
- 本目录入库的 `release-approval.json` 与 Release 上的附件 `cmp` 逐字节相同（目录内 `.gitattributes` 把它设为 `-text`）；
- 协议仓仍是 public，`fp/v2-candidate` 顶端为 `3fb214e5…`，远端 `protocol-v4*` 只有这一个 tag。

## 脚本相对 v3.0.0 的改动

`Publish-ProtocolRelease.ps1` 由 `../20261009-protocol-v3.0.0-release-3f091cb/` 的同名脚本复制，改了发布身份（`Version`、`Commit`、三个哈希、
tag 消息里的 `protocolVersion: 5`、Release 说明文案），安全步骤全部保留。结构上的改动：

- **拆成 `-Phase Prepare` 与 `-Phase Publish`。**tag 推出去就撤不回来，这一次调度要求打 tag 前先核 tag 消息全文与 Release 说明。Prepare 写出
  `tag-message.txt` 与 `release-notes.md`，Publish 原样使用它们，并在动手前核：attestation 实算哈希等于发布 G1 报告的哈希、两份文字写的就是这个哈希、
  远端 `fp/v2-candidate` 顶端仍是 `3fb214e5`、远端没有同名 tag。正是这道两段之间的人工核对抓到了上面那份作废的 attestation。
- 默认 `-WorkParent` 改为 `F:\8005-scratch\p4r`（工作区 10-11 起大件 scratch 放 F 盘）；脚本设置 pnpm store 目录，避免 pnpm v11 在 F 盘克隆里建 `.pnpm-store`。

## 未在本轮证明的

- **v4 身份上的 G2／G3／L2 正式证据还不存在。**两端此前在同一 commit、同一 manifest 的候选上跑出的证据标 `UNRELEASED_CANDIDATE`，不计入出口。
  manifest 与候选相同，两端不用重新 vendor；把身份从候选改成已发布、在发布身份上重证，由调度另行安排。
- 本次发布起绑定 `protocol-v3.0.0` 的门禁与 L2 证据不再是现行证据。
- G1 不看任何实现。
