# L2 场景证据：staging-to-wire-slot-group

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162746339Z` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T162746339Z-slot3` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| N1-3 需求在派工待送站取货、到机台站卸货，恰好占 3 个目标仓 | PASS | `pickup 305, drop-off 12, 3 target slots` | `pickup 305, drop-off 12, ExpectedBasketCount 3, [5,6,7]` |
| N1-3 指 REAR：目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 3 个可用仓 | PASS | `[5,6,7]（REAR 组最小的可用仓，升序）` | `[5,6,7] on AGV-L2-001` |
| N1-3 需求的旅程走到 Completed | PASS | `Completed` | `Completed` |
| N1-3：派工待送站下发给车的 LOAD 命令 slots 与旅程目标仓相同 | PASS | `[5,6,7]` | `1 command(s), slots: [5,6,7]` |
| N1-3：机台站下发给车的 UNLOAD 命令 slots 与旅程目标仓相同 | PASS | `[5,6,7]` | `1 command(s), slots: [5,6,7]` |
| N1-7 需求在派工待送站取货、到机台站卸货，恰好占 3 个目标仓 | PASS | `pickup 305, drop-off 12, 3 target slots` | `pickup 305, drop-off 12, ExpectedBasketCount 3, [1,2,3]` |
| N1-7 指 FRONT：目标仓全部属于本车 FRONT 组、升序，且恰好是该组编号最小的 3 个可用仓 | PASS | `[1,2,3]（FRONT 组最小的可用仓，升序）` | `[1,2,3] on AGV-L2-001` |
| N1-7 需求的旅程走到 Completed | PASS | `Completed` | `Completed` |
| N1-7：派工待送站下发给车的 LOAD 命令 slots 与旅程目标仓相同 | PASS | `[1,2,3]` | `1 command(s), slots: [1,2,3]` |
| N1-7：机台站下发给车的 UNLOAD 命令 slots 与旅程目标仓相同 | PASS | `[1,2,3]` | `1 command(s), slots: [1,2,3]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
