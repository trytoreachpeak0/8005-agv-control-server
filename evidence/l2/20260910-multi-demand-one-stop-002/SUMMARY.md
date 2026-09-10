# L2 场景证据：multi-demand-one-stop

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T070612914Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `b5a5e0ebb77a54f6cec727ddd90418c2425119fe` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T070612914Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 受理后建出 TO_PICKUP 单并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| 两条需求共用一趟旅程（而不是各起一趟） | PASS | `1` | `1` |
| 同一个停靠上完成两次装载闭环 | PASS | `2` | `2` |
| 两条需求都挂在这趟旅程的同一个停靠上 | PASS | `2 demands / 1 stop` | `2 demands / 1 stops` |
| 两条需求预留的仓位互不重叠 | PASS | `2 distinct` | `2 distinct of 2` |
| 装货阶段结束时记下了理由 | PASS | `a reason` | `NO_FURTHER_CARGO` |
| 持货钟在第一批装载闭环后开始走 | PASS | `a timestamp` | `2026-09-10 07:06:27.1219176+00:00` |
| journey 走到 Completed | PASS | `Completed` | `Completed` |
| 两条需求各自卸载一次，都提交 | PASS | `2 committed` | `2 committed of 2` |
| 两条需求终态都是 Succeeded | PASS | `2 Succeeded` | `2 Succeeded of 2` |
| 两条需求只占用一份车辆租约，收尾时释放 | PASS | `1 released lease` | `1 released of 1` |
| 两条需求全程只建两条 RIoT 单 | PASS | `2` | `2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
