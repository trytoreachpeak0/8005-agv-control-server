# 票 18：轨 B 出口证据（合成 3 车 L2，四项出口）

## 运行类型

本机 tier 1 ＋ **十一条合成 L2 场景，共 17 次运行**。**不动车、不使用任何现场凭据、不碰真
RIoT、不需要桌面**（`real-onboard-*` 三条不在本票范围）。

## 结论

| 项 | 结果 |
| --- | --- |
| 新增测试 | **4 条**（`MultiVehicleExecutionTests`，命令面从循环出发那条路径） |
| 全量套件 | **561 passed / 0 failed / 0 skipped**（票 09 基准 557 ＋ 4，`-c Release`） |
| 新增 migration | **零**。`Persistence/Migrations/` 与 `ControlServerDbContext` 一行未动 |
| `Ports.cs` 改动 | **零** |
| L2 | **17 次运行全 PASS**，出口三条各**连续三次** |
| 三连绿的地点 | **本机**。CI 上的三连要等这条分支被推上去，推不推由用户决定 |

原文：[`full-suite.txt`](full-suite.txt)（全量）、
[`multi-vehicle-tests.txt`](multi-vehicle-tests.txt)（`MultiVehicleExecutionTests` 27 条逐条）。

## 四项出口逐项对账（规格 8.3 轨 B 行）

| 出口 | 证据 |
| --- | --- |
| ① L1 ＋ 合成 3 车 L2 绿，新场景连续三次通过 | `full-suite.txt` ＋ 下表 `three-vehicle-exit` 三次 |
| ② RIoT 白名单架构测试绿 | 票 08 的 7 条在全量套件里，`full-suite.txt` |
| ③ 引擎快照双周期刷新与陈旧态 fail-closed 有 L2 证据，三种触发各一次 | `route-graph-staleness` ＋ `route-graph-engine` |
| ④ 命令面证明「该调用时调用了、参数正确、只调一次」 | `command-surface-order-hold` 三次 |

## L2 十一条，17 次运行

出口三条各跑三次，三次证据目录各自独立保留。三连跑纪律沿用黄金渲染机的既定标准：L2 跨进程，
一次通过区分不了「正确」与「这次恰好排上了」。

| 场景 | 次数 | 结果 | 证据 |
| --- | --- | --- | --- |
| **`three-vehicle-exit`** | 3 | **全 PASS**（22 判据 ×3） | `../../l2/20260910-ticket18-three-vehicle-exit-002/`、`-003/`、`-004/` |
| **`command-surface-order-hold`** | 3 | **全 PASS**（20 判据 ×3） | `../../l2/20260910-ticket18-command-surface-order-hold-003/`、`-004/`、`-005/` |
| **`route-graph-staleness`** | 3 | **全 PASS**（13 判据 ×3） | `../../l2/20260910-ticket18-route-graph-staleness-002/`、`-003/`、`-004/` |
| `normal-load` | 1 | PASS | `../../l2/20260910-ticket18-normal-load-003/` |
| `session-established-while-moving` | 1 | PASS | `../../l2/20260910-ticket18-session-established-while-moving-001/` |
| `load-result-requires-recovery` | 1 | PASS | `../../l2/20260910-ticket18-load-result-requires-recovery-001/` |
| `load-command-never-answered` | 1 | PASS | `../../l2/20260910-ticket18-load-command-never-answered-001/` |
| `route-graph-engine` | 1 | PASS | `../../l2/20260910-ticket18-route-graph-engine-001/` |
| `create-gate` | 1 | PASS | `../../l2/20260910-ticket18-create-gate-001/` |
| `create-gate-unapproved` | 1 | PASS（**`FP-C13` 的负向证据**） | `../../l2/20260910-ticket18-create-gate-unapproved-001/` |
| `three-synthetic-peers` | 1 | PASS | `../../l2/20260910-ticket18-three-synthetic-peers-001/` |

三条出口场景的第一次探路运行另存，不计入三连：`three-vehicle-exit-001`、
`command-surface-order-hold-002`、`route-graph-staleness-001`，都是 PASS。

### 两份故意保留的失败留档

证据永不覆盖，红的不许被绿的重跑覆盖。这两次都**不是产品问题**：

| 目录 | 原因 |
| --- | --- |
| `../../l2/20260910-ticket18-normal-load-001/` | `SocketException 10013` —— 端口 58410 被机器上一个代理的出站连接占着。旧端口块落在 Windows 的动态端口范围（49152–65535）内，是构造性易碎。修法是把整块挪到 484xx，见 `scripts/l2/README.md` 的「端口」一节 |
| `../../l2/20260910-ticket18-command-surface-order-hold-001/` | 场景脚本把 `Get-Journeys` 直接送进管道。`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 `return`，管道里只有一个元素、那个元素是整张结果集，`$_.AgvId` 成员展开成三个值，一条也匹配不上 |

## `assertions.json` 的 `identity` 块（规格 8.4）

两项新字段在每一次运行里都有：

```json
"batchId": "batch-2",
"protocolReleaseIdentity": {
  "repository": "8005-agv-protocol",
  "releaseVersion": "0.1.1",
  "tag": "protocol-v0.1.1",
  "commit": "1531489e42e328f28bfe0c51ed3f8c56e5ce0279",
  "protocolVersion": 1,
  "profileId": "WIRE_TO_GATE_MVP",
  "manifestSha256": "a467c0c4b03cbf54fae985ceade256ff13225581babad7f46d90449b7f16389f",
  "schemaBundleSha256": "e04296e9bcf48c341bc91fef5731f6f465a5ecdbb9adedc17f3bac58e193d30c",
  "vectorsSha256": "fc5902b71d1b276c674f8a21c738d27193ddcbaf9b352951deffbaf1488d356e",
  "approvalStatus": "APPROVED_RELEASE"
}
```

**这不是占位。**它从跑起来的服务端 `/version` 读回，也就是 `ProtocolCandidateIdentity` ——
build 真的在握手上强制的那一份身份。协议仓今天仍是 `v0.1.1`，所以读回的就是 v1 的三元组；
批次 1 出 v2 之后，同一处读回自动跟着走，不需要改脚本。在脚本里复述一个「候选 v2 三元组」
只会让证据与脚本自洽而与服务端无关，那正是这个字段要防的事。

## 四条证据纪律

| 纪律 | 遵守情况 |
| --- | --- |
| `-EvidenceRoot` 必须是不存在的目录 | ✅ 编排器在第一行就 throw |
| 红的证据不允许被绿的重跑覆盖 | ✅ 两份红的留档在上表，序号在绿的之前 |
| 跑失败时 stage root 不删 | ✅ 两次红的都留了，路径打在告警里 |
| 证据目录只增不改 | ✅ 17 个新目录，一个旧目录都没动 |

## 目录命名的一处说明

目录前缀是 `20260910`，而这批运行的真实墙上时间是 **2026-09-08**。前缀沿用交接文档「常用命令」
一节给出的命名，没有事后改名（改名也是「改证据目录」）。每个目录里的 `runId` 与
`timeline.jsonl` 带的是真实时间戳，以它们为准。
