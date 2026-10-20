# L2 场景证据：task-type-binding-station-reused-refuses-start

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261020T150611445Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `62e8cda37b05de3b1f3ad4b7db7e929776cd93c3` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `5.0.0` |
| protocolReleaseIdentity.tag | `protocol-v5.0.0` |
| protocolReleaseIdentity.commit | `18d4fb3495033624cab412a7ee784f675dfe4cc2` |
| protocolReleaseIdentity.protocolVersion | `6` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `dd973918cc9c4fdf28ee2ad2ec4ad522bdcc6f2b6ecaa4a4fa1c57a29f29f5aa` |
| protocolReleaseIdentity.schemaBundleSha256 | `d51abe44036f32b5b2c28fd1bea6ec7a89cf3b4dea0f0b1d6c9daaf52f350933` |
| protocolReleaseIdentity.vectorsSha256 | `291311b24bfcac75818ca9e4a888d436523e6ee14db661f6ba792ab8cc383e4a` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| protocolReleaseIdentity.source | `appsettings.json:ProtocolCandidate` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261020T150611445Z-slot6` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端进程以非零退出码结束 | PASS | `非零` | `-532462766` |
| /health/live 始终不通 | PASS | `从未答过` | `从未答过` |
| 服务端日志含 TASK_TYPE_STATION_REUSED | PASS | `TASK_TYPE_STATION_REUSED` | `[23:06:33 ERR] Task type station preset C:\Users\szy\AppData\Local\Temp\l2-20261020T150611445Z-slot6\task-type-stations.settings.json refused: TASK_TYPE_STATION_REUSED Station 关卡/210 is bound to 2 task types (STAGING_TO_WIRE, WIRE_TO_GATE); one station serves at most one task type.` |
| 拒绝点名 Station 关卡/210 与 WIRE_TO_GATE、STAGING_TO_WIRE | PASS | `关卡/210 + WIRE_TO_GATE + STAGING_TO_WIRE` | `[23:06:33 ERR] Task type station preset C:\Users\szy\AppData\Local\Temp\l2-20261020T150611445Z-slot6\task-type-stations.settings.json refused: TASK_TYPE_STATION_REUSED Station 关卡/210 is bound to 2 task types (STAGING_TO_WIRE, WIRE_TO_GATE); one station serves at most one task type.` |
| 规则版本与绑定集版本都没有落库 | PASS | `Rules=0 BindingSets=0` | `Rules=0 BindingSets=0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
