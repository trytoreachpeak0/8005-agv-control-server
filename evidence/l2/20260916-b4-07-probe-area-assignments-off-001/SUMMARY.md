# L2 场景证据：b4-07-probe-area-assignments-off

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260916T105746048Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `0bff08c7b23024262dbf33d0c5e9f582bbd1324b` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260916T105746048Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| AreaAssignments = $false：库里一版分区归属表都没有 | PASS | `0` | `0` |
| 入库与绑定照做：主车有 8 行已发布 IO 绑定，且 Context.SlotModelVersionId 已给出 | PASS | `8 / 非空` | `8 / 1d652d4dd45340eea2b46c1a2c293c61` |
| 时间线里有入库与绑定两步，没有导入那一步 | PASS | `seed + bind，无 import` | `slot-model-preseed:seed-approved-facts, slot-model-preseed:bind-io:AGV-L2-001` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
