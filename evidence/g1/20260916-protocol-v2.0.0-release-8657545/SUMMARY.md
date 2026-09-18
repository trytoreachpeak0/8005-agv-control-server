# 协议 `G1`：`protocol-v2.0.0`（`8657545`）候选 G1 与发布 G1 **均 `PASS`**

**它取代 `../20260912-protocol-v1.0.0-release-9f22db8/`，作为现行的协议 G1 证据。**那一份原样保留，对 `protocol-v1.0.0` 仍然成立。

票：`trytoreachpeak0/8005-agv-program#97`（批次5-33）。候选包：`trytoreachpeak0/8005-agv-program#96`（批次5-21）关闭评论公布的候选身份表。

## 这一版发布了什么

2026-09-16 全产品协议 `AGV_FULL_PRODUCT` 2.0.0（`protocolVersion` 3）正式发布为 `protocol-v2.0.0`：

| 项 | 值 |
| --- | --- |
| 注释 tag | `protocol-v2.0.0` → `86575456c847041515b7b75e8851a00e0d939804` |
| 身份 | `(AGV_FULL_PRODUCT, protocolVersion 3)`，`releaseVersion` 2.0.0 |
| content manifest | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7`（与候选相同） |
| schema bundle | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| vectors | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| approval attestation | `release-approval.json`，SHA-256 `db745d0dffd6fa4c206003d7d4b49d771327cc6fcc6276ff19de97c01e3631f6`，作为 GitHub Release Asset 发布 |
| Release | https://github.com/trytoreachpeak0/8005-agv-protocol/releases/tag/protocol-v2.0.0 |

tag 解引用到的就是 批次5-21 冻结在 `fp/v2-candidate` 上的候选 commit，内容与两端试消费的候选逐字节相同，没有重新生成。

## 批准

**这一版的批准由 AI 给出，attestation 上如实写明**：`ownerId` 为 `claude-opus-5 (Claude Code agent)`，`approverKind` 为 `AI_AGENT`，
`authorizedBy` 为 `Zhengyu Shao`，`decidedAt` 为 `2026-09-16T12:19:21Z`。

授权来源：产品负责人 Zhengyu Shao 在执行本票的 Claude Code 对话里给出，原话同时记录在本票调度会话评论（2026-09-16T12:11:11Z）：

> 授权执行 program#97 的会话代我批准 protocol-v2.0.0，候选 commit 86575456c847041515b7b75e8851a00e0d939804，manifest 4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7。

`decidedAt` 取的是这条授权到达执行会话的时刻（会话收到授权后执行第一条命令时记下的 UTC 时间），不是脚本运行时刻（`12:26:16Z`）。授权只覆盖这一个 commit 与 manifest。

批准之前核过：`fp/v2-candidate` 顶端仍是 `86575456…`；`manifest/release.json` 的 SHA-256 为 `4ac095ad…`；远端没有任何 `protocol-v2*` tag；候选 G1 在干净克隆上 PASS。

发布时机条件（票面第 0 步）：

- 服务端 `trytoreachpeak0/8005-agv-control-server#84` 已关闭，PR `trytoreachpeak0/8005-agv-control-server#107` 合并提交 `f563efa8`；在该候选身份上全量 L1 893 passed、开发态 `CONTROL_SERVER_G2` 十片 PASS，标 `UNRELEASED_CANDIDATE`。
- 车载端 `trytoreachpeak0/8005-agv-onboard-hmi#73` 已关闭，PR `trytoreachpeak0/8005-agv-onboard-hmi#88` 合并提交 `80093358`；单元 238/238、开发态 G2 PASS，标 `UNRELEASED_CANDIDATE`。

**attestation 里有一处文字瑕疵**：批准说明写成了 "on the product owners behalf"，本意是 "product owner's"。撇号在命令行传参时被 shell 引号吃掉了。
这份文件已经作为不可变的 Release Asset 发布，哈希被 tag 绑定；这处笔误不改变批准的对象、批准人或授权人，所以没有重发。

## 结果

| | 候选 G1（`candidate-g1-result.json`） | 发布 G1（`release-g1-result.json`） |
| --- | --- | --- |
| attestation | 仓里的空白模板，`PENDING`，`TRACKED_TEMPLATE` | 外置的 `release-approval.json`，`APPROVED`，`EXTERNAL_RELEASE_ASSET` |
| `status` | `PASS` | `PASS` |
| `candidateManifestSha256` | `4ac095ad…` | `4ac095ad…` |
| `approvalAttestationSha256` | 模板的哈希 `cf3cc4aa…` | `db745d0d…`，与 Release 上下载回来的附件一致 |
| `failures` | 空 | 空 |
| 计数 | 69 schema／63 消息／63 valid／1571 invalid／33 向量／16 切片 | 同左 |

