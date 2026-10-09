# 协议 `G1`：`protocol-v3.0.0`（`3f091cb`）候选 G1 与发布 G1 **均 `PASS`**

**它取代 `../20260916-protocol-v2.0.0-release-8657545/`，作为现行的协议 G1 证据。**那一份原样保留，对 `protocol-v2.0.0` 仍然成立。

票：`trytoreachpeak0/8005-agv-program#152`（批次8-07）。候选包：`trytoreachpeak0/8005-agv-program#151`（批次8-06）关闭评论公布的候选身份表；此后没有重发候选。

## 这一版发布了什么

2026-10-09 全产品协议 `AGV_FULL_PRODUCT` 3.0.0（`protocolVersion` 4）正式发布为 `protocol-v3.0.0`：

| 项 | 值 |
| --- | --- |
| 注释 tag | `protocol-v3.0.0`（对象 `e08c362eaceb9dda34d18f3da81596d783992047`）→ `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| 身份 | `(AGV_FULL_PRODUCT, protocolVersion 4)`，`releaseVersion` 3.0.0 |
| content manifest | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e`（与候选相同） |
| schema bundle | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| vectors | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| approval attestation | `release-approval.json`，SHA-256 `64f4036b124eb2393e5a2f747457d3083677036e59347a83f70d897245e63a53`，作为 GitHub Release Asset 发布 |
| Release | https://github.com/trytoreachpeak0/8005-agv-protocol/releases/tag/protocol-v3.0.0 |

tag 解引用到的就是 批次8-06 冻结在 `batch-p3/protocol-v3.0.0-candidate` 上的候选 commit，内容与两端试消费的候选逐字节相同，没有重新生成。

发布前，`fp/v2-candidate` 由 `86575456c847041515b7b75e8851a00e0d939804` **快进**到 `3f091cb2…`（2026-10-09T11:01:35Z 前后，`git push origin 3f091cb2…:refs/heads/fp/v2-candidate`，推送前 `git merge-base --is-ancestor` 确认可快进，没有 force push）。候选 PR `trytoreachpeak0/8005-agv-protocol#12` 随之显示为 MERGED。

## 批准

**这一版的批准由 AI 给出，attestation 上如实写明**：`ownerId` 为 `claude-opus-5-5 (Claude Code agent)`，`approverKind` 为 `AI_AGENT`，
`authorizedBy` 为 `Zhengyu Shao`，`decidedAt` 为 `2026-10-09T10:41:00Z`。

授权来源：产品负责人 Zhengyu Shao 于 2026-10-09 18:41 CST 在调度会话（Coordinator 10）里给出，原话「现在就授权」，并在调度给出的选项中选择「授权 AI 代批」。调度把授权范围记在
https://github.com/trytoreachpeak0/8005-agv-program/issues/152#issuecomment-6079269621 ：仅限 `protocol-v3.0.0`、候选 commit `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`、`contentManifestSha256` `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e`，候选重发即失效。

`decidedAt` 取的是授权给出的时刻。调度记录只精确到分钟（18:41 CST），秒数记为 `00`；不是脚本运行时刻（`11:08:23Z`）。

**批准说明这次从文件读入**（脚本参数 `-StatementPath`），不经命令行传参。v2.0.0 的 attestation 里 "product owner's" 的撇号在命令行传参时被吃掉了；这一份的批准说明原样写的是 "on the product owner's behalf"。

## 发布时机核对（票面第 0 步）

执行会话自己复核的结果贴在 https://github.com/trytoreachpeak0/8005-agv-program/issues/152#issuecomment-6079532782 ，要点：

- `fp/v2-candidate` 顶端为 `86575456…`，候选分支顶端为 `3f091cb2…`，compare 为 ahead 1、behind 0；候选提交上 `g1` check success（`8005-agv-protocol` run 36576277856）；远端没有任何 `protocol-v3*` tag。
- 服务端 `batch-p3/v3` 的 `appsettings.json` `ProtocolCandidate` 绑定 `3f091cb2…`／`d5e1a53f…`；车载端 `w2g/batch-p3/v3` vendor 的 `manifest/release.json` 实算 SHA-256 为 `d5e1a53f…`。
- 七张 v3 实现票全部关闭、PR 合入批次分支、合入前 CI 绿：服务端 cs#382（PR #421）、cs#383（PR #424）、cs#384（PR #467）、cs#385（PR #425）；车载端 hmi#214（PR #224）、hmi#215（PR #247）、hmi#216（PR #225）。
- 两端在这份候选上的 L1 与开发态 G2 通过，标 `UNRELEASED_CANDIDATE`（服务端 13 片见 program#152 第一条评论；车载端见 hmi#214 关闭评论，CI run 36614227750）。
- 上真车验证以调度确认为准：10-09 用户认定首趟主流程为「上真车验证完成」。**v3 发布前没有在车上跑过**：候选过不了服务端打包闸门（`scripts/New-WireToGateReleaseCandidate.ps1` 要求 `APPROVED_RELEASE`），这一点调度在授权前已告知用户。

## 结果

