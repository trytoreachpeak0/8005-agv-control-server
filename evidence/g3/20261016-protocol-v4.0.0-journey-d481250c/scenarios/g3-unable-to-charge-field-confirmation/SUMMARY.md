# L2 场景证据：g3-unable-to-charge-field-confirmation

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T082535771Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `d481250c08d65b9b03ba854f22f563fb33bfa5aa` |
| onboardHmiCommit | `c9d24dfd6e96dc43781735cac209dc3976fe3a83` |
| protocolReleaseIdentity.repository | `8005-agv-protocol` |
| protocolReleaseIdentity.releaseVersion | `4.0.0` |
| protocolReleaseIdentity.tag | `protocol-v4.0.0` |
| protocolReleaseIdentity.commit | `3fb214e57d861a909d872511940d26133eb32dca` |
| protocolReleaseIdentity.protocolVersion | `5` |
| protocolReleaseIdentity.profileId | `AGV_FULL_PRODUCT` |
| protocolReleaseIdentity.manifestSha256 | `beca46a7fe98003ce1f21efb88f2c1406f3040883d0b372a51505a843e306f3a` |
| protocolReleaseIdentity.schemaBundleSha256 | `dad44a5e93203eb0b7ade50b23d12f2aaffc0e2abb3c8e5dcdd3de62d931d628` |
| protocolReleaseIdentity.vectorsSha256 | `24cc3a5e5023bc7ead70f609456136208a40c04c1ecd0982a7182880d8eaa0c7` |
| protocolReleaseIdentity.approvalStatus | `APPROVED_RELEASE` |
| rig | `RealOnboard` |
| slotsSimulatorCommit | `fb5f7c593742bf98bc3957b8729a38aad5321f28` |
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T082535771Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 车停在 211、单 HANG、开始充电的结果码不是 407802：旅程写 ORDER_HANG，没有任何暂停，周期仍 ACTIVE（系统没有自动确认，REQ-0175） | PASS | `ORDER_HANG \| (none) \| ACTIVE` | `ORDER_HANG \| (none) \| ACTIVE / ORDER_HANG \| (none) \| ACTIVE` |
| 车载端：充电用途、计划当前腿是 211 时「现场确认充不上」入口出现（「接不上充电」可按，没有不可用说明） | PASS | `offered=True \| notice=False` | `offered=True \| notice=False` |
| 车载端发 UnableToChargeFieldConfirmationRequested（L2-OPERATOR、计划里那条 CHARGER 腿的站点、CONNECTION_FAILED），服务端回 Result：CONFIRMED、problem 为空、chargingPolicyDecision=MANUAL_CHARGING_HOLD（名册只有 211、电量 25 低于最低余量 30） | PASS | `L2-OPERATOR / 充电点1 / CONNECTION_FAILED -> CONFIRMED / null / MANUAL_CHARGING_HOLD` | `L2-OPERATOR / 充电点1 / CONNECTION_FAILED -> CONFIRMED /  / MANUAL_CHARGING_HOLD` |
| Result 之后，业务状态 UNABLE_TO_CHARGE、CLEARING_MAINTENANCE 进发件箱并被车载端确认（VehicleBusinessStateSnapshot → SnapshotAppliedAck） | PASS | `after 10/16/2026 08:26:12 +00:00, acknowledged` | `10/16/2026 08:26:13 +00:00, acknowledged=True` |
| 清桩中的业务状态被车载端确认之后，界面结果一行 UnableToChargeStatus 在随后 3 秒里每次读都是 CONFIRMED（onboard-hmi#242 已修，预期为绿：用途转为 CLEARING_MAINTENANCE 时车载端保留结果） | PASS | `11 reads, all CONFIRMED` | `11 reads, 0 not CONFIRMED (first: -), 0 unreadable` |
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
