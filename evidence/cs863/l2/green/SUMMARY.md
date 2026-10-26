# L2 场景证据：charging-order-not-dispatched-holds-charger

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261026T081041685Z` |
| agvId | `AGV-L2-001` |
| batchId | `program-210` |
| controlServerCommit | `95d6f0f1f91ee5beae847b4c45b8429ec21153cd` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `6.0.0` |
| protocolReleaseIdentity.tag | `protocol-v6.0.0` |
| protocolReleaseIdentity.commit | `98f172642937994ed01668d81fe41e69cf089e10` |
| protocolReleaseIdentity.protocolVersion | `7` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a52876be2ddcc9b8cdd1f1e3b7b7ddbe70d31a307f1d3f017d7a1eb95ffd77b1` |
| protocolReleaseIdentity.schemaBundleSha256 | `0dab682174e2d3e4f931b3c0e87c7fa55bd2cb3395356d7845e595d8bfb1c795` |
| protocolReleaseIdentity.vectorsSha256 | `e06ca9ea1d4445cd7d70fc190b0cf606a8e33cb7760d848c0270df24e2594c91` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261026T081041685Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：主车 20% 取得 211 的预占与 CHARGING 用途，它的充电单 RIoT 收下、停在 1 QUEUEING 没派车（executeVehicleKey --）；第二台 25% 排队，报「211 已被预占」 | PASS | `reported / RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261026T081118194Z / CHARGING charging:BROKERX-L2-0001:20261026T081118194Z / (none) / 1 --` | `reported / RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261026T081118194Z / CHARGING charging:BROKERX-L2-0001:20261026T081118194Z / (none) / 1 --` |
| 单一直不派车，30 秒窗口过后：主车那趟充电写上 CHARGING_ORDER_NOT_DISPATCHED、服务端日志告警一次（事件 2284）；211 仍是主车的预占，RIoT 上那张单仍是 1 QUEUEING、没有执行车，命令审计里没有任何订单命令（不取消、不放桩），第二台仍分不到 | PASS | `CHARGING_ORDER_NOT_DISPATCHED \| 1 alarms \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261026T081118194Z \| 1 -- \| 0 order commands \| second (none)` | `CHARGING_ORDER_NOT_DISPATCHED \| 1 alarms \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261026T081118194Z \| 1 -- \| 0 order commands \| second (none)` |
| 主车在 RIoT 掉线、人在 RIoT 取消那张单：服务端照取消走（ORDER_ENDED_WITHOUT_ARRIVAL、不重建），车证明不了停稳，211 仍预占；L2-R11 确认清桩：CONFIRMED、stationReleased=true；之后第二台取得 211 的预占与 CHARGING 用途。主车的充电单只建过一张 | PASS | `ORDER_ENDED_WITHOUT_ARRIVAL \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261026T081118194Z / CONFIRMED released=1 / RESERVED BROKERX-L2-0002 \| CHARGING / 1 main intents, 0 rebuilds` | `ORDER_ENDED_WITHOUT_ARRIVAL \| RESERVED BROKERX-L2-0001 charging:BROKERX-L2-0001:20261026T081118194Z / CONFIRMED released=1 / RESERVED BROKERX-L2-0002 \| CHARGING / 1 main intents, 0 rebuilds` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