| | 候选 G1（`candidate-g1-result.json`） | 发布 G1（`release-g1-result.json`） |
| --- | --- | --- |
| attestation | 仓里的空白模板，`PENDING`，`TRACKED_TEMPLATE` | 外置的 `release-approval.json`，`APPROVED`，`EXTERNAL_RELEASE_ASSET` |
| `status` | `PASS` | `PASS` |
| `candidateManifestSha256` | `d5e1a53f…` | `d5e1a53f…` |
| `approvalAttestationSha256` | 模板的哈希 `ddfa96ad…` | `64f4036b…`，与 Release 上下载回来的附件一致 |
| `failures` | 空 | 空 |
| 计数 | 71 schema／65 消息／65 valid／1666 invalid／39 向量／16 切片 | 同左 |

候选 G1 在短路径的普通克隆 `C:\s\p152`（快进之后的 `fp/v2-candidate`，`3f091cb2`）上运行，输出见 `candidate-g1-stdout.log`；跑完 `git status --porcelain` 为空，
门禁写出的 `evidence/g1-result.json` 与候选 commit 里提交的那一份逐字节相同。

发布 G1 由 `Publish-ProtocolRelease.ps1` 在发布流程中运行：核远端 `fp/v2-candidate` 顶端等于 `3f091cb2…` 且没有同名 tag、干净克隆、核 manifest 哈希、写入 attestation、
带 `PROTOCOL_APPROVAL_ATTESTATION` 跑 G1，PASS 之后才打 tag、推送、发 Release。日志是 `publish.log`（`11:08:14`–`11:08:42` UTC）。

两次 G1 都不在 git worktree 里跑（worktree 的 `.git` 是文件，会被算进内容清单），克隆放在短路径下以避开负例文件长路径撞 MAX_PATH。

**有一次被中断的运行，没有产生任何发布动作。**`11:07:30Z` 第一次启动脚本时，操作在第 1 步（只读的远端核对）之后被中断：没有写 attestation、没有打 tag，进程已全部退出，
远端核对仍无 `protocol-v3*` tag。那份只有两行的日志没有入库；`11:08:14Z` 重新运行，即 `publish.log`。

## 发布后核对（票面第 6 步）

在一个只 fetch 了这个 tag 的空仓库里独立核对，不依赖脚本自己的判断：

- `git cat-file -t protocol-v3.0.0` 为 `tag`（注释 tag），`protocol-v3.0.0^{commit}` 为 `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c`；
- tag 消息含 `releaseVersion: 3.0.0`、`protocolVersion: 4`、`contentManifestSha256: d5e1a53f…`、`approvalAttestationSha256: 64f4036b…`，与上表一致；tagger 为 `Zhengyu Shao <trytoreachpeak0@gmail.com>`；
- `gh release download` 取回的 `release-approval.json` SHA-256 为 `64f4036b…`，与 `release-g1-result.json` 的 `approvalAttestationSha256` 一致；Release 不是 draft、不是 prerelease，附件只有这一个；
- 本目录入库的 `release-approval.json` 与 Release 上的附件 `cmp` 逐字节相同（目录内 `.gitattributes` 把它设为 `-text`，不被仓库根的 `*.json text eol=lf` 规范化）；
- 协议仓仍是 public，`fp/v2-candidate` 顶端为 `3f091cb2…`，没有改任何文件。

## 脚本相对 v2.0.0 的改动

`Publish-ProtocolRelease.ps1` 由 `../20260916-protocol-v2.0.0-release-8657545/` 的同名脚本复制，改了发布身份（`Version`、`Commit`、三个哈希、
tag 消息里的 `protocolVersion: 4`、Release 说明文案），安全步骤（第 1 步核远端 `fp/v2-candidate` 顶端、推 tag 前再核远端、G1 条件检查、打 tag 的 git 身份、
推送后核 tag 解引用、资产回读核哈希）全部保留，另有一处改动：

- 批准说明由 `-StatementPath` 从文件读入（UTF-8，要求恰好一行非空），不再由 `-Statement` 经命令行传入；
- 默认 `-WorkParent` 改为 `C:\s\p3r`。

## 未在本轮证明的

- **v3 身份上的 G2／G3／L2 正式证据还不存在，由出口票 批次8-23（`trytoreachpeak0/8005-agv-control-server#393`）在发布身份上重跑。**两端此前的 L1 与开发态 G2
  是在同一 commit、同一 manifest 的候选上跑的，标 `UNRELEASED_CANDIDATE`，不计入批次出口。manifest 与候选相同，两端不用重新 vendor，
  只需把 `ApprovalStatus` 改为 `APPROVED_RELEASE`（服务端连同 `appsettings.json` 的 `ProtocolCandidate` 块），`Tag` 已是 `protocol-v3.0.0`。
- 按规格第 6.5 节，本次发布起绑定 `protocol-v2.0.0` 的门禁与 L2 证据不再是现行证据；集成分支 `fp/v2-impl` 在 cs#393 合回之前仍是 `protocol-v2.0.0` 身份。
- G1 不看任何实现。
- 协议仓的 `main` 仍不在这条线上，tag 指向 `fp/v2-candidate` 上的 commit，治理文档没有要求先合入。
