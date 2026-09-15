# L2 场景证据：auto-charge-endurance

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260915T030706706Z` |
| agvId | `AGV-L2-001` |
| batchId | `BATCH-3` |
| controlServerCommit | `786e1078a8a6cac85750322935294709cebe226b` |
| protocolReleaseIdentity.tag | `protocol-v0.3.0` |
| protocolReleaseIdentity.repositoryCommit | `345c53c58517968192c87c3e7777ed08ddb48726` |
| protocolReleaseIdentity.manifestSha256 | `b6c81ca9bb482986249411fcfc9169ac6b70b77388c63e43d581295eb02ba138` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260915T030706706Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 地图上有一个 211 号充电桩 | PASS | `station 211` | `11,12,210,211` |
| 第一单在电量充足时正常走完 | PASS | `Completed` | `Completed` |
| 第一单终态为 Succeeded | PASS | `Succeeded` | `Succeeded` |
| 电量充足时不会没事跑去充电 | PASS | `0` | `0` |
| 低电触发一趟去充电桩的行程 | PASS | `AwaitingChargerArrival / 211 / 15` | `AwaitingChargerArrival / 211 / 15` |
| 充电行程建出并确认了一条 TO_CHARGER 单 | PASS | `CONFIRMED / 211` | `CONFIRMED / 211` |
| 充电不产生旅程，也不占用需求 | PASS | `1` | `1` |
| 去充电桩的路上不会重复派单 | PASS | `1` | `1` |
| 充电单带开始充电动作 act(78,1,0)，完成后车才报 CHARGING | PASS | `act(78,1,0) / CHARGING` | `1 个开始充电动作 / CHARGING` |
| 到桩并确认在充电 | PASS | `Charging / 无阻塞原因` | `Charging /` |
| 充电占用的轮次不评估候选，解释在充电行程而不是 backlog 里 | PASS | `(无 backlog 记录)` | `(无 backlog 记录)` |
| 充电未达恢复线时不受理新需求 | PASS | `(无旅程)` | `` |
| 充电行程未到恢复线时不结束 | PASS | `Charging` | `Charging` |
| 充到恢复线后充电行程结束并记下释放电量 | PASS | `Completed / 80` | `Completed / 80` |
| 车仍然停在桩上并且仍报 CHARGING | PASS | `CHARGING / 211` | `CHARGING / 211` |
| 达到恢复线后等着的那一单立刻被受理 | PASS | `AwaitingPickupArrival` | `AwaitingPickupArrival` |
| 充电之后整条受理链路完好，第二单走到 Succeeded | PASS | `Completed / Succeeded` | `Completed / Succeeded` |
| 全程只去了一趟充电桩 | PASS | `1` | `1` |
| 全程五条 RIoT 单：两单各两段，加一条去充电桩 | PASS | `5` | `5` |
| 车载端报文逐条符合 protocol-v0.3.0 的 JSON Schema（合成对端发出的每一行） | PASS | `退出码 0，校验行数 > 0，未登记违约 0` | `退出码 0：Schema conformance (protocol-v0.3.0): 34 lines, 34 distinct, 9 message types, 0 distinct violations, 0 known; schema compilation 15191 ms.` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照
- `schema-conformance/` —— 车载端报文逐条过 protocol JSON Schema 的覆盖账与违约明细

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
