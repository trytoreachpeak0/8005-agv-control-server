# 协议 `G1`：`protocol-v1.0.0`（`9f22db8`）候选 G1 与发布 G1 **均 `PASS`**

**它取代 `../20260912-fp-is-14-15-protocol-g1-16e2567/`，作为现行的协议 G1 证据。**那一份原样保留，对 `16e2567` 仍然成立。

## 这一版发布了什么

2026-09-12 协议 v2 正式发布为 `protocol-v1.0.0`：

| 项 | 值 |
| --- | --- |
| 注释 tag | `protocol-v1.0.0` → `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| content manifest | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| schema bundle | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| vectors | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df`（与 `16e2567` 相同） |
| approval attestation | `release-approval.json`，SHA-256 `545fba1c6d67be0cf2b834142340001e36ab9b3245ec1fc1eabaf9b28ccf22e0`，作为 GitHub Release Asset 发布 |

与 `16e2567` 相比，内容只改了批准规则。产品负责人当天决定，发布批准也可以由他授权的 AI agent 给出。为此，attestation schema 在
`approvals` 的每一项新增必填的 `approverKind`（`PRODUCT_OWNER` 或 `AI_AGENT`）；取值为 `AI_AGENT` 时，`authorizedBy` 也必填。
治理文档、`README.md`、`CLAUDE.md` 同步修改，`8005-agv-program` 的候选生成器也一起改了。消息、schema 的其余部分、样例与向量都没有动。

**这一版的批准由 AI 给出，attestation 上如实写明**：`ownerId` 为 `claude-opus-5 (Claude Code agent)`，`approverKind` 为 `AI_AGENT`，
`authorizedBy` 为 `Zhengyu Shao`，`decidedAt` 为 `2026-09-12T14:29:48Z`。批准说明写明了授权来自对话，也写明批准时新身份上的
G2／G3／L2 证据还不存在，要在发布之后重跑，也就是同日这一轮。

## 结果

| | 候选 G1（`candidate-g1-result.json`） | 发布 G1（`release-g1-result.json`） |
| --- | --- | --- |
| attestation | 仓里的空白模板，`PENDING`，`TRACKED_TEMPLATE` | 外置的 `release-approval.json`，`APPROVED`，`EXTERNAL_RELEASE_ASSET` |
| `status` | `PASS` | `PASS` |
| `candidateManifestSha256` | `a0e1deed…` | `a0e1deed…` |
| `approvalAttestationSha256` | 模板的哈希 | `545fba1c…`，与 Release 上下载回来的附件一致 |
| `failures` | 空 | 空 |
| 计数 | 69 schema／63 消息／63 valid／1548 invalid／31 向量／16 切片 | 同左 |

候选 G1 在一份新的普通克隆 `C:\Users\szy\8005-b3\proto-rel-9f22db8` 上运行，这份克隆带着 `protocol-v1.0.0` 这个 tag；跑完 `git status` 为空，
说明门禁写出的 `evidence/g1-result.json` 与 `9f22db8` 里提交的那一份逐字节相同。同日的 G2 就用这份克隆作为协议仓。

发布 G1 由 `Publish-ProtocolRelease.ps1` 在发布流程中运行：干净克隆 `9f22db8`，写入 attestation，带 `PROTOCOL_APPROVAL_ATTESTATION` 跑 G1，
PASS 之后才打 tag、推送、发 Release。脚本与日志（`publish.log`）放在本目录。发布之后核对过：tag 是注释 tag，解引用到 `9f22db8`；
tag 消息里的两个哈希与上表一致；Release 附件下载后 SHA-256 为 `545fba1c…`。

schema 的新约束发布前在一份与 `9f22db8` 内容相同的生成树上核过：`AI_AGENT` 缺 `authorizedBy`、缺 `approverKind`，这两种 G1 都判 FAIL；
`AI_AGENT` 带授权人、`PRODUCT_OWNER` 这两种判 PASS。

## 同日第一次发布尝试

`publish-aborted-20260912T141237Z.log` 与 `release-approval.aborted-20260912T141237Z-tag-identity.json` 是第一次尝试留下的：
发布 G1 已经 PASS，但打注释 tag 时全新克隆里没有 git 身份，git 报 `Committer identity unknown`，脚本就此停止。
**tag 没有打出，也没有推送，Release 没有创建。**那份 attestation 没有被发布，改名保留在这里；脚本补上打 tag 身份后重新运行，
生成的是一份新的 attestation，就是上表那份。

## 未在本轮证明的

- G1 不看任何实现。两端在这个身份上的行为，由同日重跑的 G2、G3 与 L2 证明。
- 协议仓的 `main` 仍在 v0.x 那条线上，`fp/v2-candidate` 没有合入 `main`。tag 指向候选分支上的 commit，治理文档没有要求先合入。
