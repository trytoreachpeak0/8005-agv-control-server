# L2 场景证据：onboard-alarm-snapshot-dashboard

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260912T112145328Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-3` |
| controlServerCommit | `17cf478323f9c97e4aaea3f7c7a3cef732908249` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `16e2567a7033883f00fc999f7fa08f954dd13a26` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `25fd6689e8234b7d481874b408109cd27eb0f02fbb023225385d6642e9bfd3d0` |
| protocolReleaseIdentity.schemaBundleSha256 | `225a83340eb5f27c4e6dfd7bf8aba8007cf787d29f1df860deaf0ba039baf3ff` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Temp\l2-20260912T112145328Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 完整握手里报了一份告警快照，空的也报，服务端收下了 | PASS | `1 / []` | `1 / []` |
| 看板上这台车在线、告警为「无」，不是「尚未收到该车快照」 | PASS | `AGV-L2-001 无` | `AGV-L2-001  无` |
| 服务端收下的是整份快照，两条告警都在库里 | PASS | `L2_ALARM_VEHICLE_RELATED + L2_ALARM_FLEET_FIRST` | `[{"AlarmCode":"L2_ALARM_VEHICLE_RELATED","Severity":"WARNING","RaisedAt":"2026-09-12T11:21:56.0374943+00:00","Scope":"CurrentVehicle","Message":"\u4E0E\u8FD9\u53F0\u8F66\u76F4\u63A5\u76F8\u5173","DemandId":null,"StationId":null,"SlotOperationAttemptId":null,"PhysicalSlotNumber":null,"AlarmId":"039a8937-7b07-493d-a142-1716f2fa609a"},{"AlarmCode":"L2_ALARM_FLEET_FIRST","Severity":"CRITICAL","RaisedAt":"2026-09-12T11:21:56.0374943+00:00","Scope":"Fleet","Message":"\u4E0E\u4EFB\u4F55\u4E00\u53F0\u8F66\u7684\u5F53\u4E0B\u90FD\u65E0\u5173","DemandId":null,"StationId":null,"SlotOperationAttemptId":null,"PhysicalSlotNumber":null,"AlarmId":"3ad4fdf6-276c-4fd9-aad9-b441736c4095"}]` |
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
