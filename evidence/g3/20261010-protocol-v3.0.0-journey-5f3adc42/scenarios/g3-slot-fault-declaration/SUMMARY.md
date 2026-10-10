# L2 场景证据：g3-slot-fault-declaration

结论：**FAIL**

失败原因：The property 'declarationId' cannot be found on this object. Verify that the property exists.

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261009T164609324Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `5f3adc424e23cabffd4423d4eae1d2863720a9e2` |
| expectedActionOverdueThreshold | `00:00:20` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261009T164609324Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 服务端只在超时仓上受理判定：门槛之前判回 409 SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE、什么都不写；越过门槛后同一仓受理 | PASS | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` | `409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE` |
| 判定未结时装货照常结算：装货结果 COMPLETED、仓位操作 Committed，此刻判定仍 PENDING（SETTLE_OPERATION_RESULT_NORMALLY_WHILE_DECLARATION_PENDING） | PASS | `Committed / PENDING / COMPLETED` | `Committed / PENDING / COMPLETED` |
| 线上顺序与向量一致：补发的 SlotFaultDeclarationCommand（同一 messageId、新一代会话）→ SlotFaultDeclarationResult → DurableAck | PASS | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / 新一代` | `SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / gen 2 vs 1` |
| 车载端对已结的尝试回 NOT_APPLICABLE，problem.reasonCode = ACTION_NOT_ALLOWED_IN_STATE（REJECT_DECLARATION_ON_SETTLED_UNKNOWN_OR_SUPERSEDED_ATTEMPT） | PASS | `NOT_APPLICABLE / bc824f74-7a68-7e59-a12b-7e868626db9e / ACTION_NOT_ALLOWED_IN_STATE` | `{"declarationId":"eda117c7-f436-4d1e-8b09-843ea5247ffb","slotOperationAttemptId":"bc824f74-7a68-7e59-a12b-7e868626db9e","outcome":"NOT_APPLICABLE","problem":{"reasonCode":"ACTION_NOT_ALLOWED_IN_STATE","fieldPath":"payload.slotOperationAttemptId","displayMessage":"本次仓位操作已结算，结果已上报。"}}` |
| 服务端撤销判定、不改任何业务状态：判定 NOT_APPLICABLE 并记下原因，装货仍 Committed、需求仍 Accepted、旅程没有阻断 | PASS | `NOT_APPLICABLE / Committed / Accepted / not Blocked` | `NOT_APPLICABLE / Committed / Accepted / AwaitingStationDeparture` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
