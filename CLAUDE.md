# 8005-agv-control-server

WIRE_TO_GATE MVP 的服务端生产仓库。本文是 agent 在本仓库工作时的指令。

## 写权限

本仓库**可写**。同一工作区里的其他仓库不是：`8005-agv-onboard-hmi` 与
`slots-simulator` 对 agent **只读**；`8005-agv-protocol` 可写，但每次推送都要开 issue
@`SocialKKKK` 通知。能连上一台机器、或问题路由到某个仓库，都不等于获得那个仓库的
写权限。

若本仓库是单独克隆的（不在 `8005-workspace` 工作区里），把上述三个仓库一律当只读处理
并先询问。

## 协作工作流

项目由两个人推进：Kun Wang（GitHub `SocialKKKK`）负责 `8005-agv-onboard-hmi` 与
`slots-simulator`；Zhengyu Shao 负责本仓库；`8005-agv-protocol` 共同维护。
完整说明在 `8005---AGV/docs/collaboration-workflow.md`。

Agent 必须遵守的部分：

- **协作单位是 integration slice**，不要自己发明推进单位。
  `8005-agv-protocol/integration-slices/index.json` 定义了 `W2G-IS-00` 到 `W2G-IS-07`，
  每个带 `sequence` 与 `prerequisites`。每个切片的 `gates` 就是分工：`G1` 双方共用、
  `CONTROL_SERVER_G2` 本仓库、`ONBOARD_HMI_G2` 对方、`G3` 两人一起。
- **两边的 G2 无依赖，可并行**；两边 G2 都绿了才约 G3。G3 需要两人同时在场，是最贵的
  资源，不要在对方 G2 未绿时提议。
- **跨仓库反馈分三条路**：协议契约的歧义或错误 → `8005-agv-protocol` 的 issue，附触发
  它的 `vectorId`；对方实现不符合契约 → **对方仓库**的 issue，**必须先跑 G3 拿证据**并
  附上 evidence 目录；本仓库的活 → 本仓库的 issue。
  **跨仓库指控必须带可复现的门禁证据，不能只是"我这边跑不通"。**格式照
  `docs/defects/` 已有的写法：开头一行 `Found by:` 加指向 G3 证据 `SUMMARY.md` 的链接。
- **改 protocol 由 Zhengyu Shao 单独决定，不需要事先批准**，但**每次推送都要在
  `8005-agv-protocol` 开 issue @`SocialKKKK` 通知**，写清改了什么、影响哪些
  `W2G-IS-*` 切片、他的 `ONBOARD_HMI_G2` 证据是否作废。**通知要和推送在同一个任务里
  完成。**发布（打 tag）仍需双人签名走 `attestations/` 那套机制，**AI 和 CI 不能批准**。
- **协议改动要攒批次。** 补丁发布会作废两边受影响的 G1/G2/G3 证据，每一次小改都在让
  对方重跑整套门禁。

## 语言约定

写进 GitHub 的东西用中文：README、文档正文、issue 标题与正文、PR 标题与正文、
commit message 正文。

保持英文：commit 的 conventional 前缀（`feat:` `fix:` `docs:` `chore:`）、标识符、
路径、命令、环境变量、错误码、门禁与切片名（`G1`、`W2G-IS-00`）、协议消息名与
schema 字段与 `vectorId`（它们是契约的一部分，改不得）。引用报错和测试输出时先贴
英文原文，再用中文解释。不回溯改旧的。

## 测试与门禁

**"跑全套测试"在本仓库指的是这一条**，不是任何门禁：

```powershell
dotnet test .\tests\ControlServer.Tests\ControlServer.Tests.csproj -c Release
```

历史规模约 243–249 passed / 0 skipped。构建前必须使用 `global.json` 指定的 .NET SDK
`8.0.424`；SDK 不在 `PATH` 时把 `WIRE_TO_GATE_DOTNET_EXE` 指向该版本的 `dotnet.exe`，
然后 `.\scripts\build.ps1`。

测试授权按当前任务算。整理、提交、推送一个本来就脏的工作树**不构成**跑测试的授权；
只有当前任务改了产品代码、测试或构建输入，或用户明确要求验证时才跑。不要从
`git status` 推断。

门禁比全套测试贵得多，**不要自行进入**，先说清楚跑哪个、成本多少、要验证什么：

| 门禁 | 命令 | 什么时候 |
| --- | --- | --- |
| `G1` | 在 protocol 仓 `pnpm g1` | 协议内容清单与审批签名校验 |
| `CONTROL_SERVER_G2` | `.\scripts\test-wire-to-gate.ps1 -Gate G2 -Slice <id> -ProtocolManifest <protocol-repo>\manifest\release.json -Output <新目录>` | 本端逐切片一致性 |
| `G3` | `.\scripts\run-staged-g3.ps1`、`run-staged-g3-restart.ps1`、`run-demand-bearing-g3-vectors.ps1`，均要 `-StageRoot <不存在的短路径> -EvidenceRoot <新目录>` | 双端联调 |
| `RC` | `.\scripts\New-WireToGateReleaseCandidate.ps1` | 打候选版本 |

要点：

- **G2 入口会先验证精确 protocol manifest 哈希。**协议版本一变，旧证据不能继承——
  `W2G-IS-01` 在 `protocol-v0.1.1` 中被重新映射到 `CV-DEMAND-ACCEPT-TO-PICKUP`，
  `v0.1.0` 的 G2 证据就已经作废过一次。
- **`-Output` / `-EvidenceRoot` 必须是新目录**，不要覆盖既有证据。
- `run-staged-g3.ps1` 需要 Node.js 与 pnpm（它要跑协议 G1）。三个 G3 runner 都走明文、
  都可无人值守，共用 `run-staged-g3.ps1` param 块里的四个 commit 绑定。
- **G3 向量各有证据不等于八个切片通过。**切片的通过要四道门禁齐全。
- 详细步骤见 `docs/RELEASE-CANDIDATE.md`。

## 证据纪律

- 证据按门禁分目录：`evidence/g2/`、`evidence/g3/`、`evidence/rc/`，每次一个新目录。
- **红色证据必须保留**，不能用一次成功的重跑掩盖。保留首次失败、解释原因、可能的话补
  回归测试。
- 缺陷记录进 `docs/defects/`，开头写 `Found by:` 加发现它的那次运行的证据链接。
- 历史红色证据和已发布的身份是不可变的。

## 脚本基线

PowerShell 7。不写 Windows PowerShell 5.1 兼容代码，不加版本探测或降级分支，不调
`powershell.exe` —— 用 `pwsh`。新建 `.ps1` 以 `#Requires -Version 7` 开头。
