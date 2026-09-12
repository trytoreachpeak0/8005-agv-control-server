# L2 场景证据：onboard-alarm-snapshot-dashboard

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260910T084604242Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-3` |
| controlServerCommit | `eefb3a8802664623fdde9dd7bdc759ea5b61a5b0` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `1.0.0` |
| protocolReleaseIdentity.tag | `protocol-v1.0.0` |
| protocolReleaseIdentity.commit | `f6ee75defe6e2d18f63f4082bee445dbb678ab1b` |
| protocolReleaseIdentity.protocolVersion | `2` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `84f984eabf17106e92666c415b63100d404e9ec69a9a710dfddf17683cc42788` |
| protocolReleaseIdentity.schemaBundleSha256 | `71146c881e8ec199e9a977779ec1a557bed96a9ab71e36cfc3dfb7b329351c6b` |
| protocolReleaseIdentity.vectorsSha256 | `51c5aaca2ca02326d16e02af7e76c9954d84414a9772c5b208a92969a417d1df` |
| protocolReleaseIdentity.approvalStatus | `SUPERSEDING_CANDIDATE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260910T084604242Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 完整握手里报了一份告警快照，空的也报，服务端收下了 | PASS | `1 / []` | `1 / []` |
| 看板上这台车在线、告警为「无」，不是「尚未收到该车快照」 | PASS | `AGV-L2-001 无` | `AGV-L2-001  无` |
| 服务端收下的是整份快照，两条告警都在库里 | PASS | `L2_ALARM_VEHICLE_ONLY + L2_ALARM_FLEET_FIRST` | `[{"AlarmCode":"L2_ALARM_VEHICLE_ONLY","Severity":"WARNING","RaisedAt":"2026-09-10T08:46:37.6111304+00:00","Scope":"CurrentVehicle","Message":"\u53EA\u8BE5\u51FA\u73B0\u5728\u8F66\u4E0A","DemandId":null,"StationId":null,"SlotOperationAttemptId":null,"PhysicalSlotNumber":null,"AlarmId":"8f5816fb-6f0e-40c0-9ae5-441c12eff5b9"},{"AlarmCode":"L2_ALARM_FLEET_FIRST","Severity":"CRITICAL","RaisedAt":"2026-09-10T08:46:37.6111304+00:00","Scope":"Fleet","Message":"\u4E0E\u4EFB\u4F55\u4E00\u53F0\u8F66\u7684\u5F53\u4E0B\u90FD\u65E0\u5173","DemandId":null,"StationId":null,"SlotOperationAttemptId":null,"PhysicalSlotNumber":null,"AlarmId":"00a21db2-b132-400b-b84c-f83d1bc40f0c"}]` |
| 看板上看得到进看板的那条，看不到只该出现在车上的那条 | PASS | `L2_ALARM_FLEET_FIRST, not L2_ALARM_VEHICLE_ONLY` | `AGV-L2-001  L2_ALARM_FLEET_FIRST` |
| 新快照整体取代旧快照：看板上只剩新的那条，旧的那条不见了 | PASS | `L2_ALARM_FLEET_SECOND, not L2_ALARM_FLEET_FIRST` | `AGV-L2-001  L2_ALARM_FLEET_SECOND` |
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
