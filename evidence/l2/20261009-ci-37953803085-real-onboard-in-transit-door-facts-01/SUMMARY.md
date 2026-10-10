# L2 场景证据：real-onboard-in-transit-door-facts

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T162935820Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
| protocolFaultProxy | `True` |
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
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37953803085-1\_stage\l2-20261009T162935820Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 真车载端空车行驶 12 秒（会话因本单未就绪）：不 Hold、不急停、不 Cancel | PASS | `>= 10 samples, 0/0/0` | `24 samples, 0/0/0` |
| 真车载端有货行驶 12 秒并途中重连一次：不 Hold、不急停、不 Cancel | PASS | `>= 10 samples, 0/0/0` | `24 samples, 0/0/0` |
| 锁反馈变 0：Hold 恰好一条，打在本车、关卡段这一张单上，RIoT 侧收到的 CMD_ORDER_HELD 也打在这张 orderId 上 | PASS | `1 / AGV-L2-001 / W2G-46d2eb08-7b85-49ca-9698-b1a513c12b58-GATE-1 / -> ORDER-000002` | `1 / AGV-L2-001 / W2G-46d2eb08-7b85-49ca-9698-b1a513c12b58-GATE-1 / -> ORDER-000002` |
| 按不住、车还在动：急停恰好一条，打在这台车上，而且在 Hold 之后 | PASS | `1 / AGV-L2-001 / hold first` | `1 / AGV-L2-001 / hold 2026-10-09 16:31:31.8559678+00:00 trigger 2026-10-09 16:31:32.3368857+00:00` |
| 故障事实记的是门锁症状 | PASS | `VEHICLE_DOOR_NOT_PROVEN_LOCKED` | `VEHICLE_DOOR_NOT_PROVEN_LOCKED` |
| 锁反馈仍是 0 时不解除 | PASS | `0` | `0` |
| 锁反馈恢复后自动解除一次，原因记门锁原因消除 | PASS | `1 / EMERGENCY_DOOR_CAUSE_REMOVED` | `1 / EMERGENCY_DOOR_CAUSE_REMOVED` |
| 解除之后单仍停着：没有 CONTINUE，没有第二次急停，没有 Cancel | PASS | `0 continue / 1 trigger / 0 cancel` | `0 continue / 1 trigger / 0 cancel` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
