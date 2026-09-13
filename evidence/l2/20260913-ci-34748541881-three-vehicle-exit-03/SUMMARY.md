# L2 场景证据：three-vehicle-exit

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260913T085314634Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `95294f9e0c36cea2bd48fc46b29c8a91e77a0f03` |
| fleet | `AGV-L2-001/BROKERX-L2-0001, AGV-L2-002/BROKERX-L2-0002, AGV-L2-003/BROKERX-L2-0003` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `9f22db825d52ad86c1d803bd0c1925dcc58d6793` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `a0e1deedb50419057dbe6aa7a7e8df983fb9ea901bbc452f97020ebf4743ef23` |
| protocolReleaseIdentity.schemaBundleSha256 | `885191e7a9e5da98a44f17f131756f9eb2033e7e11f13f4df965d4e35ac55685` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Temp\l2-20260913T085314634Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 三台车各自拿到一趟 journey | PASS | `AGV-L2-001,AGV-L2-002,AGV-L2-003` | `AGV-L2-001,AGV-L2-002,AGV-L2-003` |
| 三趟 journey 落在三个互不相同的需求上 | PASS | `3` | `3` |
| 三趟 journey 落在三把互不相同的 vehicleKey 上 | PASS | `3` | `3` |
| AGV-L2-001 的 TO_PICKUP 单已建并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| AGV-L2-002 的 TO_PICKUP 单已建并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| AGV-L2-003 的 TO_PICKUP 单已建并确认 | PASS | `CONFIRMED` | `CONFIRMED` |
| AGV-L2-001 到站取货、装载提交、出发前安全检查通过，进入关卡段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| AGV-L2-002 到站取货、装载提交、出发前安全检查通过，进入关卡段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| AGV-L2-003 到站取货、装载提交、出发前安全检查通过，进入关卡段 | PASS | `AwaitingGateArrival` | `AwaitingGateArrival` |
| AGV-L2-001 的装载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-002 的装载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-003 的装载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-001 的 journey 走到 Completed | PASS | `Completed` | `Completed` |
| AGV-L2-002 的 journey 走到 Completed | PASS | `Completed` | `Completed` |
| AGV-L2-003 的 journey 走到 Completed | PASS | `Completed` | `Completed` |
| AGV-L2-001 的卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-002 的卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| AGV-L2-003 的卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| 三个需求全部终态 Succeeded | PASS | `3 / 3` | `3 / 3` |
| 全程只建了六张 RIoT 单（三台车各取货一张、关卡一张） | PASS | `6` | `6` |
| 六张单绑在三把 vehicleKey 上，没有一张落到别的车头上 | PASS | `BROKERX-L2-0001,BROKERX-L2-0002,BROKERX-L2-0003` | `BROKERX-L2-0001,BROKERX-L2-0002,BROKERX-L2-0003` |
| 路网快照全程不是陈旧态（引擎在三车链路里真的工作） | PASS | `(无陈旧原因)` | `(null)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
