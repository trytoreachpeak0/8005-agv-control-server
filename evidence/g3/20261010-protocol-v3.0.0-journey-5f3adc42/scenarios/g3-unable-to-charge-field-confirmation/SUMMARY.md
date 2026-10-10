# L2 场景证据：g3-unable-to-charge-field-confirmation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T165850075Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `5f3adc424e23cabffd4423d4eae1d2863720a9e2` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261009T165850075Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车停在 211、单 HANG、开始充电的结果码不是 407802：旅程写 ORDER_HANG，没有任何暂停，周期仍 ACTIVE（系统没有自动确认，REQ-0175） | PASS | `ORDER_HANG \| (none) \| ACTIVE` | `ORDER_HANG \| (none) \| ACTIVE / ORDER_HANG \| (none) \| ACTIVE` |
| 车载端：充电用途、计划当前腿是 211 时「现场确认充不上」入口出现（「接不上充电」可按，没有不可用说明） | PASS | `offered=True \| notice=False` | `offered=True \| notice=False` |
| 车载端发 UnableToChargeFieldConfirmationRequested（L2-OPERATOR、计划里那条 CHARGER 腿的站点、CONNECTION_FAILED），服务端回 Result：CONFIRMED、problem 为空、chargingPolicyDecision=MANUAL_CHARGING_HOLD（名册只有 211、电量 25 低于最低余量 30） | PASS | `L2-OPERATOR / 充电点1 / CONNECTION_FAILED -> CONFIRMED / null / MANUAL_CHARGING_HOLD` | `L2-OPERATOR / 充电点1 / CONNECTION_FAILED -> CONFIRMED /  / MANUAL_CHARGING_HOLD` |
| Result 之后，业务状态 UNABLE_TO_CHARGE、CLEARING_MAINTENANCE 进发件箱并被车载端确认（VehicleBusinessStateSnapshot → SnapshotAppliedAck） | PASS | `after 10/09/2026 16:59:36 +00:00, acknowledged` | `10/09/2026 16:59:37 +00:00, acknowledged=True` |
| 清桩中的业务状态被车载端确认之后，界面结果一行 UnableToChargeStatus 在随后 3 秒里每次读都是 CONFIRMED（已知红，等 onboard-hmi#242：用途转为 CLEARING_MAINTENANCE 时车载端清掉了结果） | PASS | `12 reads, all CONFIRMED` | `12 reads, 0 not CONFIRMED (first: '-')` |
| 一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停（确认人 L2-OPERATOR、R-11、现场处置 CONNECTION_FAILED）；周期 UNABLE_TO_CHARGE／CLEARING；人工充电等待 UNABLE_TO_CHARGE_LOW_BATTERY；判定记下一行（DECIDE_CHARGING_POLICY_CENTRALLY、RECORD_FIELD_OBSERVATION） | PASS | `UNABLE_TO_CHARGE_CONFIRMED L2-OPERATOR/R-11/CONNECTION_FAILED \| UNABLE_TO_CHARGE/CLEARING \| UNABLE_TO_CHARGE_LOW_BATTERY \| 1 decided` | `UNABLE_TO_CHARGE_CONFIRMED L2-OPERATOR/R-11/CONNECTION_FAILED \| UNABLE_TO_CHARGE/CLEARING \| UNABLE_TO_CHARGE_LOW_BATTERY \| 1 decided` |
| 车保持原位：RIoT 上恰好一张充电单（duplicate-riot-order），没有任何订单命令，211 仍是这一趟的 | PASS | `1 orders \| 0 commands \| True` | `1 orders \| 0 commands \| True` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
