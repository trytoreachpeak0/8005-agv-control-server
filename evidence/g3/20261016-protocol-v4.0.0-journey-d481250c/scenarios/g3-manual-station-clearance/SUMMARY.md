# L2 场景证据：g3-manual-station-clearance

结论：**PASS**

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261016T082500410Z` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261016T082500410Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 到桩开始充电失败（407802、HANG）：一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停；清桩中的计划（恰好一条 CHARGER 腿）与业务状态（UNABLE_TO_CHARGE、CLEARING_MAINTENANCE）都被真车载端确认 | PASS | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 / plan [CHARGER@充电点1] acked / state acked` | `UNABLE_TO_CHARGE_CONFIRMED recovered=0 / plan True / state True` |
| 车载端：界面 ChargingStatus 报 UNABLE_TO_CHARGE，「确认清桩」入口出现（从计划里那条 CHARGER 腿取到了原充电桩） | PASS | `UNABLE_TO_CHARGE \| offered=True` | `UNABLE_TO_CHARGE \| offered=True` |
| 车载端发 ManualStationClearanceConfirmationRequested（操作员 L2-OPERATOR、publicStationFunction 为空、STATION_EMPTY、站点是那条 CHARGER 腿的站点），服务端回 Result：CONFIRMED、problem 为空、stationReleased=true；操作员被告知：操作记录里出现「服务端已确认清桩…站点已释放」、车载端日志记下本次确认 Confirmed／stationReleased=True、「确认清桩」入口收起（不读结果那一行：清桩一结束它就清空，control-server#412） | PASS | `L2-OPERATOR / null / STATION_EMPTY / 充电点1 -> CONFIRMED / null / true \| record shown \| outcome=Confirmed stationReleased=True \| entry gone` | `L2-OPERATOR /  / STATION_EMPTY / 充电点1 -> CONFIRMED /  / True \| record shown \| outcome=Confirmed stationReleased=True \| entry gone` |
| 211 的独占以 CHARGER_RELEASED_ON_MANUAL_CLEARANCE 释放，暂停没有恢复行，旅程以 CHARGING_UNABLE_TO_CHARGE_CLEARED 收尾；RIoT 上恰好一张充电单（RELEASE_STATION_ONLY_ON_CONFIRMED_CLEARANCE） | PASS | `(none) \| CHARGER_RELEASED_ON_MANUAL_CLEARANCE \| UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED \| 1 orders` | `(none) \| CHARGER_RELEASED_ON_MANUAL_CLEARANCE \| UNABLE_TO_CHARGE_CONFIRMED recovered=0 \| Completed CHARGING_UNABLE_TO_CHARGE_CLEARED \| 1 orders` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
