# L2 场景证据：area-assignment-version-freeze

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T151511259Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-4` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server\_work\_temp\l2-35361077376-1\_stage\l2-20260918T151511259Z-slot4` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 前置：当前归属表是版本 1，只有 N1-3 → FRONT 一行 | PASS | `OK / version 1 / N1-3/MAP-25-WIRE_TO_GATE/FRONT` | `OK / version 1 / N1-3/MAP-25-WIRE_TO_GATE/FRONT` |
| 需求甲（2 花篮）在版本 1 下受理：目标仓全部属于 FRONT 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[1,2]（FRONT 组最小的可用仓，升序）` | `[1,2] on AGV-L2-001` |
| 需求甲的冻结行是版本 1 | PASS | `1` | `1` |
| 导入版本 2（N1-3 → REAR）成功 | PASS | `OK / version 2 / 1 entry` | `OK / version 2 / 1 entries` |
| 导入预览恰好列出在途的需求甲：原分组 FRONT（冻结版本 1）、新分组 REAR | PASS | `dc196794-0355-4eed-9881-7738e27adc78 N1-3 v1 FRONT->REAR` | `dc196794-0355-4eed-9881-7738e27adc78 N1-3 v1 FRONT->REAR` |
| 不停车生效之一：导入前后服务端是同一个进程（PID 与启动时刻都不变） | PASS | `pid 6712 ControlServer.Host started 2026-09-18T23:15:15.6660385+08:00` | `pid 6712 ControlServer.Host started 2026-09-18T23:15:15.6660385+08:00` |
| 不停车生效之二：导入前后车的会话代不变 | PASS | `1` | `1` |
| 不停车生效之三：导入时车在开往关卡的途中（RIoT 报 RUNNING、旅程在 AwaitingGateArrival），不需要空闲 | PASS | `procState RUNNING / AwaitingGateArrival` | `procState RUNNING / AwaitingGateArrival` |
| 需求甲走完：卸货命令的 slots 仍是受理时的目标仓（FRONT 组），没有按版本 2 改开 REAR | PASS | `Completed / [1,2]` | `Completed / 1 command(s), slots: [1,2]` |
| 需求甲走完之后，冻结行仍是版本 1 | PASS | `1` | `1` |
| 需求乙（2 花篮）在版本 2 下受理：目标仓全部属于 REAR 组、升序，且恰好是该组编号最小的 2 个可用仓 | PASS | `[5,6]（REAR 组最小的可用仓，升序）` | `[5,6] on AGV-L2-001` |
| 需求乙的冻结行是版本 2 | PASS | `2` | `2` |
| 回滚：版本 1 的内容原样导入，形成新的版本 3（不是改回版本 1），内容与哈希都与版本 1 相同 | PASS | `OK / version 3 / N1-3/MAP-25-WIRE_TO_GATE/FRONT / sha256 8ab62504edb88283c1f89062bdec82316bfea9ff44c986b884a1ec00d5a5b50f` | `OK / version 3 / N1-3/MAP-25-WIRE_TO_GATE/FRONT / sha256 8ab62504edb88283c1f89062bdec82316bfea9ff44c986b884a1ec00d5a5b50f` |
| 回滚在导入审计里多记一条 | PASS | `3` | `3` |
| 回滚不改变已冻结的需求：需求乙仍冻结版本 2，回滚预览恰好列出在途的需求乙（REAR → FRONT） | PASS | `frozen 2 / eafa0fc2-5e59-4d19-b3fc-39fe23208a91 REAR->FRONT` | `frozen 2 / eafa0fc2-5e59-4d19-b3fc-39fe23208a91 REAR->FRONT` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

L2 PASS 只证明服务端在假 RIoT、假 MesIngest 与合成车载端下的跨端时序，
**不代表真实 RCS、真车、真实 IO 模块或接线合格**。
