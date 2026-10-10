# L2 场景证据：real-onboard-charging-cycle

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T071520867Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-8` |
| controlServerCommit | `6d56e90645f3fb2133c16c1f0c39c6c4fcab8ec4` |
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
| stageRoot | `C:\actions-runner\win11-01-control-server-desktop\_work\_temp\real-rig-38032912362-1\_stage\l2-20261010T071520867Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 低电：一条 CHARGER、ACTIVE 腿的计划与 activePurpose=CHARGING 的业务状态都到了真车载端并被确认 | PASS | `plan acknowledged / CHARGING acknowledged` | `plan acknowledged / CHARGING acknowledged` |
| 到桩充电：界面 ChargingStatus 报 CHARGING；车停在桩上那十秒里每次读车载端都不能提交 | PASS | `CHARGING / CanSubmit False` | `CHARGING / CanSubmit False` |
| 充满：界面 ChargingStatus 报 COMPLETE；之后十秒里 211 仍是这一趟的占用、RIoT 上仍只有那一张充电单（不为离桩建单）、车载端仍不能提交 | PASS | `COMPLETE / OCCUPIED charging:BROKERX-L2-0001:20261010T071550309Z \| 1 orders \| CanSubmit False` | `COMPLETE / OCCUPIED charging:BROKERX-L2-0001:20261010T071550309Z \| 1 orders \| CanSubmit False` |
| 充满的车被派走：被确认的最新一版计划不含 CHARGER 腿；车到 12 号站，车载端能录入、装货 Committed（充满离桩接走后，在取货站能录入） | PASS | `0 CHARGER legs / load Committed` | `0 CHARGER legs of 2 / load Committed` |
| 车离开 211 之后：211 以 CHARGER_RELEASED_ON_DEPARTURE 释放，界面 ChargingStatus 回 NOT_CHARGING；卸货之后需求 Succeeded | PASS | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| NOT_CHARGING / Succeeded` | `(none) \| CHARGER_RELEASED_ON_DEPARTURE \| NOT_CHARGING / Succeeded` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
