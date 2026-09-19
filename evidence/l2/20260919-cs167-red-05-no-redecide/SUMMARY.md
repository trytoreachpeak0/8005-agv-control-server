# L2 场景证据：real-onboard-expected-action-overdue

结论：**FAIL**

失败原因：Timed out after 90s waiting for: the stop passed its deadline with the door open. Last observed: {"DemandId":"91c3ab53-934b-4d83-9b38-9d8deae5afeb","Stage":"AwaitingLoadResult","AgvId":"AGV-L2-001","VehicleKey":"BROKERX-L2-0001","AgvLifecycleGeneration":1,"MapId":25,"MapIdentity":"MAP-L2","DispatchZone":"MAP-25-WIRE_TO_GATE","RouteEvidenceId":"MAPCAT-211ca7624e8c3bf810bfe2e2c959b5c9016030d343fa634b988a4b64aa273d6f","PickupStationId":"N1-3_N1-7","PickupStationRiotId":12,"GateStationId":"关卡","GateStationRiotId":210,"ExpectedBasketCount":1,"TargetSlotsJson":"[1]","OperationSessionId":"e232d54f-72b3-185e-b99c-d9dc7bcab2cc","PickupMovementLegId":"26b9e870-94a7-5d58-8bb2-36ba6b78c99a","PickupUpperId":"W2G-91c3ab53-934b-4d83-9b38-9d8deae5afeb-PICKUP-1","GateMovementLegId":"beba682a-4060-4756-887b-abb2fe8ace00","GateUpperId":"W2G-91c3ab53-934b-4d83-9b38-9d8deae5afeb-GATE-1","DispatchGeneration":1,"VehicleBusinessRevision":1,"WorklistRevision":1,"PlanRevision":1,"VehicleBusinessMessageId":"f8d5f975-1de5-5352-8768-b035711fe457","WorklistMessageId":"6ec8eda9-3fcf-995c-90b6-1a99ba8a1981","PlanMessageId":"b4ca12d0-4916-6958-9440-94819fe576f7","SublotRequestMessageId":"b183be05-40a7-b35a-af8b-f78f156f8c38","LoadCommandMessageId":"0ca45c09-65c7-6c51-abf7-4ba8c0fb46ec","LoadSlotOperationAttemptId":"c77a12ec-8810-9a54-b760-506f89c155f9","PreDepartureSafetyCheckMessageId":"7f10184d-bfa8-4853-8884-0cbef972c44d","PreDepartureSafetyCheckId":"04fe37c8-df73-895e-9200-f608293e06e0","GateVehicleBusinessMessageId":"720405f1-e2b2-a058-8367-824739498f85","GateWorklistMessageId":"dec77d5a-69a3-3157-b71c-62ad8cdd29f3","GatePlanMessageId":"49ab2c04-2164-6d5a-b7eb-6b90f4aac066","UnloadCommandMessageId":"01ca8281-3ced-0c5f-8dad-42be79bc21eb","UnloadSlotOperationAttemptId":"7d4b0dd5-8a80-0359-b624-ba121209d306","ConsumedSublotMessageId":"84310f5c-0554-420e-98b8-1718ae4f746d","ConsumedSafetyResultMessageId":null,"BlockReasonCode":"ONBOARD_SESSION_NOT_READY","CreatedAt":"2026-09-19 09:08:11.8755538+00:00","UpdatedAt":"2026-09-19 09:10:38.601357+00:00","StationDepartureWaitStartedAt":null,"BlockReasonSince":"2026-09-19 09:08:34.6036508+00:00"}

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20260919T090723319Z` |
| agvId | `AGV-L2-001` |
| batchId | `batch-6` |
| controlServerCommit | `0004cdf99414a538940beab194541abb3b627ec8` |
| expectedActionOverdueThreshold | `00:00:20` |
| onboardHmiCommit | `7ded1b703bd57a641aa8587b5433647b4fefaca4` |
| protocolFaultProxy | `True` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20260919T090723319Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 空关后车自己重开：这次装货的 UNLOCKING 进度 ≥ 2 条，1 号仓门又弹开、空着、开锁输出复位，重开早于门槛 | PASS | `UNLOCKING ≥ 2 / OPEN/EMPTY/0/0 / 重开早于门槛` | `UNLOCKING 2 / OPEN/EMPTY/0/0 / 重开于第一次开锁后 8.9 s` |
| 门槛之前没有超时告警：最新一份告警快照里没有这个码，端点 slots 为空，HMI 上 ExpectedActionOverdue 不在 UIA 树里，服务端没要过中途快照；读完仍在门槛之前 | PASS | `告警 0 / 端点 0 行 / HMI 无 / 请求 0 / 读于门槛前` | `告警 0 / 端点 0 行 / HMI 无 / 请求 0 / 读于门槛前 9.8 s` |
| 越过门槛后告警出现且字段正确：code、subjectType=SLOT、subjectId=1、displayMessage 是三种期待动作之一；raisedAt ≈ 第一次开锁 + 门槛（±2 s），不 ≈ 重开 + 门槛 | PASS | `SLOT_EXPECTED_ACTION_OVERDUE / SLOT / 1 / 三种期待动作之一 / 距第一次开锁 + 门槛 ≤ 2 s / 距重开 + 门槛 > 2 s` | `SLOT_EXPECTED_ACTION_OVERDUE / SLOT / 1 / '放入货物并关好1号仓门' / 距第一次开锁 + 门槛 -0.01 s / 距重开 + 门槛 -8.87 s` |
| 线上：这份告警快照之后，同一连接上出现 server->onboard:SafetyStateSnapshotRequested，随后 onboard->server:SafetyStateSnapshot；到此只有一条连接 | PASS | `告警、请求、快照同一连接 / 1 条连接` | `告警 #1 请求 #1 快照 #1 / 1 条连接` |
| 中途快照被采纳、不把会话打回握手：同一代次 1 有第二份 SafetyStateSnapshot，版本大于握手那份（1），回应 SnapshotAppliedAck + SessionReadiness；会话仍 Ready、不是 HANDSHAKE_INCOMPLETE；没有恢复痕迹 | FAIL | `v > 1 / SnapshotAppliedAck,SessionReadiness / gen 1 Ready / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` | `v10 / SnapshotAppliedAck / gen 1 / RecoveryRequired / HANDSHAKE_INCOMPLETE / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` |
| 端点一行、字段对得上：thresholdSeconds = 20；slots 恰好一行，车、仓、取货站、LOAD、期待动作与 raisedAt 同告警，waitedSeconds ≥ 门槛，站点期限未过 | FAIL | `threshold 20 / 1 行 / AGV-L2-001 slot 1 N1-3_N1-7 LOAD '放入货物并关好1号仓门' raisedAt 2026-09-19T09:08:33.7987507+00:00 waited ≥ 20 stationTimeout=False` | `threshold 20 / 0 行 / (no row)` |
| 读数来自中途快照且与模拟器一致：readings 非空，版本 = 中途那份（v10，不是握手的 v1），锁／光幕／开锁输出 = 模拟器此刻（UNLOCKED/EMPTY/RESET），changedSinceObserved = false | FAIL | `v10 UNLOCKED/EMPTY/RESET changed=False（模拟器 UNLOCKED/EMPTY/RESET）` | `readings null` |
| 看板页出卡片：「期待动作超时」卡片门槛前写「无期待动作超时的仓位」，越过门槛后有这台车、这个仓、装货与期待动作的一行 | FAIL | `门槛前「无期待动作超时的仓位」/ 门槛后「AGV-L2-001 N1-3_N1-7 装货 1 放入货物并关好1号仓门」` | `门槛前 '期待动作超时 门槛 0 分 20 秒：当前仓自第一次开锁起累计等待达到门槛仍未闭环时由车载端上报；只让人看到，班组长或管理员到现场查看。 无期待动作超时的仓位' / 门槛后 '期待动作超时 门槛 0 分 20 秒：当前仓自第一次开锁起累计等待达到门槛仍未闭环时由车载端上报；只让人看到，班组长或管理员到现场查看。 无期待动作超时的仓位 AGV-L2-001：车辆失联，期待动作超时状态不明'` |
| HMI 提示「已上报」：ExpectedActionOverdue 在 UIA 树里，文字含告警的 displayMessage 与「已上报」，不是断开文案 | PASS | `含 '放入货物并关好1号仓门' 与 '已上报'` | `'期待的操作（放入货物并关好1号仓门）很久没有完成，已上报，班组长或管理员会到现场查看'` |
| 上报不改变行为（守护判据）：告警在时这次装货没有结果，最新进度仍是开锁或等操作员，旅程仍 AwaitingLoadResult，期限起点不变，没有恢复痕迹 | FAIL | `0 result / UNLOCKING 或 WAITING_OPERATOR / AwaitingLoadResult / 起点 2026-09-19 09:08:12.7054595+00:00 / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` | `0 result / WAITING_OPERATOR / AwaitingLoadResult / 起点  / 恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0` |
| 同一超时再报不出第二行、不覆盖第一行：再空关、重开之后，每份告警快照里这一仓的超时都是同一个 alarmId、同一个 raisedAt；端点仍恰好一行，raisedAt 不变 | FAIL | `重开 OPEN/EMPTY/0/0 / 1 个 alarmId / 1 个 raisedAt / 端点 1 行 raisedAt 2026-09-19T09:08:33.7987507+00:00` | `重开 OPEN/EMPTY/0/0 / 1 份快照带它，1 个 alarmId、1 个 raisedAt / 端点 0 行 (no row)` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