候选 G1 在短路径的普通克隆 `C:\p2r\cand`（`fp/v2-candidate`，`86575456`）上运行，输出见 `candidate-g1-stdout.log`；跑完 `git status --porcelain` 为空，
说明门禁写出的 `evidence/g1-result.json` 与候选 commit 里提交的那一份逐字节相同。

发布 G1 由 `Publish-ProtocolRelease.ps1` 在发布流程中运行：干净克隆 `86575456`、核 manifest 哈希、写入 attestation、带 `PROTOCOL_APPROVAL_ATTESTATION` 跑 G1，
PASS 之后才打 tag、推送、发 Release。日志是 `publish.log`（控制台 `12:26:00`–`12:26:44` UTC）。

两次 G1 都不在 git worktree 里跑：协议仓的门禁排除 `.git` 的判据是 `.git/` 前缀，worktree 里 `.git` 是文件，会被算进内容清单（批次5-21 踩过）。
克隆放在短路径下，也避开负例文件长路径撞 MAX_PATH。

## 发布后核对（票面第 4 步）

在一个只 fetch 了这个 tag 的空仓库里独立核对，不依赖脚本自己的判断：

- `git cat-file -t protocol-v2.0.0` 为 `tag`（注释 tag），`protocol-v2.0.0^{commit}` 为 `86575456c847041515b7b75e8851a00e0d939804`；
- tag 消息含 `protocolVersion: 3`、`contentManifestSha256: 4ac095ad…`、`approvalAttestationSha256: db745d0d…`，与上表一致；tagger 为 `Zhengyu Shao <trytoreachpeak0@gmail.com>`；
- `gh release download` 取回的 `release-approval.json` SHA-256 为 `db745d0d…`，与 `release-g1-result.json` 的 `approvalAttestationSha256` 一致；Release 不是 draft、不是 prerelease；
- 协议仓仍是 public，`fp/v2-candidate` 顶端未变，没有改任何文件。

## 脚本相对 v1.0.0 的改动

`Publish-ProtocolRelease.ps1` 由 `../20260912-protocol-v1.0.0-release-9f22db8/` 的同名脚本复制，改了发布身份（`Version`、`Commit`、三个哈希、
tag 消息里的 `protocolVersion: 3`、Release notes 文案），安全步骤（推 tag 前再核远端、G1 条件检查、打 tag 的 git 身份、资产回读）全部保留，另加三处：

1. `decidedAt` 由新参数 `-DecidedAt` 给出，写授权时刻；
2. 工作克隆放在 `-WorkParent`（默认 `C:\p2r`）下，不再放在证据目录里；
3. 推送后核远端 tag 解引用到 `$Commit`；Release 附件下载回来核 SHA-256，而不只核文件名。

本目录另有一个 `.gitattributes`，把 `release-approval.json` 设为 `-text`，按字节入库。仓库根的 `*.json text eol=lf` 会把 PowerShell 写出的 CRLF
规范化，入库的那份就不再是 Release 上的字节：v1.0.0 目录里的 `release-approval.json` 入库后 SHA-256 是 `d4289684…`，而不是它自己 SUMMARY 写的 `545fba1c…`。
本目录的这份入库后仍是 `db745d0d…`。

## 未在本轮证明的

- **新身份上的 G2／G3／L2 证据还不存在，由实现侧在发布后重跑。**两端此前的 L1 与开发态 G2 是在同一 commit、同一 manifest 的候选上跑的，
  标 `UNRELEASED_CANDIDATE`，不计入批次出口。服务端 `trytoreachpeak0/8005-agv-control-server#89`（批次5-34）、车载端 `trytoreachpeak0/8005-agv-onboard-hmi#79`（批次5-35）
  只需把 `ApprovalStatus` 改为 `APPROVED_RELEASE` 即可出证，不用重新 vendor。
- 本次发布作废全部绑定 `protocol-v1.0.0` 的 `FP-IS-*` 证据。
- G1 不看任何实现。
- 协议仓的 `main` 仍不在这条线上，tag 指向候选分支上的 commit，治理文档没有要求先合入。
