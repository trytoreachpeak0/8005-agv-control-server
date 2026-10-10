# L2 场景证据：staging-to-wire-reversed-journey

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162432238Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `3.0.0` |
| protocolReleaseIdentity.tag | `protocol-v3.0.0` |
| protocolReleaseIdentity.commit | `3f091cb2eae7c58cec54a95dd9389c9180bc7b4c` |
| protocolReleaseIdentity.protocolVersion | `4` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `d5e1a53f1fd61f105a890dc0267e1b0a9ac5ea49f713d2cf730b0f554df9db9e` |
| protocolReleaseIdentity.schemaBundleSha256 | `e435b2b14d9ccd60c89f07df909da7626fef056a6b8a2241087557fd7dc3df43` |
| protocolReleaseIdentity.vectorsSha256 | `be849f9749b004296ebd9e7bffa98faf2f8ffa90b63308ca3b210c68e7b8656e` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `SyntheticOnboard` |
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162432238Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 反向旅程的取货站是派工待送站，卸货站是需求 AREA 的机台站 | PASS | `pickup 305, drop-off 12` | `pickup 305, drop-off 12` |
| 反向旅程冻结的路线证据等于按这个方向独立重算出来的 id，把两端互换重算会得到另一个 | PASS | `route evidence = MAPCAT-70d1cd847c60826781ceb6f6c4a8af0949f65dda699af83c12909a666efad7c0 (swapped would be MAPCAT-938605c023d7d6618ef8ef781093e36de5ea5a2051ab9d67f560d04fe743b028)` | `route evidence MAPCAT-70d1cd847c60826781ceb6f6c4a8af0949f65dda699af83c12909a666efad7c0` |
| 计划快照先 TO_PICKUP 到派工待送站、后 TO_DROPOFF 到机台站，publicStationFunction 为空 | PASS | `1:TO_PICKUP@派工待送取货 2:TO_DROPOFF@N1-3_N1-7, no publicStationFunction` | `1:TO_PICKUP@派工待送取货 2:TO_DROPOFF@N1-3_N1-7; publicStationFunction: null,null` |
| STAGING_TO_WIRE 旅程先装后卸走到 Completed | PASS | `Completed` | `Completed` |
| 两条移动订单的目标站依次是派工待送站与机台站 | PASS | `305,12` | `305,12` |
| 清单快照：派工待送站 stopRole 为 PICKUP，机台站为 DROPOFF | PASS | `派工待送取货:PICKUP N1-3_N1-7:DROPOFF` | `派工待送取货:PICKUP N1-3_N1-7:DROPOFF` |
| 准入决策只冻结在卸货那次操作上，站点是机台站、任务类型 STAGING_TO_WIRE | PASS | `one decision on UnloadSlotOperationAttemptId: N1-3_N1-7/STAGING_TO_WIRE` | `1 decision(s): Unload:N1-3_N1-7/STAGING_TO_WIRE/1` |
| WIRE_TO_GATE 照常：机台站取货、关卡卸货、走完，准入冻结在装货那次操作上 | PASS | `pickup 12, drop-off 210, Completed, one decision on the load` | `pickup 12, drop-off 210, Completed, 1 decision(s): Load:N1-3_N1-7/WIRE_TO_GATE` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
