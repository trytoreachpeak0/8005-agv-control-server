# L2 场景证据：multi-demand-four-stops

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T085759231Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `d3911a439c0680829a46c4138faa58c8e578ca41` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T085759231Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 四条需求凑成一趟旅程：四个取货停靠加一个关卡 | PASS | `4 PICKUP + 1 GATE` | `4 PICKUP + 1 GATE` |
| 是一趟旅程，不是四趟 | PASS | `1` | `1` |
| 四个取货停靠落在四个不同站点上（这才是多停靠，不是一站多单） | PASS | `4` | `4` |
| 服务端按契约发出五条腿的行程带（四个取货加一个关卡） | PASS | `5` | `5` |
| 行程带里四条 TO_PICKUP 加一条 TO_GATE，序号连续 | PASS | `4 TO_PICKUP + 1 TO_GATE / 1,2,3,4,5` | `4 TO_PICKUP + 1 TO_GATE / 1,2,3,4,5` |
| 停靠 1 装载提交，累计 1 次 | PASS | `>= 1` | `1` |
| 停靠 2 装载提交，累计 2 次 | PASS | `>= 2` | `2` |
| 停靠 3 装载提交，累计 3 次 | PASS | `>= 3` | `3` |
| 停靠 4 装载提交，累计 4 次 | PASS | `>= 4` | `4` |
| 四个停靠都装完之后旅程进入去关卡那一段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| 装货阶段收尾时记下了理由 | PASS | `a reason` | `NO_FURTHER_CARGO` |
| 全程没有任何一次装载被判 RecoveryRequired | PASS | `0` | `0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
