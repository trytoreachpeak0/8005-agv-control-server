# L2 场景证据：area-assignment-version-freeze

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T155531895Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-37953826544-1\_stage\l2-20261009T155531895Z-slot2` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：当前归属表是版本 1，只有 N1-3 → FRONT 一行 | PASS | `OK / version 1 / N1-3/MAP-25-WIRE_TO_GATE/FRONT` | `OK / version 1 / N1-3/MAP-25-WIRE_TO_GATE/FRONT` |
| 需求甲（2 花篮）在版本 1 下受理：目标仓全部属于 FRONT 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[1,2]（FRONT 组最小的可用仓，升序）` | `[1,2] on AGV-L2-001` |
| 需求甲的冻结行是版本 1 | PASS | `1` | `1` |
| 导入版本 2（N1-3 → REAR）成功 | PASS | `OK / version 2 / 1 entry` | `OK / version 2 / 1 entries` |
| 导入预览恰好列出在途的需求甲：原分组 FRONT（冻结版本 1）、新分组 REAR | PASS | `7914d173-0937-4aed-882b-bcc3bc71770b N1-3 v1 FRONT->REAR` | `7914d173-0937-4aed-882b-bcc3bc71770b N1-3 v1 FRONT->REAR` |
| 不停车生效之一：导入前后服务端是同一个进程（PID 与启动时刻都不变） | PASS | `pid 3124 ControlServer.Host started 2026-10-09T23:55:38.8795926+08:00` | `pid 3124 ControlServer.Host started 2026-10-09T23:55:38.8795926+08:00` |
| 不停车生效之二：导入前后车的会话代不变 | PASS | `1` | `1` |
| 不停车生效之三：导入时车在开往关卡的途中（RIoT 报 RUNNING、旅程在 AwaitingGateArrival），不需要空闲 | PASS | `procState RUNNING / AwaitingGateArrival` | `procState RUNNING / AwaitingGateArrival` |
| 需求甲走完：卸货命令的 slots 仍是受理时的目标仓（FRONT 组），没有按版本 2 改开 REAR | PASS | `Completed / [1,2]` | `Completed / 1 command(s), slots: [1,2]` |
| 需求甲走完之后，冻结行仍是版本 1 | PASS | `1` | `1` |
| 需求乙（2 花篮）在版本 2 下受理：目标仓全部属于 REAR 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |
| 需求乙的冻结行是版本 2 | PASS | `2` | `2` |
| 回滚：版本 1 的内容原样导入，形成新的版本 3（不是改回版本 1），内容与哈希都与版本 1 相同 | PASS | `OK / version 3 / N1-3/MAP-25-WIRE_TO_GATE/FRONT / sha256 8ab62504edb88283c1f89062bdec82316bfea9ff44c986b884a1ec00d5a5b50f` | `OK / version 3 / N1-3/MAP-25-WIRE_TO_GATE/FRONT / sha256 8ab62504edb88283c1f89062bdec82316bfea9ff44c986b884a1ec00d5a5b50f` |
| 回滚在导入审计里多记一条 | PASS | `3` | `3` |
| 回滚不改变已冻结的需求：需求乙仍冻结版本 2，回滚预览恰好列出在途的需求乙（REAR → FRONT） | PASS | `frozen 2 / 64d2dc10-0a34-47a8-bcf1-61401ca4f44d REAR->FRONT` | `frozen 2 / 64d2dc10-0a34-47a8-bcf1-61401ca4f44d REAR->FRONT` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
