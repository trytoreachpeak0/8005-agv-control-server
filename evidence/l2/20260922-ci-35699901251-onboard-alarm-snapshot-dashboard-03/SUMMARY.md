# L2 场景证据：onboard-alarm-snapshot-dashboard

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260922T080658680Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-3` |
| controlServerCommit | `f2ddd40522432925580d6048dca2d6d54171a7a8` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `2.0.0` |
| protocolReleaseIdentity.tag | `protocol-v2.0.0` |
| protocolReleaseIdentity.commit | `86575456c847041515b7b75e8851a00e0d939804` |
| protocolReleaseIdentity.protocolVersion | `3` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `4ac095ad371d3aaa60d7c2e0198cfd64cff5f3068230fc3420e9cdf5616422a7` |
| protocolReleaseIdentity.schemaBundleSha256 | `9db0dbdc22fed7e39edf8d01b1fc40a12f5d70a7414f696f909ab2a87eb8c221` |
| protocolReleaseIdentity.vectorsSha256 | `391fa69a7d6e9f86ea139ba4c74eadf4994bf0a87e89d3dc5258dd7968d9182a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35699901251-1\_stage\l2-20260922T080658680Z-slot1` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 完整握手里报了一份告警快照，空的也报，服务端收下了 | PASS | `1 / []` | `1 / []` |
| 看板上这台车在线、告警为「无」，不是「尚未收到该车快照」 | PASS | `AGV-L2-001 无` | `AGV-L2-001  无` |
| 服务端收下的是整份快照，两条告警都在库里 | PASS | `L2_ALARM_VEHICLE_RELATED + L2_ALARM_FLEET_FIRST` | `[{"AlarmCode":"L2_ALARM_VEHICLE_RELATED","Severity":"WARNING","RaisedAt":"2026-09-22T08:07:14.5465823+00:00","Scope":"CurrentVehicle","Message":"\u4E0E\u8FD9\u53F0\u8F66\u76F4\u63A5\u76F8\u5173","DemandId":null,"StationId":null,"SlotOperationAttemptId":null,"PhysicalSlotNumber":null,"AlarmId":"69814713-b4fa-4833-8474-33bcf279f926"},{"AlarmCode":"L2_ALARM_FLEET_FIRST","Severity":"CRITICAL","RaisedAt":"2026-09-22T08:07:14.5465823+00:00","Scope":"Fleet","Message":"\u4E0E\u4EFB\u4F55\u4E00\u53F0\u8F66\u7684\u5F53\u4E0B\u90FD\u65E0\u5173","DemandId":null,"StationId":null,"SlotOperationAttemptId":null,"PhysicalSlotNumber":null,"AlarmId":"6a72cc99-49ea-4b0a-b2a3-c20a8ffc2444"}]` |
| 看板集中显示全部告警：与车直接相关的那条和与哪台车都无关的那条都看得到 | PASS | `L2_ALARM_FLEET_FIRST + L2_ALARM_VEHICLE_RELATED` | `AGV-L2-001  L2_ALARM_VEHICLE_RELATED、L2_ALARM_FLEET_FIRST` |
| 新快照整体取代旧快照：看板上只剩新的那条，旧的两条都不见了 | PASS | `L2_ALARM_FLEET_SECOND, not L2_ALARM_FLEET_FIRST` | `AGV-L2-001  L2_ALARM_FLEET_SECOND` |
| 一车一行，停在最新那一份的序号上 | PASS | `1 / 3` | `1 / 3` |
| 车失联时看板显示失联本身，不显示它失联前的最后一批告警 | PASS | `车辆失联, not L2_ALARM_FLEET_SECOND` | `AGV-L2-001  车辆失联` |
| 重连的完整握手重报当下的全量告警，服务端按新会话代采纳，看板恢复显示 | PASS | `L2_ALARM_FLEET_SECOND / generation 2` | `AGV-L2-001  L2_ALARM_FLEET_SECOND / generation 2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
