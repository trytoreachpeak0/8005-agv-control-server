# L2 场景证据：real-onboard-station-timeout-door-open

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260918T161320496Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-2` |
| controlServerCommit | `06b656880ce6a32c8250b4ad62ad8ad3ce11b936` |
| onboardHmiCommit | `9748c4187e74aeb46cf95557e8f2430e8fe2abf3` |
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
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260918T161320496Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车载端说在等操作员时，1 号仓门确实弹开了、空着、开锁输出已复位——操作员走开时的现场原样 | PASS | `OPEN/EMPTY/0/0` | `OPEN/EMPTY/0/0` |
| 服务端的安全投影里出现车辆自己算出的 LOCK_NOT_CLOSED，而会话仍是 Ready——开着的门由本车在途的装货解释 | PASS | `含 LOCK_NOT_CLOSED / Ready` | `["LOCK_NOT_CLOSED"] / Ready (READY)` |
| 期限未到：门开着也不挂告警，旅程原地等装货结果 | PASS | `读于期限前 / AwaitingLoadResult / 无阻断码` | `读于期限前 35.9 s / AwaitingLoadResult / ''` |
| 期限到期挂上 STATION_TIMEOUT_DOOR_NOT_CLOSED：开始时间不早于期限，stage 仍是 AwaitingLoadResult（不是 Blocked），装货仍在途，需求未被终结 | PASS | `AwaitingLoadResult / since >= 2026-09-18T16:14:26.8165212+00:00 / Prepared / Accepted` | `AwaitingLoadResult / since 2026-09-18 16:14:27.704977+00:00 / Prepared / Accepted` |
| 告警不是恢复：没有异常恢复会话、没有恢复工作流、没有 RecoveryRequired，会话仍是 Ready | PASS | `恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0 / Ready` | `恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0 / Ready (READY)` |
| 过期 30 秒后仍然如此：同一个告警码、开始时间不动，stage 仍是 AwaitingLoadResult，装货仍在途、车载端一份结果都没报，需求未终结，没有恢复，会话 Ready，门仍开着 | PASS | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-18 16:14:27.704977+00:00 / Prepared / 0 result / Accepted / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0 / Ready / OPEN` | `AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED since 2026-09-18 16:14:27.704977+00:00 / Prepared / 0 result / Accepted / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0 / Ready / OPEN/EMPTY/0/0` |
| 放料关门后车载端报 COMPLETED（整个 attempt 唯一一份结果），装货 Committed，仓位关门、锁上、有货 | PASS | `COMPLETED / Committed / CLOSED/OCCUPIED/1/0` | `COMPLETED / Committed / CLOSED/OCCUPIED/1/0` |
| 告警清掉，旅程离开 AwaitingLoadResult 往下走，需求仍在（没有被取消） | PASS | `非 STATION_TIMEOUT_DOOR_NOT_CLOSED / 非 AwaitingLoadResult / Accepted` | `'' / AwaitingStationDeparture / Accepted` |
| 全程没有进过恢复 | PASS | `恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` | `恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
