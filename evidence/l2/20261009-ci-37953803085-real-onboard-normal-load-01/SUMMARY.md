# L2 场景证据：real-onboard-normal-load

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T154555992Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `563242d071de6f243c59f11722b197d683fee2a4` |
| onboardHmiCommit | `b9e67a538ba4cdf1916d201a08af40dd28270d14` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-37953803085-1\_stage\l2-20261009T154555992Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 真车载端在场时需求被受理并建出 TO_PICKUP 单 | PASS | `CONFIRMED` | `CONFIRMED` |
| UIA 录入的 sublot 经真车载端以 KEYBOARD 方式提交到服务端 | PASS | `L2-SUBLOT-20261009T154555992Z / KEYBOARD / L2-OPERATOR` | `L2-SUBLOT-20261009T154555992Z / KEYBOARD / L2-OPERATOR` |
| 车载端上报在等操作员时，它说要开的那个仓门确实是开的 | PASS | `slot 1 OPEN` | `slot 1 OPEN` |
| 装载走的是真 Modbus 闭环：车载端开锁、放货、关门、锁反馈回到 1、开锁输出复位 | PASS | `CLOSED/OCCUPIED/1/0` | `CLOSED/OCCUPIED/1/0` |
| 装载操作提交（Committed），且记的是 UIA 录进去的那个 sublot | PASS | `Committed / L2-SUBLOT-20261009T154555992Z` | `Committed / L2-SUBLOT-20261009T154555992Z` |
| 出发前安全检查通过后才建 TO_GATE 单 | PASS | `CONFIRMED` | `CONFIRMED` |
| 关卡卸的是装货时用的那个仓位 | PASS | `1` | `1` |
| 卸载后仓位回到空、门关、锁上 | PASS | `CLOSED/EMPTY/1/0` | `CLOSED/EMPTY/1/0` |
| journey 走到 Completed | PASS | `Completed` | `Completed` |
| 卸载操作提交（Committed） | PASS | `Committed` | `Committed` |
| 需求终态为 Succeeded | PASS | `Succeeded` | `Succeeded` |
| 全程只建了两条 RIoT 单（取货一条、关卡一条） | PASS | `2` | `2` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
