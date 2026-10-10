# L2 场景证据：g3-multi-stop-plan

结论：**FAIL**

失败原因：Timed out after 180s waiting for: the server issued the load command for B. Last observed: (nothing)

## 身份

| 项 | 值 |
| --- | --- |
| runId | `20261010T050242559Z` |
| agvId | `AGV-L2-001` |
| batchId | `unspecified` |
| controlServerCommit | `1f63fe0bb173f37c05dd5059c78455da57ed100b` |
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
| stageRoot | `C:\Users\szy\AppData\Local\Temp\l2-20261010T050242559Z` |
| vehicleKey | `BROKERX-L2-0001` |

## 判据

| 判据 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- |
| 追加之前：车载端确认了追加前最后一版计划（两条腿，车在 12 号站装完甲之后）之后，计划腿列表的行数与行序等于服务端那一版的 sequence（DISPLAY_FULL_JOURNEY_PLAN） | PASS | `两条腿，行序 1,2` | `服务端 plan r2 wire[1,2] 1:TO_PICKUP@N1-3_N1-7:ARRIVED:BUSINESS,2:TO_DROPOFF@关卡:PLANNED:BUSINESS / 界面 1(name)[1\|BUSINESS\|ARRIVED],2(name)[2\|BUSINESS\|PLANNED]` |
| 追加之后：车载端确认了三条腿那一版计划之后，计划腿列表的行数与行序等于服务端那一版的 sequence；站点刻意挑成按站名排会是 2,1,3，本地重排就红（DISPLAY_FULL_JOURNEY_PLAN、NEVER_REORDER_LEGS_LOCALLY） | PASS | `行序 1,2,3` | `服务端 plan r3 wire[1,2,3] 1:TO_PICKUP@N1-3_N1-7:ARRIVED:BUSINESS,2:TO_PICKUP@C15-13:PLANNED:BUSINESS,3:TO_DROPOFF@关卡:PLANNED:BUSINESS / 界面 1(name)[1\|BUSINESS\|ARRIVED],2(name)[2\|BUSINESS\|PLANNED],3(name)[3\|BUSINESS\|PLANNED]` |

## 目录内容

- `assertions.json` —— 机器可读的判据结论
- `timeline.jsonl` —— 一行一次判据翻转，只追加
- `logs/` —— 每个组件的 stdout 与 stderr
- `snapshots/` —— 收尾时各控制面与服务端数据库的快照

本次跑的是真 ControlServer + **真车载端 WPF** + **真 slots-simulator** + 假 RIoT + 假 MesIngest。
条码由 UI Automation 写进 `ScanTextBox` 并点「手动提交」，装卸货是真 Modbus IO 闭环。

L2 PASS 仍**不代表真实 RCS、真车、真实 IO 模块或接线合格**——模拟器只证明软件 IO 闭环。
见 `docs/RELEASE-CANDIDATE.md` 第 11 节。
